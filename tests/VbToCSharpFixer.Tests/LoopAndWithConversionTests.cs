using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static VbToCSharpFixer.Tests.ConversionTestSupport;

[TestFixture]
public sealed class LoopAndWithConversionTests
{
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
}
