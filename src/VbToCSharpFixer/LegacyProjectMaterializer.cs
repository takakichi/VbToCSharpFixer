using System.Text;
using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public sealed class LegacyProjectMaterializer
{
    public const string VisualBasicProjectTypeGuid = "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}";
    public const string CSharpProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    /// <summary>Solution、旧形式Projectおよび関連ファイルをC#出力構成として生成します。</summary>
    public async Task<MaterializationResult> MaterializeAsync(
        Options options, IReadOnlyList<LoadedProject> projects, CancellationToken cancellationToken = default)
    {
        var layout = new OutputLayout(options);
        var files = new List<FileCopyLogEntry>();
        var changes = new List<ProjectConversionLogEntry>();
        var reviews = new List<ManualReviewItem>();
        var sourceOutputs = new Dictionary<DocumentId, string>();
        var directories = projects.ToDictionary(x => x.Project.Id, x => layout.ProjectDirectory(x.Project));
        var projectOutputs = projects.Where(x => x.Project.FilePath is not null).ToDictionary(
            x => Path.GetFullPath(x.Project.FilePath!),
            x => Path.Combine(layout.ProjectDirectory(x.Project), Path.ChangeExtension(Path.GetFileName(x.Project.FilePath), ".csproj")!),
            StringComparer.OrdinalIgnoreCase);

        if (options.Solution is not null)
            await ConvertSolutionAsync(options, layout, projects, files, changes, reviews, cancellationToken);

        foreach (var loaded in projects)
        {
            if (loaded.Project.FilePath is null) continue;
            var plan = ProjectConversionPlanner.CreatePlan(options, layout, loaded.Project, projectOutputs,
                sourceOutputs, files, changes, reviews);
            if (plan is not null) await plan.ExecuteAsync(options.DryRun, cancellationToken);
        }

        var buildTarget = options.Solution is not null
            ? Path.Combine(layout.ConversionRoot, Path.GetFileName(options.Solution))
            : options.Project is not null && projectOutputs.TryGetValue(Path.GetFullPath(options.Project), out var projectTarget)
                ? projectTarget : null;
        return new(files, changes, reviews, directories, sourceOutputs, layout.ConversionRoot, buildTarget);
    }

    /// <summary>Solution内のVBプロジェクトパスとProject Type GUIDをC#用に変換します。</summary>
    private static async Task ConvertSolutionAsync(Options options, OutputLayout layout,
        IReadOnlyList<LoadedProject> projects, List<FileCopyLogEntry> files,
        List<ProjectConversionLogEntry> changes, List<ManualReviewItem> reviews, CancellationToken ct)
    {
        var source = options.Solution!;
        var destination = Path.Combine(layout.ConversionRoot, Path.GetFileName(source));
        try
        {
            var content = await File.ReadAllTextAsync(source, ct);
            foreach (var project in projects.Select(x => x.Project).Where(x => x.FilePath is not null))
            {
                var oldRelative = Path.GetRelativePath(Path.GetDirectoryName(source)!, project.FilePath!);
                var newProject = Path.Combine(layout.ProjectDirectory(project), Path.ChangeExtension(Path.GetFileName(project.FilePath), ".csproj")!);
                var newRelative = Path.GetRelativePath(layout.ConversionRoot, newProject);
                content = content.Replace(oldRelative, newRelative, StringComparison.OrdinalIgnoreCase)
                    .Replace(oldRelative.Replace('\\', '/'), newRelative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            content = content.Replace(VisualBasicProjectTypeGuid, CSharpProjectTypeGuid, StringComparison.OrdinalIgnoreCase);
            if (!options.DryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, content, DetectEncoding(source), ct);
            }
            files.Add(new("Solution", source, destination, "Solution", "Convert", options.DryRun ? "Planned" : "Written", new FileInfo(source).Length));
            changes.Add(new("Solution", source, destination, "VB project paths and project type GUIDs converted", "Success"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reviews.Add(Review("Solution", source, ReasonCode.SolutionConversionFailure, e.Message));
        }
    }

    /// <summary>既存の呼び出し元との互換性を保つため、共通の配置規則へ委譲します。</summary>
    public static string MapProjectPath(string value) => ProjectPathMapper.Map(value);

    /// <summary>プロジェクト構成に関するManualReviewRequired項目を生成します。</summary>
    private static ManualReviewItem Review(string project, string path, ReasonCode reason, string details) =>
        new(project, path, 0, 0, "", reason, details);

    /// <summary>元ファイルのBOMを考慮してテキストエンコーディングを検出します。</summary>
    private static Encoding DetectEncoding(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        _ = reader.Peek();
        return reader.CurrentEncoding;
    }
}
