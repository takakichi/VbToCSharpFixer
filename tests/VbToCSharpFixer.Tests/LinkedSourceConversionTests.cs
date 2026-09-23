using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class LinkedSourceConversionTests
{
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, false, false, true)]
    public async Task Shared_source_has_one_output_and_conflicts_are_not_overwritten(bool conflict, bool dryRun, bool reverse, bool conditional = false)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        try
        {
            var source = Path.Combine(root, "Library", "Common.vb");
            var code = "Public Class Common\nPublic Shared Function Value() As Integer\n" +
                (conditional ? "#If CUSTOM Then\nReturn 8\n#Else\nReturn 7\n#End If\n" : "Return 7\n") + "End Function\nEnd Class";
            await File.WriteAllTextAsync(source, code);
            var solutionPath = Path.Combine(root, "Input.sln");
            await File.WriteAllTextAsync(solutionPath, "Global\nEndGlobal");
            using var workspace = new AdhocWorkspace();
            var loaded = new List<LoadedProject>();
            foreach (var name in new[] { "Library", "App" })
            {
                var projectPath = Path.Combine(root, name, name + ".vbproj");
                var compile = name == "Library"
                    ? new XElement("Compile", new XAttribute("Include", "Common.vb"))
                    : new XElement("Compile", new XAttribute("Include", "..\\Library\\Common.vb"), new XElement("Link", "Helpers\\Common.vb"));
                new XDocument(new XElement("Project", new XElement("ItemGroup", compile))).Save(projectPath);
                var id = ProjectId.CreateNewId();
                var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name,
                    LanguageNames.VisualBasic, filePath: projectPath,
                    compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                        rootNamespace: conflict && !conditional && name == "App" ? "Different" : ""), metadataReferences: PlatformReferences(),
                    parseOptions: new VisualBasicParseOptions(preprocessorSymbols: new[] { new KeyValuePair<string, object>("CUSTOM", conditional && name == "App") })));
                solution = solution.AddDocument(DocumentId.CreateNewId(id), "Common.vb", SourceText.From(code),
                    folders: name == "App" ? ["Helpers"] : [], filePath: source);
                var project = solution.GetProject(id)!;
                loaded.Add(new(project, (await project.GetCompilationAsync())!));
            }
            if (reverse) loaded.Reverse();
            var options = new Options(solutionPath, null, null, null, Path.Combine(root, "out"), dryRun, false, true);
            var materialized = await new LegacyProjectMaterializer().MaterializeAsync(options, loaded);
            var destinations = materialized.SourceOutputPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.That(destinations, Has.Length.EqualTo(1));
            var destination = Path.Combine(options.Output, "converted", "Input", "Library", "Common.cs");
            Assert.That(destinations[0], Is.EqualTo(destination));
            var reviews = materialized.ManualReviews.ToList();
            var converter = new ProjectSourceConverter();
            await converter.PrepareSharedAsync(loaded, materialized, options, reviews);
            foreach (var project in loaded)
                await converter.ConvertAsync(project, options, new OutputLayout(options), materialized, [], reviews, []);
            Assert.That(reviews.Count(r => r.ReasonCode == ReasonCode.LinkedSourceConflict), Is.EqualTo(conflict ? 2 : 0));
            if (!conflict) Assert.That(reviews, Is.Empty);
            Assert.That(await File.ReadAllTextAsync(source), Is.EqualTo(code));
            if (dryRun)
            {
                Assert.That(Directory.Exists(options.Output), Is.False);
                return;
            }
            var app = XDocument.Load(Path.Combine(options.Output, "converted", "Input", "App", "App.csproj"));
            Assert.That(app.Descendants("Compile").Single().Attribute("Include")!.Value, Is.EqualTo("..\\Library\\Common.cs"));
            Assert.That(app.Descendants("Link").Single().Value, Is.EqualTo("Helpers\\Common.cs"));
            Assert.That(Directory.GetFiles(Path.Combine(options.Output, "converted"), "Common.cs", SearchOption.AllDirectories), Has.Length.EqualTo(1));
            var generated = await File.ReadAllTextAsync(destination);
            if (conflict)
            {
                Assert.That(generated, Does.StartWith("#error LinkedSourceConflict"));
                var report = Directory.GetFiles(Path.Combine(options.Output, "logs", "linked-conflicts")).Single();
                var content = await File.ReadAllTextAsync(report);
                Assert.That(content, Does.Contain("Project: Library").And.Contain("Project: App"));
                Assert.That(content, Does.Contain(conditional ? "return 8;" : "namespace Different"));
            }
            else Assert.That(generated, Does.Contain("public static int Value()"));
        }
        finally { Directory.Delete(root, true); }
    }
}
