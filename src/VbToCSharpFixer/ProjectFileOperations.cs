using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

/// <summary>項目単位のコピーとXML保存を担当します。後続項目の処理を予約しません。</summary>
internal static class ProjectFileOperations
{
    /// <summary>プロジェクト項目を安全な出力パスへコピーし、実際の出力先を返します。</summary>
    internal static async Task<string?> CopyItemAsync(Project project, string projectDirectory, OutputLayout layout,
        string include, string itemType, Options options, List<FileCopyLogEntry> files,
        List<ManualReviewItem> reviews, CancellationToken ct, string? link = null)
    {
        var source = Path.GetFullPath(Path.Combine(projectDirectory, include));
        var relativeDestination = link ?? include;
        var destination = layout.PathInProject(project, relativeDestination);
        // 後続項目の存在確認を先取りしない。直前のコピーや外部の更新を反映した状態で判断する。
        // dry-runではコピーを行わないため、先行項目の出力を実在するものとして扱わない。
        if (!File.Exists(source))
        {
            var code = itemType == "Reference" ? ReasonCode.MissingReference : ReasonCode.MissingContentFile;
            reviews.Add(Review(project.Name, source, code, $"Project item not found: {include}"));
            files.Add(new(project.Name, source, destination, itemType, "Copy", "Missing", null));
            return null;
        }
        if (!OutputLayout.IsWithin(layout.OutputBase, destination))
        {
            reviews.Add(Review(project.Name, source, ReasonCode.InvalidRelativePath, $"Unsafe output path: {destination}"));
            return null;
        }
        if (!options.DryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, ct);
        }
        files.Add(new(project.Name, source, destination, itemType, "Copy", options.DryRun ? "Planned" : "Copied", new FileInfo(source).Length));
        if (!OutputLayout.IsWithin(projectDirectory, source))
            reviews.Add(Review(project.Name, source, ReasonCode.ExternalLinkedFile, $"External linked file copied to {destination}"));
        return destination;
    }
    
    /// <summary>全項目の処理とリソース親の検証を終えたXMLを保存します。</summary>
    internal static async Task SaveProjectAsync(XDocument document, string destination, bool dryRun, CancellationToken ct)
    {
        if (dryRun) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var stream = File.Create(destination);
        await document.SaveAsync(stream, SaveOptions.DisableFormatting, ct);
    }

    private static ManualReviewItem Review(string project, string path, ReasonCode reason, string details) =>
        new(project, path, 0, 0, "", reason, details);
}
