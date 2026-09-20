using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class OptionalAndPartialConversionTests
{
    [TestCase("Boolean", "True", "False")]
    [TestCase("Integer", "12", "4")]
    [TestCase("String", "\"abc\"", "\"def\"")]
    [TestCase("Decimal", "1.25D", "2.5D")]
    [TestCase("Integer", "Nothing", "4")]
    [TestCase("String", "Nothing", "\"abc\"")]
    public void Optional_defaults_and_explicit_arguments_have_same_behavior(string type, string initial, string supplied)
    {
        Compare($$"""
Public Class Example
    Public Function Value(Optional ByVal aaaa As {{type}} = {{initial}}) As {{type}}
        Return aaaa
    End Function
    Public Function Run() As Boolean
        Return Value() = {{(initial == "Nothing" && type == "Integer" ? "0" : initial)}} AndAlso Value({{supplied}}) = {{supplied}}
    End Function
End Class
""");
    }

    [Test]
    public void Optional_constructor_sub_implicit_call_omitted_and_named_arguments()
    {
        Compare("""
Public Class Example
    Private count As Integer = 0
    Public Sub New(Optional start As Integer = 4)
        count = start
    End Sub
    Public Sub Add(Optional ByVal aaaa As Boolean = True, Optional amount As Integer = 3)
        If aaaa Then count += amount
    End Sub
    Public Function Number(Optional value As Integer = 5) As Integer
        Return value
    End Function
    Public Function Run() As Integer
        Add()
        Add(, 2)
        Me.Add(amount:=6)
        Add(False)
        Add
        Return count + Number + Me.Number
    End Function
End Class
""");
    }

    [Test]
    public void Optional_enum_default_is_constant()
    {
        Compare("""
Public Enum Choice
    First = 1
    Second = 2
End Enum
Public Class Example
    Public Function Value(Optional item As Choice = Choice.Second) As Choice
        Return item
    End Function
    Public Function Run() As Boolean
        Return Value() = Choice.Second
    End Function
End Class
""");
    }

    [TestCase("Partial Public Class", "Public Class")]
    [TestCase("Partial Public Class", "Partial Class")]
    [TestCase("Public Partial Class", "Public Partial Class")]
    public void All_parts_in_different_files_receive_partial(string first, string second)
    {
        var result = Compare($$"""
{{first}} Example
    Private value As Integer = 7
End Class
""", $$"""
{{second}} Example
    Public Function Run() As Integer
        Return value
    End Function
End Class
""");
        Assert.That(result.Split("public partial class Example").Length - 1, Is.EqualTo(2));
    }

    [Test]
    public void Explicit_partial_is_preserved_even_when_only_one_part_is_loaded()
    {
        Assert.That(Compare("""
Partial Public Class Example
    Public Function Run() As Integer
        Return 7
    End Function
End Class
"""), Does.Contain("public partial class Example"));
    }

    private static string Compare(params string[] sources)
    {
        var trees = sources.Select((s, i) => VisualBasicSyntaxTree.ParseText(s, path: $"Part{i}.vb")).ToArray();
        var vb = CreateCompilation(trees[0], "OptionalPartial" + Guid.NewGuid().ToString("N")).AddSyntaxTrees(trees.Skip(1));
        using var stream = new MemoryStream();
        var emitted = vb.Emit(stream);
        Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
        var original = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Example")!;
        var constructor = original.GetConstructors().Single();
        var instance = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
        var expected = original.GetMethod("Run")!.Invoke(instance, null);
        var converter = new VbToCSharpConverter();
        var results = trees.Select(t => converter.Convert(t, vb.GetSemanticModel(t), "Test")).ToArray();
        Assert.That(results.SelectMany(r => r.ManualReviews), Is.Empty);
        var combined = results[0] with { CSharp = string.Join("\n", results.Select(r => r.CSharp)) };
        AssertGeneratedCompiles(combined, vb, "OptionalPartial");
        // パラメーターなしの呼出側を追加し、省略可能コンストラクターもC#コンパイラー経由で呼ぶ。
        combined = combined with { CSharp = combined.CSharp + "\npublic class Caller { public object Run() { return new Example().Run(); } }" };
        var converted = CompileAndCreate(combined, vb, "Caller");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(expected), combined.CSharp);
        return combined.CSharp;
    }
}
