using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class ObjectDeclarationConversionTests
{
    [TestCase("Dim obj As New clsAAAA()", "obj", 1)]
    [TestCase("Dim obj As New clsAAAA", "obj", 1)]
    [TestCase("Dim obj As New clsAAAA(7)", "obj", 7)]
    [TestCase("Dim obj As clsAAAA = New clsAAAA(7)", "obj", 7)]
    [TestCase("Dim obj = New clsAAAA(7)", "obj", 7)]
    [TestCase("Dim first, obj As New clsAAAA(7)\nfirst.Value = 99", "obj", 7)]
    public void Local_object_initialization_is_preserved(string declaration, string variable, int expected)
    {
        Verify($$"""
Public Class Example
    Public Function Run() As Integer
        {{declaration}}
        Return {{variable}}.Value
    End Function
End Class
""", expected);
    }

    [Test]
    public void Instance_and_shared_fields_create_distinct_objects()
    {
        Verify("""
Public Class Example
    Private first, second As New clsAAAA(7)
    Private Shared sharedObject As New clsAAAA(3)
    Public Function Run() As Integer
        first.Value = 99
        Return second.Value + sharedObject.Value
    End Function
End Class
""", 10);
    }

    [Test]
    public void Declaration_without_New_does_not_invent_an_instance()
    {
        var result = Verify("""
Public Class Example
    Private missing As clsAAAA
    Public Function Run() As Integer
        Dim obj As clsAAAA
        obj = New clsAAAA(4)
        If missing Is Nothing Then Return obj.Value
        Return -1
    End Function
End Class
""", 4);
        Assert.That(result.CSharp, Does.Contain("clsAAAA obj;").And.Contain("clsAAAA missing;"));
    }

    private static ConversionResult Verify(string source, int expected)
    {
        source += """

Public Class clsAAAA
    Public Value As Integer
    Public Sub New()
        Value = 1
    End Sub
    Public Sub New(number As Integer)
        Value = number
    End Sub
End Class
""";
        var tree = VisualBasicSyntaxTree.ParseText(source);
        var vb = CreateCompilation(tree, "ObjectDeclarations" + Guid.NewGuid().ToString("N"));
        using var stream = new MemoryStream();
        var emitted = vb.Emit(stream);
        Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
        var original = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Example")!;
        Assert.That(original.GetMethod("Run")!.Invoke(Activator.CreateInstance(original), null), Is.EqualTo(expected));
        var result = new VbToCSharpConverter().Convert(tree, vb.GetSemanticModel(tree), "Test");
        Assert.That(result.ManualReviews, Is.Empty);
        var converted = CompileAndCreate(result, vb, "Example");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(expected), result.CSharp);
        return result;
    }
}
