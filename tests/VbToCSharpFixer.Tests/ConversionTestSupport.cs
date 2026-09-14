using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

// 入力VBの解析・生成C#のコンパイル・実行を共通化する。期待値は各テスト側で明示する。
internal static class ConversionTestSupport
{
    internal const string Prelude = """
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

    /// <summary>VBソースの最後の初期化式と対応するSemanticModelを返します。</summary>
    internal static (ExpressionSyntax Expression, SemanticModel Model) ParseInitializer(string source)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "test.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var expression = tree.GetRoot().DescendantNodes().OfType<EqualsValueSyntax>().Last().Value;
        return (expression, compilation.GetSemanticModel(tree));
    }

    /// <summary>生成された単一C#ソースを元VB Compilationの参照でコンパイル検証します。</summary>
    internal static void AssertGeneratedCompiles(ConversionResult result, Compilation compilation, string name)
    {
        var errors = new ValidationService().ValidateCompilation(
            [(result.CSharp, name + ".cs")], compilation.References, name);
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
    }

    /// <summary>VBソースを変換してC# Assemblyをメモリへemitし、指定した型のインスタンスを返します。</summary>
    internal static (object Instance, Type Type) ConvertCompileAndCreate(string source, string typeName)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source, path: typeName + ".vb");
        var vbCompilation = CreateCompilation(tree, typeName + "Vb");
        var result = new VbToCSharpConverter().Convert(tree, vbCompilation.GetSemanticModel(tree), "Test");
        return CompileAndCreate(result, vbCompilation, typeName);
    }

    /// <summary>変換済みC#をメモリへemitし、指定した型のインスタンスを返します。</summary>
    internal static (object Instance, Type Type) CompileAndCreate(
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
    internal static VisualBasicCompilation CreateCompilation(SyntaxTree tree, string name = "Tests", params MetadataReference[] additional) =>
        VisualBasicCompilation.Create(name, [tree], PlatformReferences().Concat(additional),
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>テスト実行環境のTrusted Platform Assembliesを参照として列挙します。</summary>
    internal static IEnumerable<MetadataReference> PlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
}
