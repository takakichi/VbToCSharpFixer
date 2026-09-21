using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static ConversionTestSupport;

[TestFixture]
public sealed class RequestedCompatibilityTests
{
    private static (ConversionResult Result, VisualBasicCompilation Vb) Convert(string source, params string[] imports)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source);
        var vb = CreateCompilation(tree).WithOptions(new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            globalImports: imports.Select(GlobalImport.Parse)));
        Assert.That(vb.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error), Is.Empty);
        var result = new VbToCSharpConverter().Convert(tree, vb.GetSemanticModel(tree), "Test");
        Assert.That(result.ManualReviews, Is.Empty);
        AssertGeneratedCompiles(result, vb, "Requested");
        return (result, vb);
    }

    private static void SameResult(string source, string method, params object?[] args)
    {
        var (result, vb) = Convert(source);
        using var stream = new MemoryStream();
        var emit = vb.Emit(stream);
        Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
        var original = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Sample")!;
        var expected = original.GetMethod(method)!.Invoke(Activator.CreateInstance(original), args);
        var converted = CompileAndCreate(result, vb, "Sample");
        Assert.That(converted.Type.GetMethod(method)!.Invoke(converted.Instance, args), Is.EqualTo(expected));
    }

    [Test]
    public void Inherits_project_imports_and_qualifies_conflicting_types()
    {
        var (result, _) = Convert("""
Imports System
Public Class Sample
    Public Function Run() As DateTime
        Return New DateTime(2020, 1, 1)
    End Function
End Class
Namespace Other
    Public Class DateTime
    End Class
End Namespace
""", "Other", "System.Collections.Generic");
        Assert.That(result.CSharp, Does.Contain("using Other;"));
        Assert.That(result.CSharp, Does.Contain("using System.Collections.Generic;"));
        Assert.That(result.CSharp, Does.Contain("global::System.DateTime"));
    }

    [Test]
    public void Inherits_global_namespace_and_alias_imports()
    {
        var (result, _) = Convert("""
Public Class Sample
    Public Function Run() As DateTime
        Dim value As New SB()
        Return New DateTime(2020, 1, 1)
    End Function
End Class
""", "System", "SB = System.Text.StringBuilder");
        Assert.That(result.CSharp, Does.Contain("using System;"));
        Assert.That(result.CSharp, Does.Contain("using SB = global::System.Text.StringBuilder;"));
    }

    [Test]
    public void File_alias_overrides_global_alias_and_global_type_import_is_static()
    {
        var (result, _) = Convert("""
Imports SB = System.Text.StringBuilder
Public Class Sample
    Public Function Run() As Double
        Dim value As New SB()
        Return Sqrt(4.0)
    End Function
End Class
""", "SB = System.IO.MemoryStream", "System.Math");
        Assert.That(result.CSharp, Does.Not.Contain("using SB = System.IO.MemoryStream;"));
        Assert.That(result.CSharp, Does.Contain("using static global::System.Math;"));
    }

    [Test]
    public void Floating_literal_types_preserve_overload_selection()
    {
        SameResult("""
Public Class Sample
    Public Function Pick(value As Double) As String
        Return "double"
    End Function
    Public Function Pick(value As Single) As String
        Return "single"
    End Function
    Public Function Pick(value As Decimal) As String
        Return "decimal"
    End Function
    Public Function Pick(value As Integer) As String
        Return "integer"
    End Function
    Public Function Run() As String
        Dim a = 1.0
        Dim b = 1.0F
        Dim c = 1.0D
        Return Pick(a) & Pick(b) & Pick(c) & Pick(1.0) & Pick(1.0F) & Pick(1.0D)
    End Function
End Class
""", "Run");
    }

    [Test]
    public void Function_return_assignment_supports_implicit_string_conversion()
    {
        SameResult("""
Public Class Sample
    Public Function Run(n As Integer) As String
        Run = n
        Exit Function
    End Function
End Class
""", "Run", 123);
    }

    [Test]
    public void Explicit_return_and_finally_preserve_function_value()
    {
        SameResult("""
Public Class Sample
    Public Function Run() As Integer
        Try
            Return 10
        Finally
            Run = 20
        End Try
    End Function
End Class
""", "Run");
    }

    [TestCase("Binary", ">=")]
    [TestCase("Text", ">=")]
    [TestCase("Text", "=")]
    [TestCase("Binary", "<>")]
    [TestCase("Text", "<")]
    [TestCase("Binary", "<=")]
    [TestCase("Text", ">")]
    public void String_comparisons_match_vb(string mode, string op)
    {
        var source = $"Option Compare {mode}\nPublic Class Sample\nPublic Function Run(a As String, b As String) As Boolean\nReturn a {op} b\nEnd Function\nEnd Class";
        SameResult(source, "Run", "abc", "ABC");
        SameResult(source, "Run", null, "");
        SameResult(source, "Run", "10", "2");
    }

    [Test]
    public void Floating_literals_and_conversions_match_vb()
    {
        const string source = """
Public Class Sample
    Public Function Run() As Decimal
        Dim a As Single = 1.23456789
        Dim b As Double = 1.2R
        Dim c As Decimal = 1.234567890123456789D
        Dim d As Single = 1.2F
        Dim e As Decimal = 0.12345678901234567
        Dim values As Single() = New Single() {1.2, 2.3}
        Dim computed As Single = (b + 0.123456789) / 3.0
        Return c + CDec(a) + CDec(d) + e + CDec(values(0)) + CDec(computed)
    End Function
End Class
""";
        SameResult(source, "Run");
        var (result, _) = Convert(source);
        Assert.That(result.CSharp, Does.Contain("1.2d"));
        Assert.That(result.CSharp, Does.Contain("1.2f"));
        Assert.That(result.CSharp, Does.Contain("1.234567890123456789m"));
    }

    [TestCase(123)]
    [TestCase(-2147483648)]
    public void Integer_to_string_in_all_target_contexts(int value)
    {
        const string source = """
Public Class Sample
    Public Property Text As String
    Public Function Echo(s As String) As String
        Return s
    End Function
    Public Function Run(n As Integer) As String
        Dim s As String = n
        s = n
        Text = n
        Dim values As String() = New String() {n}
        Return s & Text & Echo(n) & values(0)
    End Function
    Public Function Direct(n As Integer) As String
        Return n
    End Function
End Class
""";
        SameResult(source, "Run", value);
        SameResult(source, "Direct", value);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Function_value_preserves_recursion_exit_and_finally(int value)
    {
        const string source = """
Public Class Sample
    Public Function Run(n As Integer) As Integer
        Dim __returnValue As Integer = 10
        If n = 0 Then Return 2
        run = n
        Try
            Run += Run(n - 1)
            Exit Function
        Finally
            Run += __returnValue
        End Try
    End Function
    Public Function Plain() As String
        Return "plain"
    End Function
End Class
""";
        SameResult(source, "Run", value);
        var (result, _) = Convert(source);
        Assert.That(result.CSharp, Does.Contain("return \"plain\";"));
        Assert.That(result.CSharp.Split("public string Plain()")[1], Does.Not.Contain("__returnValue"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Function_value_initializes_default_and_continues_after_assignment(bool set)
    {
        SameResult("""
Public Class Sample
    Public Function Run(setValue As Boolean) As String
        If setValue Then
            Run = "aaaa"
            Run &= "bbbb"
        End If
    End Function
End Class
""", "Run", set);
    }
}
