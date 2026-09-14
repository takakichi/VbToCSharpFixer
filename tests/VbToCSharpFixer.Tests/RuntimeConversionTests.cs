using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static VbToCSharpFixer.Tests.ConversionTestSupport;

[TestFixture]
public sealed class RuntimeConversionTests
{
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
}
