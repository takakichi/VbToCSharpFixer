using System.Globalization;
using System.Text;

namespace VbToCSharpFixer;

public static class ConversionLogger
{
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

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
