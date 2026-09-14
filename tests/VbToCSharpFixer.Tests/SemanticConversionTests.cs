using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static VbToCSharpFixer.Tests.ConversionTestSupport;

[TestFixture]
public sealed class SemanticConversionTests
{
    /// <summary>解決済みシンボルに基づくメソッド、配列、Indexer変換を検証します。</summary>
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

    /// <summary>未解決呼び出しを推測せずManualReviewRequiredにすることを検証します。</summary>
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

    /// <summary>式ステートメント内の引数なし／引数ありメソッド呼び出しを検証します。</summary>
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

    /// <summary>コメントと文字列リテラルの内容を置換しないことを検証します。</summary>
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

    /// <summary>RootNamespaceの適用とGlobal名前空間の除外を検証します。</summary>
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

    /// <summary>Constants型名が競合するときVBランタイム定数にusing aliasを使用します。</summary>
    [Test]
    public void Uses_alias_when_visual_basic_constants_type_name_collides()
    {
        var source = """
Imports Microsoft.VisualBasic
Public Class Constants
End Class
Public Class C
    Public Function Run() As String
        Return vbCrLf
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "constant-collision.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("using VBConstants = global::Microsoft.VisualBasic.Constants;"));
            Assert.That(result.CSharp, Does.Contain("return VBConstants.vbCrLf;"));
        });
    }

    /// <summary>文字列内の実タブを維持し、バックスラッシュとtの並びと区別します。</summary>
    [Test]
    public void Preserves_literal_tab_without_changing_backslash_t()
    {
        var source = """
Public Class C
    Public Sub Run()
        Dim actualTab = "A	B"
        Dim backslashT = "A\tB"
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "tabs.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("\"A\tB\""));
            Assert.That(result.CSharp, Does.Contain("\"A\\\\tB\""));
            Assert.That(result.CSharp, Does.Not.Contain("\"A\\tB\""));
            Assert.That(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.CSharp).GetDiagnostics()
                .Where(x => x.Severity == DiagnosticSeverity.Error), Is.Empty);
        });
    }

    /// <summary>VBのIs／IsNot参照同一性比較を古いC#でも有効なReferenceEqualsへ変換します。</summary>
    [TestCase("Dim result = value Is Nothing", "object.ReferenceEquals(value, null)")]
    [TestCase("Dim result = value IsNot Nothing", "!object.ReferenceEquals(value, null)")]
    [TestCase("Dim result = left Is right", "object.ReferenceEquals(left, right)")]
    [TestCase("Dim result = left IsNot right", "!object.ReferenceEquals(left, right)")]
    public void Converts_reference_identity_operators(string statement, string expected)
    {
        var source = "Public Class C\nPublic Sub Run(value As Object, left As Object, right As Object)\n" +
            statement + "\nEnd Sub\nEnd Class";
        var (expression, model) = ParseInitializer(source);
        var actual = new VbToCSharpConverter().ConvertExpression(expression, model);
        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <summary>プロジェクトのGlobal Importsから必要なusingを追加することを検証します。</summary>
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

    /// <summary>別アセンブリ相当の参照からメソッドシンボルを解決できることを検証します。</summary>
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

    /// <summary>型と格納場所を確定できる呼び出しだけにrefまたはoutを付加します。</summary>
    [Test]
    public void Converts_only_proven_safe_ref_and_out_arguments()
    {
        var source = """
Public Class RefOutUsage
    Private Sub Increment(ByRef value As Integer)
        value += 1
    End Sub
    Public Function Run(text As String) As Integer
        Dim value As Integer = 1
        Increment(value)
        Dim parsed As Integer
        Integer.TryParse(text, parsed)
        Return value + parsed
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "ref-out.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("Increment(ref value);"));
            Assert.That(result.CSharp, Does.Contain("int.TryParse(text, out parsed);"));
            Assert.That(result.ManualReviews, Is.Empty);
        });
        var compiled = CompileAndCreate(result, compilation, "RefOutUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, ["42"]), Is.EqualTo(44));
    }

    /// <summary>変換式やPropertyをByRefへ渡すVB copy-backケースは推測せずレビュー対象にします。</summary>
    [Test]
    public void Flags_unsafe_byref_copy_back_arguments_for_manual_review()
    {
        var source = """
Public Class UnsafeByRefUsage
    Public Property Number As Integer
    Private Sub SetValue(ByRef value As Integer)
        value = 1
    End Sub
    Public Sub Run(value As Object)
        SetValue(CInt(value))
        SetValue(Number)
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "unsafe-byref.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("VBConversions.ToInteger(value)"));
            Assert.That(result.CSharp, Does.Contain("ManualReviewRequired: Ref argument"));
            Assert.That(result.ManualReviews.Count(x => x.Details.Contains("copy-in/copy-back", StringComparison.Ordinal)), Is.EqualTo(2));
        });
    }
}
