using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class SemanticConversionTests
{
    private const string Prelude = """
Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.Windows.Forms

Public Class MyService
    Public Function GetValue(index As Integer) As String
        Return ""
    End Function
    Public Sub Close()
    End Sub
    Public Function GetRows() As RowCollection
        Return Nothing
    End Function
End Class
Public Class MyModel
    Public Property Text As String
End Class
Public Class EmployeeCollection
    Default Public ReadOnly Property Item(index As Integer) As String
        Get
            Return ""
        End Get
    End Property
End Class
Public Class ItemMethodClass
    Public Function Item(index As Integer) As String
        Return ""
    End Function
End Class
Public Class BaseService
    Public Sub Dispose()
    End Sub
    Public ReadOnly Property Name As String
End Class
Public Class DerivedService
    Inherits BaseService
End Class
Public Interface IService
    Function GetValue(index As Integer) As String
    ReadOnly Property Name As String
End Interface
Public Class Cell
    Public Property Value As Object
End Class
Public Class CellCollection
    Default Public ReadOnly Property Item(index As Integer) As Cell
        Get
            Return Nothing
        End Get
    End Property
End Class
Public Class Row
    Public ReadOnly Property Cells As CellCollection
End Class
Public Class RowCollection
    Default Public ReadOnly Property Item(index As Integer) As Row
        Get
            Return Nothing
        End Get
    End Property
    Public Sub RemoveAt(index As Integer)
    End Sub
End Class
Public Class Grid
    Public ReadOnly Property Rows As RowCollection
    Default Public ReadOnly Property Item(col As Integer, row As Integer) As Cell
        Get
            Return Nothing
        End Get
    End Property
    Public Sub ClearSelection()
    End Sub
End Class
""";

    [TestCase("Dim value = service.GetValue(i)", "service.GetValue(i)")]
    [TestCase("Dim value = model.Text", "model.Text")]
    [TestCase("Dim value = employees(i)", "employees[i]")]
    [TestCase("Dim value = itemMethods.Item(i)", "itemMethods.Item(i)")]
    [TestCase("Dim value = numbers(i)", "numbers[i]")]
    [TestCase("Dim value = list(i)", "list[i]")]
    [TestCase("Dim value = dict(\"key\")", "dict[\"key\"]")]
    [TestCase("Dim value = grid.Rows(i)", "grid.Rows[i]")]
    [TestCase("Dim value = grid.Rows(i).Cells(j)", "grid.Rows[i].Cells[j]")]
    [TestCase("Dim value = grid.Item(i, j)", "grid[i, j]")]
    [TestCase("Dim value = grid.Rows(i).Cells(j).Value.ToString", "grid.Rows[i].Cells[j].Value.ToString()")]
    [TestCase("Dim value = contract.GetValue(i)", "contract.GetValue(i)")]
    [TestCase("Dim value = row(i)", "row[i]")]
    [TestCase("Dim value = row(\"NAME\")", "row[\"NAME\"]")]
    [TestCase("Dim value = table.Rows(i)", "table.Rows[i]")]
    [TestCase("Dim value = dataGridView.Rows(i)", "dataGridView.Rows[i]")]
    [TestCase("Dim value = dataGridView.Columns(i)", "dataGridView.Columns[i]")]
    [TestCase("Dim value = dataGridView.SelectedRows(i)", "dataGridView.SelectedRows[i]")]
    [TestCase("Dim value = dataGridView.SelectedCells(i)", "dataGridView.SelectedCells[i]")]
    [TestCase("Dim value = dataGridView.Rows(i).Cells(j)", "dataGridView.Rows[i].Cells[j]")]
    [TestCase("Dim value = dataGridView.Item(i, j)", "dataGridView[i, j]")]
    [TestCase("Dim value = control.Controls(i)", "control.Controls[i]")]
    [TestCase("Dim value = combo.Items(i)", "combo.Items[i]")]
    [TestCase("Dim value = listBox.Items(i)", "listBox.Items[i]")]
    [TestCase("Dim value = checkedList.Items(i)", "checkedList.Items[i]")]
    public void Converts_using_resolved_symbol(string statement, string expected)
    {
        var locals = """
Dim service As MyService = Nothing
Dim model As MyModel = Nothing
Dim employees As EmployeeCollection = Nothing
Dim itemMethods As ItemMethodClass = Nothing
Dim numbers As Integer() = Nothing
Dim list As List(Of String) = Nothing
Dim dict As Dictionary(Of String, Integer) = Nothing
Dim grid As Grid = Nothing
Dim derived As DerivedService = Nothing
Dim contract As IService = Nothing
Dim row As DataRow = Nothing
Dim table As DataTable = Nothing
Dim dataGridView As DataGridView = Nothing
Dim control As Control = Nothing
Dim combo As ComboBox = Nothing
Dim listBox As ListBox = Nothing
Dim checkedList As CheckedListBox = Nothing
Dim i As Integer = 0
Dim j As Integer = 0
""";
        var (expression, model) = ParseInitializer(Prelude + "\nPublic Class Usage\nPublic Sub Run()\n" + locals + "\n" + statement + "\nEnd Sub\nEnd Class");
        var actual = new VbToCSharpConverter().ConvertExpression(expression, model);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Unresolved_invocation_is_manual_review_not_guessed()
    {
        var tree = VisualBasicSyntaxTree.ParseText("Public Class C\nSub M()\nDim x = missing(i)\nEnd Sub\nEnd Class", path: "unresolved.vb");
        var compilation = CreateCompilation(tree);
        var expression = tree.GetRoot().DescendantNodes().OfType<EqualsValueSyntax>().Single().Value;
        var converter = new VbToCSharpConverter();
        var actual = converter.ConvertExpression(expression, compilation.GetSemanticModel(tree));
        Assert.That(actual, Does.Contain("ManualReviewRequired"));
    }

    [TestCase("service.Close", "service.Close()")]
    [TestCase("grid.Rows.RemoveAt(i)", "grid.Rows.RemoveAt(i)")]
    [TestCase("derived.Dispose", "derived.Dispose()")]
    [TestCase("grid.ClearSelection()", "grid.ClearSelection()")]
    public void Converts_method_expression_statements(string statement, string expected)
    {
        var source = Prelude + "\nPublic Class Usage\nPublic Sub Run(service As MyService, grid As Grid, derived As DerivedService, i As Integer)\n" + statement + "\nEnd Sub\nEnd Class";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "methods.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var expression = tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>().Last().Expression;
        var actual = new VbToCSharpConverter().ConvertExpression(expression, compilation.GetSemanticModel(tree));
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Comments_and_string_literals_are_not_rewritten()
    {
        var source = """
Public Class C
    Public Sub M()
        ' obj.Close and arr(i)
        Dim text = "obj.Close and arr(i)"
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "comments.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("// obj.Close and arr(i)"));
            Assert.That(result.CSharp, Does.Contain("\"obj.Close and arr(i)\""));
        });
    }

    [Test]
    public void Applies_vb_root_namespace_but_honors_global_namespace()
    {
        var source = "Public Class Rooted\nEnd Class\nNamespace Global.External\nPublic Class Unrooted\nEnd Class\nEnd Namespace";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "namespaces.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test", "Company.App");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("namespace Company.App"));
            Assert.That(result.CSharp, Does.Contain("namespace External"));
            Assert.That(result.CSharp, Does.Not.Contain("namespace Global.External"));
        });
    }

    [Test]
    public void Uses_readable_visual_basic_runtime_type_names()
    {
        var source = """
Imports Microsoft.VisualBasic
Public Class RuntimeUsage
    Public Function Run(text As String, value As Object) As String
        Dim part = Mid(text, 2, 3)
        Dim formatted = Format(value, "000")
        Dim validDate = IsDate(value)
        Dim nothingValue = IsNothing(value)
        Return part
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "runtime.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp.Split("using Microsoft.VisualBasic;", StringSplitOptions.None).Length - 1, Is.EqualTo(1));
            Assert.That(result.CSharp, Does.Contain("Strings.Mid(text, 2, 3)"));
            Assert.That(result.CSharp, Does.Contain("Strings.Format(value, \"000\")"));
            Assert.That(result.CSharp, Does.Contain("Information.IsDate(value)"));
            Assert.That(result.CSharp, Does.Contain("Information.IsNothing(value)"));
            Assert.That(result.CSharp, Does.Not.Contain("global::Microsoft.VisualBasic.Strings.Mid"));
            Assert.That(result.VisualBasicRuntimeTypes, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Adds_visual_basic_using_for_project_level_global_import()
    {
        var source = "Public Class RuntimeUsage\nPublic Function Run(text As String) As String\nReturn Mid(text, 1, 1)\nEnd Function\nEnd Class";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "global-import.vb");
        var compilation = VisualBasicCompilation.Create("GlobalImport", [tree], PlatformReferences(),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                globalImports: [GlobalImport.Parse("Microsoft.VisualBasic")]));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.StartWith("using Microsoft.VisualBasic;"));
            Assert.That(result.CSharp, Does.Contain("Strings.Mid(text, 1, 1)"));
        });
    }

    [Test]
    public void Uses_alias_when_visual_basic_runtime_type_name_collides()
    {
        var source = """
Imports Microsoft.VisualBasic
Public Class Strings
End Class
Public Class RuntimeUsage
    Public Function Run(text As String) As String
        Return Mid(text, 2, 3)
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "runtime-collision.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("using VBStrings = global::Microsoft.VisualBasic.Strings;"));
            Assert.That(result.CSharp, Does.Contain("VBStrings.Mid(text, 2, 3)"));
        });
    }

    [Test]
    public void Does_not_rewrite_user_defined_legacy_function_name()
    {
        var source = """
Public Class RuntimeUsage
    Public Function Mid(text As String, start As Integer) As String
        Return text
    End Function
    Public Function Run(text As String) As String
        Return Mid(text, 2)
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "custom-mid.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("return Mid(text, 2);"));
            Assert.That(result.CSharp, Does.Not.Contain("using Microsoft.VisualBasic;"));
            Assert.That(result.VisualBasicRuntimeTypes, Is.Empty);
        });
    }

    [Test]
    public void Semantic_model_resolves_symbol_from_project_reference()
    {
        var libraryTree = VisualBasicSyntaxTree.ParseText("Public Class CommonService\nPublic Function GetValue(i As Integer) As String\nReturn \"\"\nEnd Function\nEnd Class");
        var library = CreateCompilation(libraryTree, "CommonLibrary");
        using var stream = new MemoryStream();
        var emit = library.Emit(stream);
        Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
        stream.Position = 0;
        var appTree = VisualBasicSyntaxTree.ParseText("Public Class C\nSub M()\nDim s As CommonService\nDim x = s.GetValue(1)\nEnd Sub\nEnd Class");
        var app = CreateCompilation(appTree, "App", MetadataReference.CreateFromStream(stream));
        var expression = appTree.GetRoot().DescendantNodes().OfType<EqualsValueSyntax>().Last().Value;
        var actual = new VbToCSharpConverter().ConvertExpression(expression, app.GetSemanticModel(appTree));
        Assert.That(actual, Is.EqualTo("s.GetValue(1)"));
    }

    private static (ExpressionSyntax Expression, SemanticModel Model) ParseInitializer(string source)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "test.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var expression = tree.GetRoot().DescendantNodes().OfType<EqualsValueSyntax>().Last().Value;
        return (expression, compilation.GetSemanticModel(tree));
    }

    private static VisualBasicCompilation CreateCompilation(SyntaxTree tree, string name = "Tests", params MetadataReference[] additional) =>
        VisualBasicCompilation.Create(name, [tree], PlatformReferences().Concat(additional),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static IEnumerable<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
}
