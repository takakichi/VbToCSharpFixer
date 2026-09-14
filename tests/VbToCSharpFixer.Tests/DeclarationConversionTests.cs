using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static VbToCSharpFixer.Tests.ConversionTestSupport;

[TestFixture]
public sealed class DeclarationConversionTests
{
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

    /// <summary>メソッドのXML文書、通常、宣言行末および終了位置コメントをC#へ保持します。</summary>
    [Test]
    public void Preserves_method_documentation_and_boundary_comments()
    {
        var source = """
Public Class CommentedMethods
    ''' <summary>
    ''' 日本語の説明です。
    ''' </summary>
    ''' <param name="value">入力値</param>
    ''' <returns>結果</returns>
    Public Function Echo(value As String) As String ' 宣言行コメント
        Return value
        ' End Function直前コメント
    End Function ' 終了行コメント

    ' 通常のメソッドコメント
    Public Sub Run()
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "method-comments.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("/// <summary>"));
            Assert.That(result.CSharp, Does.Contain("/// 日本語の説明です。"));
            Assert.That(result.CSharp, Does.Contain("/// <param name=\"value\">入力値</param>"));
            Assert.That(result.CSharp, Does.Contain("public string Echo(string value) // 宣言行コメント"));
            Assert.That(result.CSharp, Does.Contain("// End Function直前コメント"));
            Assert.That(result.CSharp, Does.Contain("} // 終了行コメント"));
            Assert.That(result.CSharp, Does.Contain("// 通常のメソッドコメント"));
        });
        AssertGeneratedCompiles(result, compilation, "CommentedMethods");
    }
}
