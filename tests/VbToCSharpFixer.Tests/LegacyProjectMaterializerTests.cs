using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class LegacyProjectMaterializerTests
{
    private string _root = null!;
    private string _projectPath = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "materializer-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(_root, "LegacyApp");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "My Project"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "lib"));
        Directory.CreateDirectory(Path.Combine(_root, "packages"));
        _projectPath = Path.Combine(projectDirectory, "LegacyApp.vbproj");
        File.WriteAllText(_projectPath, ProjectXml);
        File.WriteAllText(Path.Combine(projectDirectory, "Form1.vb"), "Public Class Form1\nEnd Class");
        File.WriteAllText(Path.Combine(projectDirectory, "Form1.Designer.vb"), "Partial Class Form1\nEnd Class");
        File.WriteAllText(Path.Combine(projectDirectory, "Form1.resx"), "<root />");
        File.WriteAllText(Path.Combine(projectDirectory, "My Project", "Resources.resx"), "<root />");
        File.WriteAllText(Path.Combine(projectDirectory, "app.config"), "<configuration />");
        File.WriteAllBytes(Path.Combine(projectDirectory, "Assets", "icon.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(projectDirectory, "lib", "Company.Common.dll"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(_root, "packages", "External.dll"), [7, 8, 9]);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task Converts_old_vbproj_and_copies_required_files()
    {
        var loaded = await CreateLoadedProject();
        var output = Path.Combine(_root, "out");
        var options = new Options(null, _projectPath, null, null, output, false, false);

        var result = await new LegacyProjectMaterializer().MaterializeAsync(options, [loaded]);

        var projectOutput = Path.Combine(output, "converted", "LegacyApp");
        var csprojPath = Path.Combine(projectOutput, "LegacyApp.csproj");
        Assert.That(File.Exists(csprojPath), Is.True);
        var xml = XDocument.Load(csprojPath);
        var values = xml.Descendants().Select(x => (Name: x.Name.LocalName, Include: x.Attribute("Include")?.Value,
            Project: x.Attribute("Project")?.Value, Value: x.Value)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(values.Any(x => x.Name == "ProjectTypeGuids" && x.Value.Contains(LegacyProjectMaterializer.CSharpProjectTypeGuid)), Is.True);
            Assert.That(values.Any(x => x.Name == "Compile" && x.Include == "Form1.cs"), Is.True);
            Assert.That(values.Any(x => x.Name == "Compile" && x.Include == "Form1.Designer.cs"), Is.True);
            Assert.That(values.Any(x => x.Name == "DependentUpon" && x.Value == "Form1.cs"), Is.True);
            Assert.That(values.Any(x => x.Name == "ProjectReference" && x.Include == "..\\Common\\Common.csproj"), Is.True);
            Assert.That(values.Any(x => x.Name == "Import" && x.Project?.Contains("Microsoft.CSharp.targets") == true), Is.True);
            Assert.That(values.Any(x => x.Name == "Generator" && x.Value == "ResXFileCodeGenerator"), Is.True);
            Assert.That(File.Exists(Path.Combine(projectOutput, "Form1.resx")), Is.True);
            Assert.That(File.Exists(Path.Combine(projectOutput, "Properties", "Resources.resx")), Is.True);
            Assert.That(File.Exists(Path.Combine(projectOutput, "app.config")), Is.True);
            Assert.That(File.Exists(Path.Combine(projectOutput, "Assets", "icon.bin")), Is.True);
            Assert.That(File.Exists(Path.Combine(projectOutput, "lib", "Company.Common.dll")), Is.True);
            Assert.That(File.Exists(Path.Combine(output, "converted", "_external", "LegacyApp", "External.dll")), Is.True);
            Assert.That(values.Any(x => x.Name == "HintPath" && x.Value == "..\\_external\\LegacyApp\\External.dll"), Is.True);
            Assert.That(result.FileOperations.Count(x => x.Result == "Copied"), Is.EqualTo(6));
        });
    }

    [Test]
    public async Task Dry_run_records_plan_without_writing_files()
    {
        var loaded = await CreateLoadedProject();
        var output = Path.Combine(_root, "dry");
        var options = new Options(null, _projectPath, null, null, output, true, false);

        var result = await new LegacyProjectMaterializer().MaterializeAsync(options, [loaded]);

        Assert.Multiple(() =>
        {
        Assert.That(Directory.Exists(Path.Combine(output, "converted")), Is.False);
            Assert.That(result.FileOperations, Is.Not.Empty);
            Assert.That(result.FileOperations.All(x => x.Result is "Planned" or "Missing"), Is.True);
        });
    }

    [Test]
    public async Task Converts_solution_project_path_and_type_guid()
    {
        var solutionPath = Path.Combine(_root, "LegacySolution.sln");
        await File.WriteAllTextAsync(solutionPath, $"Project(\"{LegacyProjectMaterializer.VisualBasicProjectTypeGuid}\") = \"LegacyApp\", \"LegacyApp\\LegacyApp.vbproj\", \"{{11111111-1111-1111-1111-111111111111}}\"\nEndProject\nGlobal\nEndGlobal\n");
        var loaded = await CreateLoadedProject();
        var output = Path.Combine(_root, "solution-out");
        var options = new Options(solutionPath, null, null, null, output, false, false);

        await new LegacyProjectMaterializer().MaterializeAsync(options, [loaded]);

        var convertedSolution = Path.Combine(output, "converted", "LegacySolution", "LegacySolution.sln");
        var content = await File.ReadAllTextAsync(convertedSolution);
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("LegacyApp\\LegacyApp.csproj"));
            Assert.That(content, Does.Contain(LegacyProjectMaterializer.CSharpProjectTypeGuid));
            Assert.That(File.Exists(Path.Combine(output, "converted", "LegacySolution", "LegacyApp", "LegacyApp.csproj")), Is.True);
        });
    }

    private async Task<LoadedProject> CreateLoadedProject()
    {
        var workspace = new AdhocWorkspace();
        var id = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            id, VersionStamp.Create(), "LegacyApp", "LegacyApp", LanguageNames.VisualBasic,
            filePath: _projectPath,
            compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        foreach (var file in new[] { "Form1.vb", "Form1.Designer.vb" })
        {
            var path = Path.Combine(Path.GetDirectoryName(_projectPath)!, file);
            solution = solution.AddDocument(DocumentId.CreateNewId(id), file, SourceText.From(await File.ReadAllTextAsync(path)), filePath: path);
        }
        var project = solution.GetProject(id)!;
        return new(project, (await project.GetCompilationAsync())!);
    }

    private const string ProjectXml = """
<Project ToolsVersion="15.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <ProjectTypeGuids>{F184B08F-C81C-45F6-A57F-5ABD9991F28F};{349C5851-65DF-11DA-9384-00065B846F21}</ProjectTypeGuids>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Form1.vb"><SubType>Form</SubType></Compile>
    <Compile Include="Form1.Designer.vb"><DependentUpon>Form1.vb</DependentUpon></Compile>
    <EmbeddedResource Include="Form1.resx"><DependentUpon>Form1.vb</DependentUpon></EmbeddedResource>
    <EmbeddedResource Include="My Project\Resources.resx"><Generator>VbMyResourcesResXFileCodeGenerator</Generator></EmbeddedResource>
    <None Include="app.config" />
    <Content Include="Assets\icon.bin" />
    <Reference Include="Company.Common"><HintPath>lib\Company.Common.dll</HintPath></Reference>
    <Reference Include="External"><HintPath>..\packages\External.dll</HintPath></Reference>
    <ProjectReference Include="..\Common\Common.vbproj"><Project>{22222222-2222-2222-2222-222222222222}</Project><Name>Common</Name></ProjectReference>
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.VisualBasic.targets" />
</Project>
""";
}
