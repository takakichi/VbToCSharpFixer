using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class ConversionSessionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Resolves_metadata_out_alongside_visual_basic_project_reference(bool brokenDependency)
    {
        // DLLのout照合に、異言語のCompilationReferenceを直接渡してはいけない。
        // 参照プロジェクトがemitできなくても、Integer.TryParseのout判定は可能。
        var dependencyTree = VisualBasicSyntaxTree.ParseText("""
Public Class Helper
    Public Shared Sub SetValue(ByRef value As Integer)
        value = 7
    End Sub
End Class
""" + (brokenDependency ? "\nPublic Class Broken\nPublic Sub Run()\nUnknownCall()\nEnd Sub\nEnd Class" : ""));
        var dependency = CreateCompilation(dependencyTree, "Dependency");
        var tree = VisualBasicSyntaxTree.ParseText("""
Public Class Caller
    Public Sub Run()
        Dim value As Integer = 0
        Integer.TryParse("42", value)
        Helper.SetValue(value)
    End Sub
End Class
""", path: "project-reference.vb");
        var compilation = CreateCompilation(tree, "Caller", dependency.ToMetadataReference());
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Caller");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("int.TryParse(\"42\", out value);"));
            Assert.That(result.CSharp, Does.Contain("Helper.SetValue(ref value);"));
            Assert.That(result.ManualReviews, Is.Empty);
        });
        if (!brokenDependency) AssertGeneratedCompiles(result, compilation, "Caller");
    }

    [Test]
    public void Reference_kind_cache_changes_with_the_source_compilation()
    {
        // 同じ型・メソッド名でも参照先が変わればref/outの契約は変わる。
        // 前のCompilationから得たout判定を次の参照へ使い回さないことを確認する。
        var converter = new VbToCSharpConverter();
        foreach (var kind in new[] { "out", "ref", "out" })
        {
            var api = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("ApiAssembly",
                [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    $"public static class Api {{ public static void Fill({kind} int value) {{ value = 42; }} }}")],
                PlatformReferences(), new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            Assert.That(api.Emit(stream).Success, Is.True);
            var tree = VisualBasicSyntaxTree.ParseText("""
Public Class Caller
    Public Sub Run()
        Dim value As Integer = 1
        Api.Fill(value)
    End Sub
End Class
""", path: "caller.vb");
            var compilation = CreateCompilation(tree, "Caller", MetadataReference.CreateFromImage(stream.ToArray()));
            var result = converter.Convert(tree, compilation.GetSemanticModel(tree), "Caller");
            Assert.That(result.CSharp, Does.Contain($"Api.Fill({kind} value);"));
            AssertGeneratedCompiles(result, compilation, "Caller");
        }
    }

    [Test]
    public void Reusing_converter_does_not_carry_file_state_into_next_conversion()
    {
        // 先のファイルでalias・一時変数・レビューを発生させ、次のファイルへ漏れないことを確認する。
        var firstTree = VisualBasicSyntaxTree.ParseText("""
Imports Microsoft.VisualBasic
Public Class Strings
End Class
Public Class First
    Public Event Changed()
    Public Sub Run()
        For i As Integer = 1 To 2
            Dim part = Mid("abc", 1, 1)
        Next
    End Sub
End Class
""", path: "first.vb");
        var secondTree = VisualBasicSyntaxTree.ParseText("""
Public Class Second
    Public Function Run() As Integer
        Dim total As Integer = 0
        For i As Integer = 1 To 2
            total += i
        Next
        Return total
    End Function
End Class
""", path: "second.vb");
        var firstCompilation = CreateCompilation(firstTree);
        var secondCompilation = CreateCompilation(secondTree);
        var converter = new VbToCSharpConverter();
        var first = converter.Convert(firstTree, firstCompilation.GetSemanticModel(firstTree), "First");
        var firstFixes = first.Fixes.ToArray();
        var firstReviews = first.ManualReviews.ToArray();

        // 部分変換も同じ公開インスタンスで交互に利用できることを確認する。
        var expression = ParseInitializer("Public Class PartialInput\nPublic Sub Run()\nDim x = 1 + 2\nEnd Sub\nEnd Class");
        converter.ConvertExpression(expression.Expression, expression.Model);
        var actual = converter.Convert(secondTree, secondCompilation.GetSemanticModel(secondTree), "Second");
        var expected = new VbToCSharpConverter().Convert(secondTree, secondCompilation.GetSemanticModel(secondTree), "Second");
        Assert.Multiple(() =>
        {
            Assert.That(first.CSharp, Does.Contain("using VBStrings ="));
            Assert.That(first.ManualReviews, Is.Not.Empty);
            Assert.That(actual.CSharp, Is.EqualTo(expected.CSharp));
            Assert.That(actual.Fixes, Is.EqualTo(expected.Fixes));
            Assert.That(actual.ManualReviews, Is.Empty);
            Assert.That(actual.VisualBasicRuntimeTypes, Is.Empty);
            Assert.That(first.Fixes, Is.EqualTo(firstFixes));
            Assert.That(first.ManualReviews, Is.EqualTo(firstReviews));
        });
        AssertGeneratedCompiles(actual, secondCompilation, "Second");
    }
}
