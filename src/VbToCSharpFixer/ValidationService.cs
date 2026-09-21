using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace VbToCSharpFixer;

public sealed class ValidationService
{
    /// <summary>生成された単一C#ソースの構文エラーを返します。</summary>
    /// <param name="source">変換または検証対象のソースコード。</param>
    /// <param name="path">処理対象のファイルパス。</param>
    /// <returns>検出されたC#構文エラーの一覧。</returns>
    public IReadOnlyList<Diagnostic> ValidateSyntax(string source, string path) =>
        CSharpSyntaxTree.ParseText(source, path: path).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

    /// <summary>プロジェクト内の生成C#ソースをまとめてCompilationし、エラーを返します。</summary>
    /// <param name="sources">検証対象のソースコード一覧。</param>
    /// <param name="references">コンパイルに使用するメタデータ参照一覧。</param>
    /// <param name="assemblyName">検証用アセンブリ名。</param>
    /// <returns>検出されたC#コンパイルエラーの一覧。</returns>
    public IReadOnlyList<Diagnostic> ValidateCompilation(
        IEnumerable<(string Source, string Path)> sources,
        IEnumerable<MetadataReference> references,
        string assemblyName)
    {
        // 元プロジェクトの参照で生成ソース間の整合性を調べる補助検証。
        // 実際のターゲットフレームワーク、MSBuild設定、リソースの検証は生成プロジェクトのビルドで行う。
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

    /// <summary>異言語CompilationReferenceをC#から利用可能なメタデータ参照へ変換します。</summary>
    /// <param name="reference">処理対象のシンボル参照。</param>
    /// <returns>C#から参照可能なメタデータ参照。変換できない場合はnull。</returns>
    private static MetadataReference? ToPortableReference(MetadataReference reference)
    {
        if (reference is not CompilationReference compilationReference) return reference;
        using var stream = new MemoryStream();
        var result = compilationReference.Compilation.Emit(stream);
        return result.Success ? MetadataReference.CreateFromImage(stream.ToArray()) : null;
    }
}
