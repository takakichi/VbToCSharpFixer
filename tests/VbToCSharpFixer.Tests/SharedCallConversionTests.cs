using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using NUnit.Framework;
using static VbToCSharpFixer.Tests.ConversionTestSupport;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class SharedCallConversionTests
{
    [TestCase("Method()")]
    [TestCase("Method")]
    [TestCase("method(3)")]
    [TestCase("ClassName.Method(3)")]
    public void Imported_class_shared_function_is_qualified(string expression)
    {
        var result = Check($$"""
Imports Services.ClassName
Namespace Services
    Public Class ClassName
        Public Shared Function Method(Optional value As Integer = 7) As Integer
            Return value
        End Function
    End Class
End Namespace
Public Class Example
    Public Function Run() As Integer
        Return {{expression.Replace("ClassName.Method", "Services.ClassName.Method")}}
    End Function
End Class
""");
        if (!expression.StartsWith("ClassName")) Assert.That(result, Does.Contain("global::Services.ClassName.Method("));
        Assert.That(result, Does.Contain("using static global::Services.ClassName;"));
    }

    [Test]
    public void Module_sub_and_function_without_imports_are_qualified()
    {
        var result = Check("""
Public Module Utilities
    Public counter As Integer = 0
    Public Sub Increment()
        counter += 1
    End Sub
    Public Function ReadCount() As Integer
        Return counter
    End Function
End Module
Public Class Example
    Public Function Run() As Integer
        Increment()
        Call Increment()
        Increment
        Return ReadCount()
    End Function
End Class
""");
        Assert.That(result, Does.Contain("global::Utilities.Increment();"));
    }

    [Test]
    public void Same_class_inherited_and_instance_methods_keep_their_targets()
    {
        var result = Check("""
Public Class Parent
    Protected Shared Function InheritedValue() As Integer
        Return 2
    End Function
End Class
Public Class Example
    Inherits Parent
    Public Shared Function OwnValue() As Integer
        Return 3
    End Function
    Public Function InstanceValue() As Integer
        Return 4
    End Function
    Public Function Run() As Integer
        Return OwnValue() + InheritedValue() + InstanceValue() + Me.InstanceValue()
    End Function
End Class
""");
        Assert.That(result, Does.Contain("return OwnValue() + InheritedValue() + InstanceValue() + this.InstanceValue();"));
    }

    [Test]
    public void Framework_static_method_import_is_valid_csharp()
    {
        Assert.That(Check("""
Imports System.Math
Public Class Example
    Public Function Run() As Integer
        Return Abs(-5)
    End Function
End Class
"""), Does.Contain("global::System.Math.Abs("));
    }

    private static string Check(string source)
    {
        var tree = VisualBasicSyntaxTree.ParseText(source);
        var vb = CreateCompilation(tree, "SharedCalls" + Guid.NewGuid().ToString("N"));
        using var stream = new MemoryStream();
        var emit = vb.Emit(stream);
        Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
        var original = System.Reflection.Assembly.Load(stream.ToArray()).GetType("Example")!;
        var expected = original.GetMethod("Run")!.Invoke(Activator.CreateInstance(original), null);
        var result = new VbToCSharpConverter().Convert(tree, vb.GetSemanticModel(tree), "Test");
        Assert.That(result.ManualReviews, Is.Empty);
        var converted = CompileAndCreate(result, vb, "Example");
        Assert.That(converted.Type.GetMethod("Run")!.Invoke(converted.Instance, null), Is.EqualTo(expected), result.CSharp);
        return result.CSharp;
    }
}
