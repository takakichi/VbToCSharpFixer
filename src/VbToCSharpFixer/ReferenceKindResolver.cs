using Microsoft.CodeAnalysis;
using CSharpCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation;

namespace VbToCSharpFixer;

/// <summary>同じCompilationの参照情報を再利用し、VBから失われるout属性を照合します。</summary>
internal sealed class ReferenceKindResolver
{
    // ファイル固有の状態と分離し、参照元Compilationが変わったときだけ作り直す。
    private Compilation? _referenceCompilationSource;
    private CSharpCompilation? _csharpReferenceCompilation;

    /// <summary>VBがRefへ正規化した参照先メタデータをC# Compilationで照合し、明示的なoutだけを復元します。</summary>
    internal RefKind Resolve(IParameterSymbol parameter, Compilation compilation)
    {
        if (parameter.RefKind != RefKind.Ref || parameter.ContainingSymbol is not IMethodSymbol method ||
            method.DeclaringSyntaxReferences.Length > 0)
            return parameter.RefKind;

        if (!ReferenceEquals(_referenceCompilationSource, compilation))
        {
            _referenceCompilationSource = compilation;
            _csharpReferenceCompilation = CSharpCompilation.Create(
                "VbToCSharpReferenceKinds", references: compilation.References);
        }

        var definition = method.ReducedFrom ?? method.OriginalDefinition;
        var documentationId = definition.GetDocumentationCommentId();
        if (documentationId is null || _csharpReferenceCompilation is null) return RefKind.Ref;
        var csharpMethod = DocumentationCommentId.GetFirstSymbolForDeclarationId(documentationId, _csharpReferenceCompilation) as IMethodSymbol;
        var ordinal = method.ReducedFrom is null ? parameter.Ordinal : parameter.Ordinal + 1;
        return csharpMethod is not null && ordinal < csharpMethod.Parameters.Length &&
               csharpMethod.Parameters[ordinal].RefKind == RefKind.Out
            ? RefKind.Out
            : RefKind.Ref;
    }
}
