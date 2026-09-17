using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class PropertyConversionTests
{
    // 文字列だけでなく、同じVBと生成C#を実行して評価順序を含めた振る舞いを比較する。
    private static ConversionResult Check(string source)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source);
        var vb = CreateCompilation(tree, "PropertyTest" + Guid.NewGuid().ToString("N"));
        using var stream = new MemoryStream();
        var emit = vb.Emit(stream);
        Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
        var originalType = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Example")!;
        var expected = originalType.GetMethod("Run")!.Invoke(Activator.CreateInstance(originalType), null);
        var result = new VbToCSharpConverter().Convert(tree, vb.GetSemanticModel(tree), "Test");
        Assert.That(result.ManualReviews, Is.Empty);
        var converted = CompileAndCreate(result, vb, "Example");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(expected), result.CSharp);
        return result;
    }

    [Test]
    public void Named_properties_preserve_names_shared_optional_named_arguments_and_private_setter()
    {
        var result = Check("""
Public Class Example
    Private Shared stored As Integer = 3
    Public Shared Property Name(Optional index As Integer = 1) As Integer
        Get
            Return stored + index
        End Get
        Private Set(newNumber As Integer)
            stored = newNumber - index
        End Set
    End Property
    Public ReadOnly Property Title(index As Integer) As Integer
        Get
            Return index * 2
        End Get
    End Property
    Public Function Run() As Integer
        Name(index:=2) = 10
        Name() += 4
        Return Name + Title(3)
    End Function
End Class
""");
        Assert.That(result.CSharp, Does.Not.Contain("this["));
        Assert.That(result.CSharp, Does.Contain("private static void Set_Name"));
    }

    [TestCase("Cell")]
    [TestCase("Item")]
    public void Default_property_explicit_implicit_and_with_access_keep_array_access(string name)
    {
        Check($$"""
Public Class Example
    Private data As Integer() = {1, 2, 3}
    Default Public Property {{name}}(index As Integer) As Integer
        Get
            Return data(index)
        End Get
        Set(number As Integer)
            data(index) = number
        End Set
    End Property
    Public Function Run() As Integer
        Dim other As Example = Me
        other.{{name}}(0) = 7
        With other
            .{{name}}(1) += 5
        End With
        Return {{name}}(0) + Me.{{name}}(1) + other(2)
    End Function
End Class
""");
    }

    [Test]
    public void Compound_assignment_evaluates_receiver_arguments_getter_rhs_setter_once_in_order()
    {
        Check("""
Public Class Example
    Private trace As String = ""
    Private stored As Integer = 5
    Public Property Number(index As Integer) As Integer
        Get
            trace &= "G"
            Return stored
        End Get
        Set(number As Integer)
            trace &= "S"
            stored = number
        End Set
    End Property
    Private Function Target() As Example
        trace &= "T"
        Return Me
    End Function
    Private Function Index() As Integer
        trace &= "I"
        Return 0
    End Function
    Private Function Right() As Integer
        trace &= "R"
        Return 4
    End Function
    Public Function Run() As String
        Target().Number(Index()) += Right()
        Return trace & stored.ToString()
    End Function
End Class
""");
    }

    [Test]
    public void Named_property_overloads_are_preserved()
    {
        Check("""
Public Class Example
    Public ReadOnly Property Name(index As Integer) As Integer
        Get
            Return index
        End Get
    End Property
    Public ReadOnly Property Name(index As String) As Integer
        Get
            Return index.Length
        End Get
    End Property
    Public Function Run() As Integer
        Return Name(2) + Name("abc")
    End Function
End Class
""");
    }

    [Test]
    public void More_than_one_hundred_properties_and_cross_file_references_compile()
    {
        var properties = string.Join("\n", Enumerable.Range(0, 120).Select(i => $$"""
Public ReadOnly Property Name{{i}}(index As Integer) As Integer
    Get
        Return index + {{i}}
    End Get
End Property
"""));
        var declarations = VisualBasicSyntaxTree.ParseText("Public Class Catalog\n" + properties + "\nEnd Class", path: "Catalog.vb");
        var caller = VisualBasicSyntaxTree.ParseText("""
Public Class Example
    Public Function Run() As Integer
        Dim catalog As Catalog = New Catalog()
        Return catalog.Name0(1) + catalog.Name119(2)
    End Function
End Class
""", path: "Example.vb");
        var vb = CreateCompilation(declarations).AddSyntaxTrees(caller);
        Assert.That(vb.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        var converter = new VbToCSharpConverter();
        var first = converter.Convert(declarations, vb.GetSemanticModel(declarations), "Test");
        var second = converter.Convert(caller, vb.GetSemanticModel(caller), "Test");
        Assert.That(first.CSharp, Does.Not.Contain("this["));
        var combined = first with { CSharp = first.CSharp + second.CSharp };
        var converted = CompileAndCreate(combined, vb, "Example");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(122));
    }

    [Test]
    public void Named_optional_arguments_preserve_source_evaluation_order_and_with_receiver()
    {
        Check("""
Public Class Example
    Private trace As String = ""
    Public Property Number(Optional first As Integer = 1, Optional second As Integer = 2) As Integer
        Get
            trace &= "G"
            Return first + second
        End Get
        Set(number As Integer)
            trace &= "S"
        End Set
    End Property
    Private Function Argument(text As String) As Integer
        trace &= text
        Return 3
    End Function
    Public Function Run() As String
        Dim target As Example = Me
        With target
            .Number(second:=Argument("B"), first:=Argument("A")) += Argument("R")
        End With
        Number(, Argument("C")) = 5
        Return trace
    End Function
End Class
""");
    }
}
