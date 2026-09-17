using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class ReportedConversionTests
{
    [Test]
    public void Parameterless_Name_property_keeps_its_name_and_private_setter()
    {
        var converted = Convert("""
Public Class Example
    Private _name As String
    Public Property Name() As String
        Get
            Return _name
        End Get
        Private Set(ByVal value As String)
            _name = value
        End Set
    End Property
End Class
""");
        Assert.Multiple(() =>
        {
            Assert.That(converted.Result.CSharp, Does.Contain("public string Name"));
            Assert.That(converted.Result.CSharp, Does.Contain("private set"));
            Assert.That(converted.Result.CSharp, Does.Not.Contain("this["));
        });
        var property = converted.Type.GetProperty("Name")!;
        Assert.That(property.GetGetMethod()!.IsPublic, Is.True);
        Assert.That(property.GetSetMethod(true)!.IsPrivate, Is.True);
        property.GetSetMethod(true)!.Invoke(converted.Instance, ["sample"]);
        Assert.That(property.GetValue(converted.Instance), Is.EqualTo("sample"));
    }

    [Test]
    public void Array_bounds_on_identifier_keep_name_and_literal_initializer()
    {
        var converted = Convert("""
Public Class Example
    Public Function Run() As Integer()
        Dim array2() As Integer = {1,2,3}
        Return array2
    End Function
End Class
""");
        Assert.That(converted.Result.CSharp, Does.Contain("int[] array2 = new int[] { 1, 2, 3 };"));
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    private static (ConversionResult Result, System.Type Type, object Instance) Convert(string source)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source, path: "Reported.vb");
        var compilation = CreateCompilation(tree);
        var errors = compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(x => x.ToString())));
        var result = new VbToCSharpConverter().Convert(tree, compilation.GetSemanticModel(tree), "Test");
        Assert.That(result.ManualReviews, Is.Empty);
        var compiled = CompileAndCreate(result, compilation, "Example");
        return (result, compiled.Type, compiled.Instance);
    }

    [Test]
    public void While_and_exit_sub_preserve_execution()
    {
        var converted = Convert("""
Public Class Example
    Public Result As Integer
    Public Sub Run()
        Dim i As Integer = 0
        While i < 6
            i += 1
            If i = 2 Then Continue While
            If i = 4 Then Exit While
            Result += i
        End While
        Exit Sub
        Result = 100
    End Sub
End Class
""");
        converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null);
        Assert.That(converted.Type.GetField("Result")!.GetValue(converted.Instance), Is.EqualTo(4));
        Assert.That(converted.Result.CSharp, Does.Contain("while ("));
        Assert.That(converted.Result.CSharp, Does.Contain("return;"));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Mixed_nested_loops_transfer_to_the_requested_kind(bool outerWhile, bool continuation)
    {
        // Exit/Continue Whileが内側のForへ、Exit/Continue Forが内側のWhileへ誤って作用しない。
        var outerStart = outerWhile ? "While i < 3\ni += 1" : "For i = 1 To 3";
        var outerEnd = outerWhile ? "End While" : "Next";
        var innerStart = outerWhile ? "For j As Integer = 1 To 3" : "Dim j As Integer = 0\nWhile j < 3\nj += 1";
        var innerEnd = outerWhile ? "Next" : "End While";
        var transfer = (continuation ? "Continue " : "Exit ") + (outerWhile ? "While" : "For");
        var converted = Convert($$"""
Public Class Example
    Public Function Run() As Integer
        Dim total As Integer = 0
        Dim i As Integer = 0
        {{outerStart}}
            {{innerStart}}
                If j = 2 Then {{transfer}}
                total += 1
            {{innerEnd}}
            total += 100
        {{outerEnd}}
        Return total
    End Function
End Class
""");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(continuation ? 3 : 1));
    }

    [Test]
    public void Property_default_access_and_setter_parameter_are_preserved()
    {
        var converted = Convert("""
Public Class Example
    Private stored As Integer
    Property Number As Integer
        Get
            Return stored
        End Get
        Set(ByVal newNumber As Integer)
            stored = newNumber
        End Set
    End Property
    Public Property Restricted As Integer
        Get
            Return stored
        End Get
        Private Set(ByVal Value As Integer)
            stored = Value
        End Set
    End Property
    Friend Property InternalNumber As Integer
End Class
""");
        var number = converted.Type.GetProperty("Number")!;
        Assert.That(number, Is.Not.Null);
        number.SetValue(converted.Instance, 42);
        Assert.That(number.GetValue(converted.Instance), Is.EqualTo(42));
        Assert.That(converted.Type.GetProperty("Restricted")!.GetSetMethod(true)!.IsPrivate, Is.True);
        Assert.That(converted.Result.CSharp, Does.Contain("internal int InternalNumber"));
    }

    [Test]
    public void Default_parameterized_property_is_a_public_csharp_indexer()
    {
        var converted = Convert("""
Public Class Example
    Private data As Integer() = {1, 2, 3}
    Default Property Item(index As Integer) As Integer
        Get
            Return data(index)
        End Get
        Set(ByVal newValue As Integer)
            data(index) = newValue
        End Set
    End Property
End Class
""");
        Assert.That(converted.Result.CSharp, Does.Contain("public int this[int index]"));
        var indexer = converted.Type.GetProperty("Item")!;
        indexer.SetValue(converted.Instance, 42, [1]);
        Assert.That(indexer.GetValue(converted.Instance, [1]), Is.EqualTo(42));
    }

    [Test]
    public void Array_literals_work_in_fields_locals_assignments_and_return_values()
    {
        var converted = Convert("""
Public Class Example
    Public Data As Integer() = {1, 2, 3}
    Public Function Run() As Integer
        Dim numbers As Integer() = {4, 5}
        numbers = {6, 7}
        Dim inferred = {8, 9}
        Dim matrix As Integer(,) = {{1, 2}, {3, 4}}
        Dim jagged As Integer()() = {New Integer() {10}, New Integer() {11}}
        Return Data(0) + numbers(0) + inferred(0) + matrix(1, 1) + jagged(0)(0)
    End Function
    Public Function Values() As Integer()
        Return {12, 13}
    End Function
End Class
""");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(29));
        Assert.That(converted.Type.GetMethod("Values")!.Invoke(converted.Instance, null), Is.EqualTo(new[] { 12, 13 }));
    }
}
