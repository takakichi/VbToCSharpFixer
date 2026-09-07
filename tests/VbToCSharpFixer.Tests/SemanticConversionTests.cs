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

    /// <summary>VBランタイム関数をusingと読みやすい型名で出力することを検証します。</summary>
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

    /// <summary>VBランタイム定数を意味解析し、Constants経由の読みやすい参照へ変換します。</summary>
    [Test]
    public void Converts_visual_basic_runtime_constants()
    {
        var source = """
Imports Microsoft.VisualBasic
Public Class RuntimeConstants
    Public Function Run() As String
        Return "A" & vbCrLf & "B" & vbCr & vbLf & vbTab
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "runtime-constants.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp.Split("using Microsoft.VisualBasic;", StringSplitOptions.None).Length - 1, Is.EqualTo(1));
            Assert.That(result.CSharp, Does.Contain("Constants.vbCrLf"));
            Assert.That(result.CSharp, Does.Contain("Constants.vbCr"));
            Assert.That(result.CSharp, Does.Contain("Constants.vbLf"));
            Assert.That(result.CSharp, Does.Contain("Constants.vbTab"));
            Assert.That(result.VisualBasicRuntimeTypes, Does.Contain("Microsoft.VisualBasic.Constants"));
        });
    }

    /// <summary>ProjectのGlobal ImportsからVBランタイム定数を解決してusingを追加します。</summary>
    [Test]
    public void Adds_visual_basic_using_for_globally_imported_runtime_constant()
    {
        var source = "Public Class C\nPublic Function Run() As String\nReturn vbCrLf\nEnd Function\nEnd Class";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "global-constant.vb");
        var compilation = VisualBasicCompilation.Create("GlobalConstant", [tree], PlatformReferences(),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                globalImports: [GlobalImport.Parse("Microsoft.VisualBasic")]));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.StartWith("using Microsoft.VisualBasic;"));
            Assert.That(result.CSharp, Does.Contain("return Constants.vbCrLf;"));
            Assert.That(result.VisualBasicRuntimeTypes, Does.Contain("Microsoft.VisualBasic.Constants"));
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

    /// <summary>VBランタイム定数と同名のローカル変数を誤変換しないことを検証します。</summary>
    [Test]
    public void Does_not_rewrite_user_defined_runtime_constant_name()
    {
        var source = """
Public Class C
    Public Function Run() As String
        Dim vbCrLf = "custom"
        Return vbCrLf
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "custom-constant.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("return vbCrLf;"));
            Assert.That(result.CSharp, Does.Not.Contain("Constants.vbCrLf"));
            Assert.That(result.VisualBasicRuntimeTypes, Is.Empty);
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

    /// <summary>VBランタイム型名が競合するときusing aliasを使用することを検証します。</summary>
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

    /// <summary>VB互換関数と同名のユーザー定義メソッドを誤変換しないことを検証します。</summary>
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

    /// <summary>VBソースの最後の初期化式と対応するSemanticModelを返します。</summary>
    private static (ExpressionSyntax Expression, SemanticModel Model) ParseInitializer(string source)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "test.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var expression = tree.GetRoot().DescendantNodes().OfType<EqualsValueSyntax>().Last().Value;
        return (expression, compilation.GetSemanticModel(tree));
    }

    /// <summary>プラットフォーム参照を含むテスト用VB Compilationを生成します。</summary>
    private static VisualBasicCompilation CreateCompilation(SyntaxTree tree, string name = "Tests", params MetadataReference[] additional) =>
        VisualBasicCompilation.Create(name, [tree], PlatformReferences().Concat(additional),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>テスト実行環境のTrusted Platform Assembliesを参照として列挙します。</summary>
    private static IEnumerable<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
}
