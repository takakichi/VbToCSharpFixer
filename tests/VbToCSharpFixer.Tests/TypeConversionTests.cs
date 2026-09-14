using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

using static VbToCSharpFixer.Tests.ConversionTestSupport;

[TestFixture]
public sealed class TypeConversionTests
{
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

    /// <summary>式の受信側に現れるStringなどのVB組み込み型をC#型名へ変換します。</summary>
    [Test]
    public void Converts_predefined_type_member_access_in_expressions()
    {
        var source = """
Imports System
Public Class PredefinedTypeUsage
    Public Function CreateError(value As Object) As Exception
        Return New Exception(String.Format("{0}", value))
    End Function
    Public Function EmptyText() As String
        Return String.Empty
    End Function
    Public Function ParseNumber(text As String) As Integer
        Return Integer.Parse(text)
    End Function
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "predefined-members.vb");
        var compilation = CreateCompilation(tree);
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.Multiple(() =>
        {
            Assert.That(result.CSharp, Does.Contain("new Exception(string.Format(\"{0}\", value))"));
            Assert.That(result.CSharp, Does.Contain("return string.Empty;"));
            Assert.That(result.CSharp, Does.Contain("return int.Parse(text);"));
            Assert.That(result.CSharp, Does.Not.Contain("default.Format"));
            Assert.That(result.CSharp, Does.Not.Contain("default.Empty"));
        });
        var compiled = CompileAndCreate(result, compilation, "PredefinedTypeUsage");
        var error = (Exception)compiled.Type.GetMethod("CreateError")!.Invoke(compiled.Instance, [123])!;
        Assert.That(error.Message, Is.EqualTo("123"));
        Assert.That(compiled.Type.GetMethod("ParseNumber")!.Invoke(compiled.Instance, ["42"]), Is.EqualTo(42));
    }
}
