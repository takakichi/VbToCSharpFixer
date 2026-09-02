using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace VbToCSharpFixer;

public sealed class ValidationService
{
    public IReadOnlyList<Diagnostic> ValidateSyntax(string source, string path) =>
        CSharpSyntaxTree.ParseText(source, path: path).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

    public IReadOnlyList<Diagnostic> ValidateCompilation(
        IEnumerable<(string Source, string Path)> sources,
        IEnumerable<MetadataReference> references,
        string assemblyName)
    {
        var trees = sources.Select(x => CSharpSyntaxTree.ParseText(x.Source, path: x.Path));
        var compatibleReferences = references.Select(ToPortableReference).OfType<MetadataReference>();
        return CSharpCompilation.Create(
                assemblyName + ".Converted",
                trees,
                compatibleReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    private static MetadataReference? ToPortableReference(MetadataReference reference)
    {
        if (reference is not CompilationReference compilationReference) return reference;
        using var stream = new MemoryStream();
        var result = compilationReference.Compilation.Emit(stream);
        return result.Success ? MetadataReference.CreateFromImage(stream.ToArray()) : null;
    }
}
