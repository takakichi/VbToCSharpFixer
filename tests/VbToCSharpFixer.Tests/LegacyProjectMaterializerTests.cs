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

    /// <summary>各テスト用の旧形式プロジェクトと関連ファイルを一時領域へ準備します。</summary>
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

    /// <summary>テストで作成した一時プロジェクト一式を削除します。</summary>
    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>旧形式vbprojの変換、関連ファイルコピー、VBランタイム参照追加を検証します。</summary>
    [Test]
    public async Task Converts_old_vbproj_and_copies_required_files()
    {
        var loaded = await CreateLoadedProject();
        var output = Path.Combine(_root, "out");
        var options = new Options(null, _projectPath, null, null, output, false, false);

        var result = await new LegacyProjectMaterializer().MaterializeAsync(options, [loaded]);

        var projectOutput = Path.Combine(output, "converted", "LegacyApp");
        var referenceLog = await new VisualBasicRuntimeReferenceService().EnsureReferenceAsync(
            loaded.Project, projectOutput, required: true, dryRun: false);
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
            Assert.That(values.Any(x => x.Name == "Reference" && x.Include == "Microsoft.VisualBasic"), Is.True);
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
            Assert.That(referenceLog?.Result, Is.EqualTo("Success"));
        });
    }

    /// <summary>dry-runが計画だけを返し成果物を作成しないことを検証します。</summary>
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

    /// <summary>Solution内のプロジェクトパスとVB Project Type GUIDの変換を検証します。</summary>
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

    /// <summary>複数ProjectからLinkされたForm一式を各Projectの論理パスへ分離して関連付けます。</summary>
    [Test]
    public async Task Materializes_linked_forms_per_project_and_preserves_resource_parent()
    {
        var shared = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(shared);
        foreach (var file in new[] { "SharedForm.vb", "SharedForm.Designer.vb" })
            await File.WriteAllTextAsync(Path.Combine(shared, file), "Public Class SharedForm\nEnd Class");
        await File.WriteAllTextAsync(Path.Combine(shared, "SharedForm.resx"), "<root />");
        var first = await CreateLinkedProject("First");
        var second = await CreateLinkedProject("Second");
        var solutionPath = Path.Combine(_root, "Linked.sln");
        await File.WriteAllTextAsync(solutionPath, $$"""
Project("{{LegacyProjectMaterializer.VisualBasicProjectTypeGuid}}") = "First", "First\First.vbproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
Project("{{LegacyProjectMaterializer.VisualBasicProjectTypeGuid}}") = "Second", "Second\Second.vbproj", "{22222222-2222-2222-2222-222222222222}"
EndProject
Global
EndGlobal
""");
        var output = Path.Combine(_root, "linked-out");
        var result = await new LegacyProjectMaterializer().MaterializeAsync(
            new Options(solutionPath, null, null, null, output, false, false), [first, second]);

        foreach (var loaded in new[] { first, second })
        {
            var projectOutput = Path.Combine(output, "converted", "Linked", loaded.Project.Name, "Forms");
            var projectFile = Path.Combine(output, "converted", "Linked", loaded.Project.Name, loaded.Project.Name + ".csproj");
            var xml = XDocument.Load(projectFile);
            var compile = xml.Descendants().Where(x => x.Name.LocalName == "Compile")
                .Select(x => x.Attribute("Include")?.Value).ToArray();
            var resource = xml.Descendants().Single(x => x.Name.LocalName == "EmbeddedResource");
            Assert.Multiple(() =>
            {
                Assert.That(compile, Does.Contain("Forms\\SharedForm.cs"));
                Assert.That(compile, Does.Contain("Forms\\SharedForm.Designer.cs"));
                Assert.That(resource.Attribute("Include")?.Value, Is.EqualTo("Forms\\SharedForm.resx"));
                Assert.That(resource.Elements().Single(x => x.Name.LocalName == "DependentUpon").Value, Is.EqualTo("SharedForm.cs"));
                Assert.That(xml.Descendants().Any(x => x.Name.LocalName == "Link"), Is.False);
                Assert.That(File.Exists(Path.Combine(projectOutput, "SharedForm.resx")), Is.True);
                Assert.That(result.SourceOutputPaths.Values, Does.Contain(Path.Combine(projectOutput, "SharedForm.cs")));
                Assert.That(result.SourceOutputPaths.Values, Does.Contain(Path.Combine(projectOutput, "SharedForm.Designer.cs")));
            });
        }
        Assert.That(result.ManualReviews.Any(x => x.ReasonCode == ReasonCode.ResourceParentMismatch), Is.False);
    }

    /// <summary>テスト用VBプロジェクトからRoslyn Compilationを構築します。</summary>
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

    /// <summary>共有FormをForms配下へLinkするテスト用旧形式Projectを生成します。</summary>
    private async Task<LoadedProject> CreateLinkedProject(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, name + ".vbproj");
        await File.WriteAllTextAsync(projectPath, """
<Project ToolsVersion="15.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
  <ItemGroup>
    <Compile Include="..\Shared\SharedForm.vb"><Link>Forms\SharedForm.vb</Link><SubType>Form</SubType></Compile>
    <Compile Include="..\Shared\SharedForm.Designer.vb"><Link>Forms\SharedForm.Designer.vb</Link><DependentUpon>SharedForm.vb</DependentUpon></Compile>
    <EmbeddedResource Include="..\Shared\SharedForm.resx"><Link>Forms\SharedForm.resx</Link><DependentUpon>SharedForm.vb</DependentUpon></EmbeddedResource>
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.VisualBasic.targets" />
</Project>
""");
        var workspace = new AdhocWorkspace();
        var id = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            id, VersionStamp.Create(), name, name, LanguageNames.VisualBasic, filePath: projectPath,
            compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        foreach (var file in new[] { "SharedForm.vb", "SharedForm.Designer.vb" })
        {
            var path = Path.Combine(_root, "Shared", file);
            solution = solution.AddDocument(DocumentId.CreateNewId(id), file,
                SourceText.From(await File.ReadAllTextAsync(path)), folders: ["Forms"], filePath: path);
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
