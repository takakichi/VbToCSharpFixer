using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

/// <summary>プロジェクト内の文書変換と、プロジェクト単位の検証を担当します。</summary>
internal sealed class ProjectSourceConverter
{
    private readonly VbToCSharpConverter _converter = new();
    private readonly ValidationService _validation = new();
    private readonly VisualBasicRuntimeReferenceService _runtimeReferences = new();

    /// <summary>プロジェクト内のVB文書を変換し、生成コードをプロジェクト単位で検証します。</summary>
    /// <param name="loaded">読み込み済みのVBプロジェクト。</param>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    /// <param name="layout">変換後ファイルの配置情報。</param>
    /// <param name="materialization">変換後のプロジェクト配置情報。</param>
    /// <param name="fixes">変換記録の格納先。</param>
    /// <param name="reviews">手動確認項目の格納先。</param>
    /// <param name="projectOperations">プロジェクト操作ログの格納先。</param>
    /// <returns>変換対象になった文書数を含むタスク。</returns>
    internal async Task<int> ConvertAsync(LoadedProject loaded, Options options, OutputLayout layout,
        MaterializationResult materialization, List<FixResult> fixes, List<ManualReviewItem> reviews,
        List<ProjectConversionLogEntry> projectOperations)
    {
        var fileCount = 0;
        var convertedSources = new List<(string Source, string Path)>();
        var visualBasicRuntimeTypes = new HashSet<string>(StringComparer.Ordinal);
        var compilationErrors = loaded.Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        // まず全ファイルを変換して収集し、その後にまとめてC# Compilationを作る。
        // ファイル単位の検証だけでは見つからない型参照やメンバー間の不整合もこれで検出できる。
        foreach (var document in loaded.Project.Documents.Where(d => !IsGeneratedBuildDocument(loaded.Project, d)))
        {
            var converted = await ConvertDocumentAsync(loaded, document, options, layout, materialization, compilationErrors, reviews);
            if (converted is null) continue;
            fileCount++;
            convertedSources.Add((converted.Result.CSharp, converted.Destination));
            fixes.AddRange(converted.Result.Fixes);
            visualBasicRuntimeTypes.UnionWith(converted.Result.VisualBasicRuntimeTypes);
        }

        foreach (var diagnostic in _validation.ValidateCompilation(
                     convertedSources, loaded.Compilation.References, loaded.Project.AssemblyName ?? loaded.Project.Name))
        {
            var p = diagnostic.Location.GetLineSpan();
            reviews.Add(new(loaded.Project.Name, p.Path, p.StartLinePosition.Line + 1,
                p.StartLinePosition.Character + 1, "", ReasonCode.CompilationError,
                "Generated C# compilation: " + diagnostic));
        }
        if (materialization.ProjectOutputDirectories.TryGetValue(loaded.Project.Id, out var projectOutputDirectory))
        {
            var referenceLog = await _runtimeReferences.EnsureReferenceAsync(
                loaded.Project, projectOutputDirectory, visualBasicRuntimeTypes.Count > 0, options.DryRun);
            if (referenceLog is not null) projectOperations.Add(referenceLog);
        }
        return fileCount;
    }

    /// <summary>文書を変換し、元VBと生成C#の診断を記録します。</summary>
    /// <param name="loaded">読み込み済みのVBプロジェクト。</param>
    /// <param name="document">処理対象のドキュメント。</param>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    /// <param name="layout">変換後ファイルの配置情報。</param>
    /// <param name="materialization">変換後のプロジェクト配置情報。</param>
    /// <param name="compilationErrors">元VBプロジェクトのコンパイルエラー一覧。</param>
    /// <param name="reviews">出力する手動確認項目一覧。</param>
    /// <returns>変換した文書。変換対象外の場合はnull。</returns>
    private async Task<ConvertedDocument?> ConvertDocumentAsync(LoadedProject loaded, Document document,
        Options options, OutputLayout layout, MaterializationResult materialization,
        IReadOnlyList<Diagnostic> compilationErrors, List<ManualReviewItem> reviews)
    {
        var tree = await document.GetSyntaxTreeAsync();
        if (tree is null) return null;
        var model = loaded.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var rootNamespace = (loaded.Compilation.Options as Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions)?.RootNamespace;
        var result = _converter.Convert(tree, model, loaded.Project.Name, rootNamespace);
        var destination = materialization.SourceOutputPaths.TryGetValue(document.Id, out var mappedDestination)
            ? mappedDestination
            : document.FilePath is null
                ? layout.PathInProject(loaded.Project, Path.ChangeExtension(document.Name, ".cs"))
                : layout.SourceDestination(loaded.Project, document.FilePath);
        // 変換判断 → 元VBの診断 → 生成C#の診断の順序をログでも維持する。
        reviews.AddRange(result.ManualReviews);
        foreach (var diagnostic in compilationErrors.Where(d => d.Location.SourceTree == tree))
        {
            var p = diagnostic.Location.GetLineSpan().StartLinePosition;
            reviews.Add(new(loaded.Project.Name, tree.FilePath, p.Line + 1, p.Character + 1,
                "", ReasonCode.CompilationError, diagnostic.ToString()));
        }

        if (!options.DryRun)
        {
            // 従来の動作に合わせ、単体の構文診断は保存時に記録する。
            // プロジェクト単位のCompilation検証はdry-runでも実行する。
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, result.CSharp);
            foreach (var diagnostic in _validation.ValidateSyntax(result.CSharp, destination))
            {
                var p = diagnostic.Location.GetLineSpan().StartLinePosition;
                reviews.Add(new(loaded.Project.Name, destination, p.Line + 1, p.Character + 1,
                    "", ReasonCode.UnsupportedSyntax, "Generated C# syntax: " + diagnostic));
            }
        }
        return new(destination, result);
    }

    private sealed record ConvertedDocument(string Destination, ConversionResult Result);

    /// <summary>MSBuildがobj配下へ生成したドキュメントを出力対象から除外します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="document">処理対象のドキュメント。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private static bool IsGeneratedBuildDocument(Project project, Document document)
    {
        if (document.FilePath is null || project.FilePath is null) return false;
        var projectDirectory = Path.GetDirectoryName(project.FilePath)!;
        var relative = Path.GetRelativePath(projectDirectory, document.FilePath);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
