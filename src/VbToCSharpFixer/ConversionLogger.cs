using System.Globalization;
using System.Text;

namespace VbToCSharpFixer;

public static class ConversionLogger
{
    /// <summary>通常ログの集計に到達しない中断も、例外の種類とスタックを追記して残します。</summary>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    /// <param name="exception">記録する例外。</param>
    /// <returns>非同期処理の完了を表すタスク。</returns>
    internal static async Task WriteFailureAsync(Options options, Exception exception)
    {
        var path = Path.Combine(options.Output, "logs", "error.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var input = options.Solution ?? options.Project ?? options.Folder ?? options.File;
            await File.AppendAllTextAsync(path,
                $"[{DateTimeOffset.Now:O}] Conversion aborted\nInput: {input}\nOutput: {options.Output}\nDry run: {options.DryRun}\n{exception}\n\n");
            Console.Error.WriteLine($"Error log: {path}");
        }
        catch (Exception logException)
        {
            // 出力先自体が書き込み不可でも、元の例外と終了コードをログ保存失敗で置き換えない。
            Console.Error.WriteLine($"Could not write error log '{path}': {logException.Message}");
        }
    }

    /// <summary>変換、コピー、プロジェクト処理、レビュー項目および集計ログを出力します。</summary>
    /// <param name="outputRoot">ログを出力するルートディレクトリ。</param>
    /// <param name="fixes">出力する変換結果一覧。</param>
    /// <param name="reviews">出力する手動確認項目一覧。</param>
    /// <param name="workspaceDiagnostics">Workspace読み込み時の診断一覧。</param>
    /// <param name="fileOperations">出力するファイル操作ログ一覧。</param>
    /// <param name="projectOperations">出力するプロジェクト操作ログ一覧。</param>
    /// <param name="files">出力するファイル操作ログ一覧。</param>
    /// <param name="dryRun">ファイルを書き込まず処理内容だけを確認する場合はtrue。</param>
    /// <param name="cancellationToken">処理のキャンセル要求を通知するトークン。</param>
    /// <returns>非同期処理の完了を表すタスク。</returns>
    public static async Task WriteAsync(string outputRoot, IReadOnlyList<FixResult> fixes,
        IReadOnlyList<ManualReviewItem> reviews, IReadOnlyList<string> workspaceDiagnostics,
        IReadOnlyList<FileCopyLogEntry> fileOperations,
        IReadOnlyList<ProjectConversionLogEntry> projectOperations,
        int files, bool dryRun, CancellationToken cancellationToken = default)
    {
        var logs = Path.Combine(outputRoot, "logs");
        Directory.CreateDirectory(logs);
        var detail = new StringBuilder();
        foreach (var f in fixes)
        {
            detail.AppendLine($"Project   : {f.Project}")
                .AppendLine($"File      : {f.File}").AppendLine($"Line      : {f.Line}")
                .AppendLine($"Column    : {f.Column}").AppendLine($"FixType   : {f.FixType}")
                .AppendLine($"Symbol    : {f.SymbolKind ?? "(none)"}")
                .AppendLine($"DeclaringType: {f.DeclaringType ?? "(none)"}")
                .AppendLine($"Assembly/Project: {f.AssemblyOrProject ?? "(none)"}")
                .AppendLine($"Before: {f.Before}").AppendLine($"After : {f.After}")
                .AppendLine($"Reason: {f.Reason}").AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(logs, "conversion.log"), detail.ToString(), cancellationToken);

        var csv = new StringBuilder("Project,File,Line,Column,Code,ReasonCode,Details\r\n");
        foreach (var r in reviews)
            csv.AppendJoin(',', Csv(r.Project), Csv(r.File), r.Line.ToString(CultureInfo.InvariantCulture),
                r.Column.ToString(CultureInfo.InvariantCulture), Csv(r.Code), r.ReasonCode.ToString(), Csv(r.Details)).Append("\r\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "manual-review.csv"), csv.ToString(), new UTF8Encoding(true), cancellationToken);

        var copyLog = new StringBuilder("Project,SourcePath,DestinationPath,ItemType,Action,Result,FileSize\r\n");
        foreach (var item in fileOperations)
            copyLog.AppendJoin(',', Csv(item.Project), Csv(item.SourcePath), Csv(item.DestinationPath),
                Csv(item.ItemType), Csv(item.Action), Csv(item.Result), item.FileSize?.ToString(CultureInfo.InvariantCulture) ?? "").Append("\r\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "file-copy.log"), copyLog.ToString(), new UTF8Encoding(true), cancellationToken);

        var projectLog = new StringBuilder();
        foreach (var item in projectOperations)
            projectLog.AppendLine($"Project: {item.Project}").AppendLine($"Source: {item.SourcePath}")
                .AppendLine($"Destination: {item.DestinationPath}").AppendLine($"Change: {item.Change}")
                .AppendLine($"Result: {item.Result}").AppendLine();
        await File.WriteAllTextAsync(Path.Combine(logs, "project-conversion.log"), projectLog.ToString(), cancellationToken);

        var summary = $"Files: {files}\nFixes: {fixes.Count}\nCopied or planned files: {fileOperations.Count}\nConverted projects/solutions: {projectOperations.Count}\nManualReviewRequired: {reviews.Count}\nWorkspace diagnostics: {workspaceDiagnostics.Count}\nDry run: {dryRun}\n";
        if (workspaceDiagnostics.Count > 0) summary += "\n" + string.Join("\n", workspaceDiagnostics);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "summary.txt"), summary, cancellationToken);
    }

    /// <summary>CSVフィールドとして安全な引用形式へエスケープします。</summary>
    /// <param name="value">処理対象の値。</param>
    /// <returns>生成または変換した文字列。</returns>
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
