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

    /// <summary>Try、複数Catch、Whenフィルター、再スロー、FinallyをC#へ変換します。</summary>
    [Test]
    public void Converts_try_catch_filter_and_finally()
    {
        var source = """
Imports System
Public Class C
    Public Sub Run(value As Object)
        Try
            Dim text = value.ToString()
        Catch ex As InvalidOperationException When ex.Message <> ""
            Throw
        Catch ex
            Dim message = ex.Message
        Catch
            Return
        Finally
            Dim finished = True
        End Try
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "try.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Match(@"\btry\r?\n"));
            Assert.That(result.CSharp, Does.Contain("catch (global::System.InvalidOperationException ex) when (ex.Message != \"\")"));
            Assert.That(result.CSharp, Does.Contain("catch (global::System.Exception ex)"));
            Assert.That(result.CSharp, Does.Match(@"\bcatch\r?\n"));
            Assert.That(result.CSharp, Does.Contain("throw;"));
            Assert.That(result.CSharp, Does.Match(@"\bfinally\r?\n"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported TryBlock"));
            Assert.That(result.ManualReviews, Is.Empty);
            AssertGeneratedCompiles(result, compilation, "TryConverted");
        });
    }

    /// <summary>Forの境界値とStepを一度だけ評価し、Exit／Continue Forを変換します。</summary>
    [Test]
    public void Converts_for_with_dynamic_step_exit_and_continue()
    {
        var source = """
Public Class C
    Public Function Run(limit As Integer, stepValue As Integer) As Integer
        Dim total = 0
        For i As Integer = 1 To limit Step stepValue
            If i = 3 Then
                Continue For
            End If
            If i = 8 Then
                Exit For
            End If
            total += i
        Next
        Return total
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "for.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("int i = 1;"));
            Assert.That(result.CSharp, Does.Contain("int __forLimit = limit;"));
            Assert.That(result.CSharp, Does.Contain("int __forStep = stepValue;"));
            Assert.That(result.CSharp, Does.Contain("(__forStep >= 0 ? i <= __forLimit : i >= __forLimit)"));
            Assert.That(result.CSharp, Does.Contain("continue;"));
            Assert.That(result.CSharp, Does.Contain("break;"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported ForBlock"));
            Assert.That(result.ManualReviews, Is.Empty);
            AssertGeneratedCompiles(result, compilation, "ForConverted");
        });
    }

    /// <summary>負のStepと宣言済み制御変数を正しい終了条件で変換します。</summary>
    [Test]
    public void Converts_for_with_existing_variable_and_negative_step()
    {
        var source = """
Public Class C
    Public Sub Run()
        Dim i As Integer
        For i = 10 To 1 Step -1
            Dim current = i
        Next i
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "for-negative.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("i = 10;"));
            Assert.That(result.CSharp, Does.Contain("int __forStep = -1;"));
            Assert.That(result.CSharp, Does.Contain("i >= __forLimit"));
            AssertGeneratedCompiles(result, compilation, "NegativeForConverted");
        });
    }

    /// <summary>Forの終了値とStepに含まれる呼び出しを生成コードへ一度だけ出力します。</summary>
    [Test]
    public void Evaluates_for_limit_and_step_expressions_once()
    {
        var source = """
Public Class C
    Private Function GetLimit() As Integer
        Return 10
    End Function
    Private Function GetStep() As Integer
        Return 2
    End Function
    Public Sub Run()
        For i As Integer = 1 To GetLimit() Step GetStep()
            Dim current = i
        Next
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "for-evaluation.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        var runMethod = result.CSharp[result.CSharp.IndexOf("public void Run", StringComparison.Ordinal)..];
        Assert.Multiple(() =>
        {
            Assert.That(runMethod.Split("GetLimit()", StringSplitOptions.None).Length - 1, Is.EqualTo(1));
            Assert.That(runMethod.Split("GetStep()", StringSplitOptions.None).Length - 1, Is.EqualTo(1));
            AssertGeneratedCompiles(result, compilation, "EvaluationForConverted");
        });
    }

    /// <summary>入れ子のForで生成一時変数名が重複しないことを検証します。</summary>
    [Test]
    public void Uses_unique_temporary_names_for_nested_for_blocks()
    {
        var source = """
Public Class C
    Public Sub Run()
        Dim __forLimit = 99
        For i As Integer = 1 To 2
            For j As Integer = 1 To 3
                Dim value = i + j
            Next
        Next
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "nested-for.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("int __forLimit2 = 2;"));
            Assert.That(result.CSharp, Does.Contain("int __forLimit3 = 3;"));
            Assert.That(result.CSharp, Does.Contain("int __forStep = 1;"));
            Assert.That(result.CSharp, Does.Contain("int __forStep2 = 1;"));
            AssertGeneratedCompiles(result, compilation, "NestedForConverted");
        });
    }

    /// <summary>Object制御変数を推測変換せずManualReviewRequiredへ残します。</summary>
    [Test]
    public void Leaves_late_bound_object_for_for_manual_review()
    {
        var source = """
Option Strict Off
Public Class C
    Public Sub Run()
        For value As Object = 1 To 3
            Dim current = value
        Next
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "object-for.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("ManualReviewRequired: unsupported ForBlock"));
            Assert.That(result.ManualReviews.Single().ReasonCode, Is.EqualTo(ReasonCode.UnsupportedSyntax));
        });
    }

    /// <summary>生成したForが正負のStepで同じ数値結果を返すことを実行時に検証します。</summary>
    [Test]
    public void Generated_for_executes_with_positive_and_negative_steps()
    {
        var source = """
Public Class LoopRunner
    Public Function SumValues(first As Integer, last As Integer, stepValue As Integer) As Integer
        Dim total = 0
        For i As Integer = first To last Step stepValue
            total += i
        Next
        Return total
    End Function
End Class
""";
        var (instance, type) = ConvertCompileAndCreate(source, "LoopRunner");
        var method = type.GetMethod("SumValues")!;
        Assert.Multiple(() =>
        {
            Assert.That(method.Invoke(instance, [1, 5, 2]), Is.EqualTo(9));
            Assert.That(method.Invoke(instance, [5, 1, -2]), Is.EqualTo(9));
            Assert.That(method.Invoke(instance, [5, 1, 1]), Is.EqualTo(0));
        });
    }

    /// <summary>宣言付きFor Each、Continue For、Exit Forを変換して実行結果を検証します。</summary>
    [Test]
    public void Converts_and_executes_declared_for_each()
    {
        var source = """
Imports System.Collections.Generic
Public Class ForEachRunner
    Public Function SumValues(items As List(Of Integer)) As Integer
        Dim total = 0
        For Each item As Integer In items
            If item = 2 Then
                Continue For
            End If
            If item = 5 Then
                Exit For
            End If
            total += item
        Next
        Return total
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "foreach.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("foreach (int __forEachItem in items)"));
            Assert.That(result.CSharp, Does.Contain("int item = __forEachItem;"));
            Assert.That(result.CSharp, Does.Contain("continue;"));
            Assert.That(result.CSharp, Does.Contain("break;"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported ForEachBlock"));
            AssertGeneratedCompiles(result, compilation, "ForEachConverted");
        });
        var (instance, type) = CompileAndCreate(result, compilation, "ForEachRunner");
        var values = new List<int> { 1, 2, 3, 5, 10 };
        Assert.That(type.GetMethod("SumValues")!.Invoke(instance, [values]), Is.EqualTo(4));
    }

    /// <summary>宣言済みFor Each変数へ各要素を代入し、ループ後に最後の値を維持します。</summary>
    [Test]
    public void Converts_existing_for_each_control_variable()
    {
        var source = """
Public Class ExistingForEachRunner
    Public Function LastValue(items As Integer()) As Integer
        Dim item = -1
        For Each item In items
            Dim current = item
        Next item
        Return item
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "foreach-existing.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("item = __forEachItem;"));
            Assert.That(result.CSharp, Does.Not.Contain("int item = __forEachItem;"));
            AssertGeneratedCompiles(result, compilation, "ExistingForEachConverted");
        });
        var (instance, type) = CompileAndCreate(result, compilation, "ExistingForEachRunner");
        var method = type.GetMethod("LastValue")!;
        Assert.Multiple(() =>
        {
            Assert.That(method.Invoke(instance, [new[] { 3, 7, 9 }]), Is.EqualTo(9));
            Assert.That(method.Invoke(instance, [Array.Empty<int>()]), Is.EqualTo(-1));
        });
    }

    /// <summary>非ジェネリックIEnumerableのObject要素を参照型へ安全にキャストして列挙します。</summary>
    [Test]
    public void Converts_non_generic_for_each_reference_conversion()
    {
        var source = """
Imports System.Collections
Public Class NonGenericForEachRunner
    Public Function LastText(items As ArrayList) As String
        Dim result As String = Nothing
        For Each text As String In items
            result = text
        Next
        Return result
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "foreach-nongeneric.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("foreach (string __forEachItem in items)"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported ForEachBlock"));
            AssertGeneratedCompiles(result, compilation, "NonGenericForEachConverted");
        });
    }

    /// <summary>DictionaryのGeneric要素型を有効なC#完全修飾型名として出力します。</summary>
    [Test]
    public void Converts_generic_dictionary_for_each_element_type()
    {
        var source = """
Imports System.Collections.Generic
Public Class C
    Public Function SumValues(items As Dictionary(Of String, Integer)) As Integer
        Dim total = 0
        For Each pair As KeyValuePair(Of String, Integer) In items
            total += pair.Value
        Next
        Return total
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "foreach-dictionary.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain(
                "foreach (global::System.Collections.Generic.KeyValuePair<string, int> __forEachItem in items)"));
            AssertGeneratedCompiles(result, compilation, "DictionaryForEachConverted");
        });
    }

    /// <summary>Object型のLate Binding列挙は推測せずManualReviewRequiredへ残します。</summary>
    [Test]
    public void Leaves_late_bound_object_for_each_for_manual_review()
    {
        var source = """
Option Strict Off
Public Class C
    Public Sub Run(values As Object)
        For Each value As Object In values
            Dim current = value
        Next
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "object-foreach.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("ManualReviewRequired: unsupported ForEachBlock"));
            Assert.That(result.ManualReviews.Single().ReasonCode, Is.EqualTo(ReasonCode.UnsupportedSyntax));
        });
    }

    /// <summary>参照型Withの対象を一度だけ評価し、先頭ドットのFieldとMethodを変換します。</summary>
    [Test]
    public void Converts_and_executes_reference_type_with_block()
    {
        var source = """
Public Class Box
    Public Value As Integer
    Public Sub Increment()
        Value += 1
    End Sub
End Class
Public Class WithRunner
    Private _box As Box
    Private Function CreateBox() As Box
        _box = New Box()
        Return _box
    End Function
    Public Function Run() As Integer
        With CreateBox()
            .Value = 4
            .Increment()
        End With
        Return _box.Value
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "with.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        var runMethod = result.CSharp[result.CSharp.IndexOf("public int Run", StringComparison.Ordinal)..];
        Assert.Multiple(() =>
        {
            Assert.That(runMethod.Split("CreateBox()", StringSplitOptions.None).Length - 1, Is.EqualTo(1));
            Assert.That(runMethod, Does.Contain("__withTarget.Value = 4;"));
            Assert.That(runMethod, Does.Contain("__withTarget.Increment();"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported WithBlock"));
            AssertGeneratedCompiles(result, compilation, "WithConverted");
        });
        var (instance, type) = CompileAndCreate(result, compilation, "WithRunner");
        Assert.That(type.GetMethod("Run")!.Invoke(instance, null), Is.EqualTo(5));
    }

    /// <summary>入れ子のWithが外側と内側で異なる一時対象を使用することを検証します。</summary>
    [Test]
    public void Converts_nested_with_blocks()
    {
        var source = """
Public Class Address
    Public City As String
End Class
Public Class Customer
    Public Name As String
    Public Address As Address
End Class
Public Class NestedWithRunner
    Public Sub Run(customer As Customer)
        With customer
            .Name = "name"
            With .Address
                .City = "city"
            End With
        End With
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "nested-with.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("var __withTarget = customer;"));
            Assert.That(result.CSharp, Does.Contain("var __withTarget2 = __withTarget.Address;"));
            Assert.That(result.CSharp, Does.Contain("__withTarget2.City = \"city\";"));
            AssertGeneratedCompiles(result, compilation, "NestedWithConverted");
        });
    }

    /// <summary>With対象の既定ItemプロパティをC# Indexerへ変換します。</summary>
    [Test]
    public void Converts_indexer_inside_with_block()
    {
        var source = """
Imports System.Collections.Generic
Public Class WithIndexerRunner
    Public Sub SetFirst(items As List(Of String))
        With items
            .Item(0) = "changed"
        End With
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "with-indexer.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("__withTarget[0] = \"changed\";"));
            Assert.That(result.CSharp, Does.Not.Contain("ManualReviewRequired"));
            AssertGeneratedCompiles(result, compilation, "WithIndexerConverted");
        });
    }

    /// <summary>値型Withは読み取りだけを許可し、書き換えはManualReviewRequiredへ残します。</summary>
    [Test]
    public void Converts_read_only_struct_with_but_rejects_mutation()
    {
        var readSource = """
Public Structure PointValue
    Public X As Integer
End Structure
Public Class C
    Public Function Read(point As PointValue) As Integer
        Dim result = 0
        With point
            result = .X
        End With
        Return result
    End Function
End Class
""";
        var readTree = VisualBasicSyntaxTree.ParseText(readSource, path: "read-struct-with.vb");
        var readCompilation = CreateCompilation(readTree, "ReadStructWith");
        var readResult = new VbToCSharpConverter().Convert(
            readTree, readCompilation.GetSemanticModel(readTree), "Test");

        var writeSource = """
Public Structure PointValue
    Public X As Integer
End Structure
Public Class C
    Public Sub Write(point As PointValue)
        With point
            .X = 10
        End With
    End Sub
End Class
""";
        var writeTree = VisualBasicSyntaxTree.ParseText(writeSource, path: "write-struct-with.vb");
        var writeCompilation = CreateCompilation(writeTree, "WriteStructWith");
        var writeResult = new VbToCSharpConverter().Convert(
            writeTree, writeCompilation.GetSemanticModel(writeTree), "Test");

        Assert.Multiple(() =>
        {
            Assert.That(readResult.CSharp, Does.Contain("result = __withTarget.X;"));
            Assert.That(readResult.ManualReviews, Is.Empty);
            AssertGeneratedCompiles(readResult, readCompilation, "ReadStructWithConverted");
            Assert.That(writeResult.CSharp, Does.Contain("ManualReviewRequired: unsupported WithBlock"));
            Assert.That(writeResult.ManualReviews.Single().ReasonCode, Is.EqualTo(ReasonCode.UnsupportedSyntax));
        });
    }

    /// <summary>生成したCatchとFinallyが実行時に元の順序で処理されることを検証します。</summary>
    [Test]
    public void Generated_try_executes_catch_then_finally()
    {
        var source = """
Imports System
Public Class TryRunner
    Public Function Run() As Integer
        Dim result = 0
        Try
            Throw New InvalidOperationException()
        Catch ex As InvalidOperationException
            result = 1
        Finally
            result += 2
        End Try
        Return result
    End Function
End Class
""";
        var (instance, type) = ConvertCompileAndCreate(source, "TryRunner");
        Assert.That(type.GetMethod("Run")!.Invoke(instance, null), Is.EqualTo(3));
    }

    /// <summary>ProjectのGlobal Importsだけで解決したCatch型を完全修飾してC#でも有効にします。</summary>
    [Test]
    public void Qualifies_catch_type_resolved_from_project_global_import()
    {
        var source = "Public Class C\nPublic Sub Run()\nTry\nDim value = 1\nCatch ex As Exception\nThrow\nEnd Try\nEnd Sub\nEnd Class";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "global-catch.vb");
        var compilation = VisualBasicCompilation.Create("GlobalCatch", [tree], PlatformReferences(),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                globalImports: [GlobalImport.Parse("System")]));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("catch (global::System.Exception ex)"));
            AssertGeneratedCompiles(result, compilation, "GlobalCatchConverted");
        });
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

    /// <summary>Enum宣言、属性、正式なメンバー名、Flags演算および整数変換をC#へ変換します。</summary>
    [Test]
    public void Converts_enum_blocks_and_enum_value_references()
    {
        var source = """
Imports System
<Flags>
Public Enum Status As Integer
    None = 0
    Ready = 1
    ErrorState = 2
    All = Ready Or ErrorState
    Mask = &HFF
    Negative = -1
End Enum

Public Class EnumUsage
    Public Function Accept(value As Status) As Status
        Return value
    End Function

    Public Function Run(value As Status) As Integer
        Dim fromNumber As Status = 1
        Dim canonical = Status.ready
        Dim combined = (fromNumber Or canonical) Xor Status.ErrorState
        Dim inverted = Not combined
        Dim passed = Accept(2)
        Return passed
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "enum.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));

        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");

        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("[global::System.FlagsAttribute]"));
            Assert.That(result.CSharp, Does.Contain("public enum Status : int"));
            Assert.That(result.CSharp, Does.Contain("All = Ready | ErrorState"));
            Assert.That(result.CSharp, Does.Contain("Mask = 255"));
            Assert.That(result.CSharp, Does.Contain("Negative = -1"));
            Assert.That(result.CSharp, Does.Contain("var canonical = Status.Ready;"));
            Assert.That(result.CSharp, Does.Contain("var combined = (fromNumber | canonical) ^ Status.ErrorState;"));
            Assert.That(result.CSharp, Does.Contain("var inverted = ~combined;"));
            Assert.That(result.CSharp, Does.Contain("Accept((Status)(2))"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported EnumBlock"));
        });
        AssertGeneratedCompiles(result, compilation, "EnumConversion");
    }

    /// <summary>Enum名とメンバー名がC#予約語の場合に宣言と参照を同じ名前へエスケープします。</summary>
    [Test]
    public void Escapes_csharp_keywords_in_enum_declarations_and_references()
    {
        var source = """
Public Enum [class]
    [event] = 1
End Enum
Public Class Usage
    Public Function Run() As [class]
        Return [class].[event]
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "enum-keywords.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");

        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("public enum @class"));
            Assert.That(result.CSharp, Does.Contain("@event = 1"));
            Assert.That(result.CSharp, Does.Contain("return @class.@event;"));
        });
        AssertGeneratedCompiles(result, compilation, "EnumKeywords");
    }

    /// <summary>CInt、CStrなどをVB互換Conversions呼び出しへ変換し、VBの丸め動作を維持します。</summary>
    [Test]
    public void Converts_predefined_casts_with_visual_basic_conversions()
    {
        var source = """
Public Class CastUsage
    Public Function ToNumber(value As Double) As Integer
        Return CInt(value)
    End Function
    Public Function ToText(value As Object) As String
        Return CStr(value)
    End Function
    Public Function EmptyText() As String
        Return CStr(Nothing)
    End Function
    Public Function Box(value As Integer) As Object
        Return CObj(value)
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "predefined-casts.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");

        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("using VBConversions = global::Microsoft.VisualBasic.CompilerServices.Conversions;"));
            Assert.That(result.CSharp, Does.Contain("return VBConversions.ToInteger(value);"));
            Assert.That(result.CSharp, Does.Contain("return VBConversions.ToString(value);"));
            Assert.That(result.CSharp, Does.Contain("return VBConversions.ToString((object)null);"));
            Assert.That(result.CSharp, Does.Contain("return (object)(value);"));
            Assert.That(result.VisualBasicRuntimeTypes, Does.Contain("Microsoft.VisualBasic.CompilerServices.Conversions"));
        });

        var compiled = CompileAndCreate(result, compilation, "CastUsage");
        Assert.Multiple(() =>
        {
            Assert.That(compiled.Type.GetMethod("ToNumber")!.Invoke(compiled.Instance, [2.5d]), Is.EqualTo(2));
            Assert.That(compiled.Type.GetMethod("ToNumber")!.Invoke(compiled.Instance, [1.5d]), Is.EqualTo(2));
            Assert.That(compiled.Type.GetMethod("EmptyText")!.Invoke(compiled.Instance, null), Is.Null);
        });
    }

    /// <summary>サポート対象のVB定義済み型変換がすべてConversionsの有効なメソッドへ変換されます。</summary>
    [Test]
    public void Converts_all_supported_predefined_casts_to_compilable_calls()
    {
        var source = """
Public Class AllCasts
    Public Function AsBoolean(value As Object) As Boolean
        Return CBool(value)
    End Function
    Public Function AsByte(value As Object) As Byte
        Return CByte(value)
    End Function
    Public Function AsSByte(value As Object) As SByte
        Return CSByte(value)
    End Function
    Public Function AsShort(value As Object) As Short
        Return CShort(value)
    End Function
    Public Function AsUShort(value As Object) As UShort
        Return CUShort(value)
    End Function
    Public Function AsInteger(value As Object) As Integer
        Return CInt(value)
    End Function
    Public Function AsUInteger(value As Object) As UInteger
        Return CUInt(value)
    End Function
    Public Function AsLong(value As Object) As Long
        Return CLng(value)
    End Function
    Public Function AsULong(value As Object) As ULong
        Return CULng(value)
    End Function
    Public Function AsSingle(value As Object) As Single
        Return CSng(value)
    End Function
    Public Function AsDouble(value As Object) As Double
        Return CDbl(value)
    End Function
    Public Function AsDecimal(value As Object) As Decimal
        Return CDec(value)
    End Function
    Public Function AsChar(value As Object) As Char
        Return CChar(value)
    End Function
    Public Function AsDate(value As Object) As Date
        Return CDate(value)
    End Function
    Public Function AsString(value As Object) As String
        Return CStr(value)
    End Function
    Public Function AsObject(value As Object) As Object
        Return CObj(value)
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "all-predefined-casts.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");

        foreach (var method in new[]
                 {
                     "ToBoolean", "ToByte", "ToSByte", "ToShort", "ToUShort", "ToInteger", "ToUInteger",
                     "ToLong", "ToULong", "ToSingle", "ToDouble", "ToDecimal", "ToChar", "ToDate", "ToString"
                 })
            Assert.That(result.CSharp, Does.Contain($"VBConversions.{method}(value)"));
        Assert.That(result.CSharp, Does.Contain("(object)(value)"));
        AssertGeneratedCompiles(result, compilation, "AllPredefinedCasts");
    }

    /// <summary>変数名側の空Rankと境界値から配列型およびVB上限+1の配列生成を出力します。</summary>
    [Test]
    public void Converts_array_ranks_and_bounds_declared_on_variable_names()
    {
        var source = """
Public Class ArrayUsage
    Private values() As String
    Public Function Run() As Integer
        Dim one() As String
        Dim matrix(,) As Integer
        Dim allocated(2) As String
        Return allocated.Length
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "arrays.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("private string[] values;"));
            Assert.That(result.CSharp, Does.Contain("string[] one;"));
            Assert.That(result.CSharp, Does.Contain("int[,] matrix;"));
            Assert.That(result.CSharp, Does.Contain("string[] allocated = new string[(2) + 1];"));
        });
        var compiled = CompileAndCreate(result, compilation, "ArrayUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, null), Is.EqualTo(3));
    }

    /// <summary>instance、this/base初期化およびSharedのSub NewをC#コンストラクターへ変換します。</summary>
    [Test]
    public void Converts_constructor_blocks_and_initializers()
    {
        var source = """
Public Class BaseType
    Public Sub New(value As Integer)
    End Sub
End Class
Public Class ConstructorUsage
    Inherits BaseType
    Public Sub New()
        Me.New(1)
    End Sub
    Public Sub New(value As Integer)
        MyBase.New(value)
        If value < 0 Then Exit Sub Else Value = value
    End Sub
    Shared Sub New()
    End Sub
    Public Value As Integer
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "constructors.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("public BaseType(int value)"));
            Assert.That(result.CSharp, Does.Contain("public ConstructorUsage() : this(1)"));
            Assert.That(result.CSharp, Does.Contain("public ConstructorUsage(int value) : base(value)"));
            Assert.That(result.CSharp, Does.Contain("static ConstructorUsage()"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported ConstructorBlock"));
        });
        var compiled = CompileAndCreate(result, compilation, "ConstructorUsage");
        Assert.That(compiled.Type.GetField("Value")!.GetValue(compiled.Instance), Is.EqualTo(1));
    }

    /// <summary>SingleLine Ifの複数Then文とElse文を通常のC#ブロックへ変換します。</summary>
    [Test]
    public void Converts_single_line_if_statements()
    {
        var source = """
Public Class SingleIfUsage
    Public Function Run(value As Integer) As Integer
        Dim result As Integer = 0
        If value > 0 Then result = 10 : result += 1 Else result = -1
        Return result
    End Function
End Class
""";
        var compiled = ConvertCompileAndCreate(source, "SingleIfUsage");
        Assert.Multiple(() =>
        {
            Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, [1]), Is.EqualTo(11));
            Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, [0]), Is.EqualTo(-1));
        });
    }

    /// <summary>識別子Label、数値LabelおよびGoToをメソッド内で対応付けて変換します。</summary>
    [Test]
    public void Converts_labels_and_goto_statements()
    {
        var source = """
Public Class LabelUsage
    Public Function Run() As Integer
        Dim count As Integer = 0
        GoTo Start
100:
        Return count
Start:
        count += 1
        If count < 2 Then GoTo Start Else GoTo 100
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "labels.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("goto "));
            Assert.That(result.CSharp, Does.Contain("__label_100"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported LabelStatement"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported GoToStatement"));
        });
        var compiled = CompileAndCreate(result, compilation, "LabelUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, null), Is.EqualTo(2));
    }

    /// <summary>Select Caseの値、複数値、範囲、比較、ElseおよびExit Selectを変換します。</summary>
    [TestCase(1, "one")]
    [TestCase(3, "two-three")]
    [TestCase(7, "range")]
    [TestCase(20, "large")]
    [TestCase(15, "other")]
    public void Converts_select_blocks(int value, string expected)
    {
        var source = """
Public Class SelectUsage
    Public Function Run(value As Integer) As String
        Dim result As String = ""
        Select Case value
            Case 1
                result = "one"
            Case 2, 3
                result = "two-three"
            Case 4 To 10
                result = "range"
            Case Is >= 20
                result = "large"
                Exit Select
            Case Else
                result = "other"
        End Select
        Return result
    End Function
End Class
""";
        var compiled = ConvertCompileAndCreate(source, "SelectUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, [value]), Is.EqualTo(expected));
    }

    /// <summary>Option Compare Textの文字列SelectをVB Operatorsで比較して大文字小文字を無視します。</summary>
    [Test]
    public void Preserves_option_compare_text_in_string_select()
    {
        var source = """
Option Compare Text
Public Class TextSelectUsage
    Public Function Run(value As String) As Boolean
        Select Case value
            Case "alpha"
                Return True
            Case Else
                Return False
        End Select
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "text-select.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.That(result.CSharp, Does.Contain("VBOperators.CompareString"));
        var compiled = CompileAndCreate(result, compilation, "TextSelectUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, ["ALPHA"]), Is.True);
    }

    /// <summary>Object型Selectの値、範囲、比較をVB Operatorsで実行してLate Binding比較を維持します。</summary>
    [TestCase(2, "range")]
    [TestCase(12, "large")]
    [TestCase(5, "other")]
    public void Preserves_object_comparisons_in_select(object value, string expected)
    {
        var source = """
Public Class ObjectSelectUsage
    Public Function Run(value As Object) As String
        Select Case value
            Case 1 To 3
                Return "range"
            Case Is >= 10
                Return "large"
            Case Else
                Return "other"
        End Select
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "object-select.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.That(result.CSharp, Does.Contain("VBOperators.ConditionalCompareObject"));
        var compiled = CompileAndCreate(result, compilation, "ObjectSelectUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, [value]), Is.EqualTo(expected));
    }

    /// <summary>CTypeの数値・文字列変換はVB互換Conversionsを使い、参照型キャストは後続呼び出しを含めて正しく括ります。</summary>
    [Test]
    public void Converts_ctype_with_vb_semantics_and_safe_parentheses()
    {
        var source = """
Public Class CTypeTarget
    Public Function Text() As String
        Return "target"
    End Function
End Class
Public Class CTypeUsage
    Public Function Rounded(value As Double) As Integer
        Return CType(value, Integer)
    End Function
    Public Function Parsed(value As Object) As Integer
        Return CType(value, Integer)
    End Function
    Public Function TargetText(value As Object) As String
        Return CType(value, CTypeTarget).Text()
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "ctype.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("VBConversions.ToInteger(value)"));
            Assert.That(result.CSharp, Does.Contain("((CTypeTarget)(value)).Text()"));
        });
        var compiled = CompileAndCreate(result, compilation, "CTypeUsage");
        Assert.That(compiled.Type.GetMethod("Rounded")!.Invoke(compiled.Instance, [2.5d]), Is.EqualTo(2));
        Assert.That(compiled.Type.GetMethod("Parsed")!.Invoke(compiled.Instance, ["123"]), Is.EqualTo(123));
    }

    /// <summary>Moduleの暗黙Sharedフィールド、プロパティ、メソッドをstatic class内のstaticメンバーとして生成します。</summary>
    [Test]
    public void Converts_implicit_shared_module_members_to_static_members()
    {
        var source = """
Friend Module Resources
    Private resourceCulture As Global.System.Globalization.CultureInfo
    Friend Property Culture As Global.System.Globalization.CultureInfo
        Get
            Return resourceCulture
        End Get
        Set(value As Global.System.Globalization.CultureInfo)
            resourceCulture = value
        End Set
    End Property
    Friend Function ResourceName() As String
        Return "sample"
    End Function
End Module
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "Resources.Designer.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("private static global::System.Globalization.CultureInfo resourceCulture;"));
            Assert.That(result.CSharp, Does.Contain("internal static global::System.Globalization.CultureInfo Culture"));
            Assert.That(result.CSharp, Does.Contain("internal static string ResourceName()"));
        });
        AssertGeneratedCompiles(result, compilation, "Resources");
    }

    /// <summary>DesignerのISupportInitializeキャストを括り、BeginInitおよびEndInitをキャスト後の値へ呼び出します。</summary>
    [Test]
    public void Converts_designer_begin_init_casts_with_safe_parentheses()
    {
        var source = """
Public Class DesignerUsage
    Public Grid As Global.System.Windows.Forms.DataGridView
    Public Sub InitializeComponent()
        CType(Me.Grid, Global.System.ComponentModel.ISupportInitialize).BeginInit()
        DirectCast(Me.Grid, Global.System.ComponentModel.ISupportInitialize).EndInit()
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "Form1.Designer.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("((global::System.ComponentModel.ISupportInitialize)(this.Grid)).BeginInit()"));
            Assert.That(result.CSharp, Does.Contain("((global::System.ComponentModel.ISupportInitialize)(this.Grid)).EndInit()"));
        });
        AssertGeneratedCompiles(result, compilation, "DesignerUsage");
    }

    /// <summary>DesignerのAddRangeで使うVB配列生成をC#の型付き配列初期化子へ変換します。</summary>
    [Test]
    public void Converts_designer_add_range_array_initializer()
    {
        var source = """
Public Class MenuDesignerUsage
    Public menuStrip1 As Global.System.Windows.Forms.MenuStrip
    Public toolStripMenuItem1 As Global.System.Windows.Forms.ToolStripMenuItem
    Public toolStripMenuItem4 As Global.System.Windows.Forms.ToolStripMenuItem
    Public Sub InitializeComponent()
        Me.menuStrip1.Items.AddRange(New Global.System.Windows.Forms.ToolStripItem() {
            Me.toolStripMenuItem1,
            Me.toolStripMenuItem4})
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "Menu.Designer.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.That(result.CSharp, Does.Contain(
            "new global::System.Windows.Forms.ToolStripItem[] { this.toolStripMenuItem1, this.toolStripMenuItem4 }"));
        AssertGeneratedCompiles(result, compilation, "MenuDesignerUsage");
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

    /// <summary>生成された単一C#ソースを元VB Compilationの参照でコンパイル検証します。</summary>
    private static void AssertGeneratedCompiles(ConversionResult result, Compilation compilation, string name)
    {
        var errors = new ValidationService().ValidateCompilation(
            [(result.CSharp, name + ".cs")], compilation.References, name);
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
    }

    /// <summary>VBソースを変換してC# Assemblyをメモリへemitし、指定した型のインスタンスを返します。</summary>
    private static (object Instance, Type Type) ConvertCompileAndCreate(string source, string typeName)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source, path: typeName + ".vb");
        var vbCompilation = CreateCompilation(tree, typeName + "Vb");
        var result = new VbToCSharpConverter().Convert(tree, vbCompilation.GetSemanticModel(tree), "Test");
        return CompileAndCreate(result, vbCompilation, typeName);
    }

    /// <summary>変換済みC#をメモリへemitし、指定した型のインスタンスを返します。</summary>
    private static (object Instance, Type Type) CompileAndCreate(
        ConversionResult result, Compilation vbCompilation, string typeName)
    {
        var csharpTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.CSharp, path: typeName + ".cs");
        var csharpCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            typeName + "Cs" + Guid.NewGuid().ToString("N"), [csharpTree], vbCompilation.References,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = csharpCompilation.Emit(stream);
        Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
        var type = System.Reflection.Assembly.Load(stream.ToArray()).GetType(typeName)!;
        return (Activator.CreateInstance(type)!, type);
    }

    /// <summary>プラットフォーム参照を含むテスト用VB Compilationを生成します。</summary>
    private static VisualBasicCompilation CreateCompilation(SyntaxTree tree, string name = "Tests", params MetadataReference[] additional) =>
        VisualBasicCompilation.Create(name, [tree], PlatformReferences().Concat(additional),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>テスト実行環境のTrusted Platform Assembliesを参照として列挙します。</summary>
    private static IEnumerable<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
}
