using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static VbToCSharpFixer.Tests.ConversionTestSupport;

[TestFixture]
public sealed class ControlFlowConversionTests
{
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
            Assert.That(result.CSharp, Does.Contain("catch (global::System.InvalidOperationException ex) when ((VBOperators.CompareString(ex.Message, \"\", false) != 0))"));
            Assert.That(result.CSharp, Does.Contain("catch (global::System.Exception ex)"));
            Assert.That(result.CSharp, Does.Match(@"\bcatch\r?\n"));
            Assert.That(result.CSharp, Does.Contain("throw;"));
            Assert.That(result.CSharp, Does.Match(@"\bfinally\r?\n"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported TryBlock"));
            Assert.That(result.ManualReviews, Is.Empty);
            AssertGeneratedCompiles(result, compilation, "TryConverted");
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

    /// <summary>宣言、既存式および複数リソースのUsingを破棄順序を保つC# usingブロックへ変換します。</summary>
    [Test]
    public void Converts_using_blocks_and_multiple_resources()
    {
        var source = """
Imports System.IO
Public Class UsingUsage
    Public Function Run() As Long
        Using stream As New MemoryStream()
            stream.WriteByte(1)
            Return stream.Length
        End Using
    End Function
    Public Sub UseExisting(stream As MemoryStream)
        Using stream
            stream.WriteByte(1)
        End Using
    End Sub
    Public Sub UseInferred()
        Using inferred = New MemoryStream()
            inferred.WriteByte(1)
        End Using
    End Sub
    Public Sub UseMultiple()
        Using stream As New MemoryStream(), writer As New BinaryWriter(stream)
            writer.Write(1)
        End Using
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "using.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("using (global::System.IO.MemoryStream stream = new MemoryStream())"));
            Assert.That(result.CSharp, Does.Contain("using (stream)"));
            Assert.That(result.CSharp, Does.Contain("using (global::System.IO.MemoryStream inferred = new MemoryStream())"));
            Assert.That(result.CSharp, Does.Contain("using (global::System.IO.BinaryWriter writer = new BinaryWriter(stream))"));
            Assert.That(result.CSharp, Does.Not.Contain("unsupported UsingBlock"));
        });
        var compiled = CompileAndCreate(result, compilation, "UsingUsage");
        Assert.That(compiled.Type.GetMethod("Run")!.Invoke(compiled.Instance, null), Is.EqualTo(1L));
        var existing = new MemoryStream();
        compiled.Type.GetMethod("UseExisting")!.Invoke(compiled.Instance, [existing]);
        Assert.That(existing.CanWrite, Is.False);
    }
}
