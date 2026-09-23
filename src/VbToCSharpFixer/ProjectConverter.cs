using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using static VbToCSharpFixer.LegacyProjectMaterializer;

namespace VbToCSharpFixer;

/// <summary>旧形式プロジェクトの項目を順番に処理し、コピー結果をXMLへ反映します。</summary>
internal static class ProjectConverter
{
    private static readonly HashSet<string> FileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "Content", "None", "EmbeddedResource", "Resource", "AdditionalFiles",
        "ApplicationDefinition", "Page", "SplashScreen", "EntityDeploy", "TypeScriptCompile"
    };

    /// <summary>項目ごとに確認・コピー・記録を完了させ、最後に変換済みXMLを保存します。</summary>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    /// <param name="layout">変換後ファイルの配置情報。</param>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="projectOutputs">プロジェクトIDと変換先ディレクトリの対応表。</param>
    /// <param name="sourceOutputs">文書IDと変換先ソースパスの対応表。</param>
    /// <param name="files">ファイル操作ログの格納先。</param>
    /// <param name="changes">プロジェクト変換ログの格納先。</param>
    /// <param name="reviews">出力する手動確認項目一覧。</param>
    /// <param name="ct">処理のキャンセル要求を通知するトークン。</param>
    /// <returns>非同期処理の完了を表すタスク。</returns>
    internal static async Task ConvertAsync(Options options, OutputLayout layout, Project project,
        IReadOnlyDictionary<string, string> projectOutputs,
        Dictionary<DocumentId, string> sourceOutputs,
        List<FileCopyLogEntry> files, List<ProjectConversionLogEntry> changes,
        List<ManualReviewItem> reviews, CancellationToken ct, LinkedFileLayout linkedLayout)
    {
        var sourceProject = project.FilePath!;
        var projectDirectory = Path.GetDirectoryName(sourceProject)!;
        var destinationProject = Path.Combine(layout.ProjectDirectory(project), Path.ChangeExtension(Path.GetFileName(sourceProject), ".csproj"));
        XDocument document;
        try
        {
            document = XDocument.Load(sourceProject, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            reviews.Add(Review(project.Name, sourceProject, ReasonCode.ProjectConversionFailure, e.Message));
            return;
        }
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var plannedOutputs = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.Descendants())
        {
            if (property.Name.LocalName == "ProjectTypeGuids" && property.Value.Contains(VisualBasicProjectTypeGuid, StringComparison.OrdinalIgnoreCase))
                property.Value = property.Value.Replace(VisualBasicProjectTypeGuid, CSharpProjectTypeGuid, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var import in root.Descendants(ns + "Import"))
        {
            var attribute = import.Attribute("Project");
            if (attribute is null) continue;
            if (attribute.Value.Contains("Microsoft.VisualBasic.targets", StringComparison.OrdinalIgnoreCase))
                attribute.Value = attribute.Value.Replace("Microsoft.VisualBasic.targets", "Microsoft.CSharp.targets", StringComparison.OrdinalIgnoreCase);
            else
                await CopyImportIfLocalAsync(project, projectDirectory, layout, attribute.Value, options, files, reviews, ct);
        }

        // 先行コピーが後続項目の入力になる場合もある。存在確認を全項目分先取りしない。
        // 各awaitの完了後に次の項目へ進み、9/14版と同じファイル状態を参照する。
        foreach (var item in root.Descendants().Where(x => x.Attribute("Include") is not null).ToArray())
        {
            var itemType = item.Name.LocalName;
            var include = item.Attribute("Include")!.Value;
            if (ContainsWildcard(include))
            {
                reviews.Add(Review(project.Name, sourceProject, ReasonCode.WildcardProjectItem, $"{itemType}: {include}"));
                continue;
            }
            if (itemType.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
            {
                var referencedSource = Path.GetFullPath(Path.Combine(projectDirectory, include));
                if (projectOutputs.TryGetValue(referencedSource, out var referencedOutput))
                    item.Attribute("Include")!.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, referencedOutput);
                else if (Path.GetExtension(include).Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    item.Attribute("Include")!.Value = Path.ChangeExtension(include, ".csproj");
                    reviews.Add(Review(project.Name, referencedSource, ReasonCode.MissingProjectFile,
                        $"Referenced VB project was not loaded for conversion: {include}"));
                }
                continue;
            }
            if (itemType.Equals("Reference", StringComparison.OrdinalIgnoreCase))
            {
                var hintPath = item.Elements().FirstOrDefault(x => x.Name.LocalName == "HintPath");
                if (hintPath is not null)
                {
                    var copied = await ProjectFileOperations.CopyItemAsync(project, projectDirectory, layout, hintPath.Value, "Reference", options, files, reviews, ct);
                    if (copied is not null)
                        hintPath.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
                }
                continue;
            }
            if (itemType is "COMReference" or "COMFileReference")
            {
                reviews.Add(Review(project.Name, sourceProject, ReasonCode.UnsupportedComReference, $"COM reference retained: {include}"));
                if (itemType == "COMFileReference")
                    await ProjectFileOperations.CopyItemAsync(project, projectDirectory, layout, include, itemType, options, files, reviews, ct);
                continue;
            }
            if (!FileItemTypes.Contains(itemType)) continue;

            var originalInclude = include;
            var linkElement = item.Elements().FirstOrDefault(x => x.Name.LocalName == "Link");
            var link = linkElement?.Value;
            var linkedDestination = linkElement is null ? null : linkedLayout.Destination(Path.Combine(projectDirectory, originalInclude));
            var isVisualBasicSource = Path.GetExtension(include).Equals(".vb", StringComparison.OrdinalIgnoreCase);
            if (isVisualBasicSource)
                include = Path.ChangeExtension(link ?? include, ".cs");
            include = MapProjectPath(include);
            item.Attribute("Include")!.Value = include;
            if (linkedDestination is not null)
            {
                item.Attribute("Include")!.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, linkedDestination);
                if (isVisualBasicSource) linkElement!.Value = Path.ChangeExtension(link!, ".cs");
            }
            foreach (var metadata in item.Elements().Where(x => x.Name.LocalName is "DependentUpon" or "LastGenOutput"))
            {
                if (Path.GetExtension(metadata.Value).Equals(".vb", StringComparison.OrdinalIgnoreCase))
                    metadata.Value = Path.ChangeExtension(metadata.Value, ".cs");
            }
            foreach (var generator in item.Elements().Where(x => x.Name.LocalName == "Generator"))
            {
                if (generator.Value.Equals("VbMyResourcesResXFileCodeGenerator", StringComparison.OrdinalIgnoreCase))
                    generator.Value = "ResXFileCodeGenerator";
            }

            if (isVisualBasicSource)
            {
                var destination = linkedDestination ?? layout.PathInProject(project, include);
                MapSourceDocument(project, projectDirectory, originalInclude, link, destination,
                    sourceOutputs, plannedOutputs, reviews);
            }
            else
            {
                var copied = await ProjectFileOperations.CopyItemAsync(project, projectDirectory, layout, originalInclude, itemType, options, files, reviews, ct, link, linkedDestination);
                if (copied is not null)
                {
                    item.Attribute("Include")!.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
                }
            }
        }

        ValidateResourceParents(project, sourceProject, destinationProject, root, sourceOutputs, reviews);
        await ProjectFileOperations.SaveProjectAsync(document, destinationProject, options.DryRun, ct);

        files.Add(new(project.Name, sourceProject, destinationProject, "Project", "Convert", options.DryRun ? "Planned" : "Written", new FileInfo(sourceProject).Length));
        changes.Add(new(project.Name, sourceProject, destinationProject,
            "Compile paths, ProjectReference, ProjectTypeGuids and Microsoft.CSharp.targets", "Success"));

        var applicationFile = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "None" &&
            x.Attribute("Include")?.Value.EndsWith("Application.myapp", StringComparison.OrdinalIgnoreCase) == true);
        if (applicationFile is not null)
            reviews.Add(Review(project.Name, sourceProject, ReasonCode.UnsupportedApplicationFramework,
                "Application.myapp was copied, but VB Application Framework requires manual C# startup conversion."));
        var startupObject = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "StartupObject" && !string.IsNullOrWhiteSpace(x.Value));
        if (startupObject is not null)
            reviews.Add(Review(project.Name, sourceProject, ReasonCode.StartupObjectUnresolved,
                $"StartupObject was retained and must be verified for C#: {startupObject.Value}"));
    }

    /// <summary>VB Compile項目をRoslyn Documentへ対応付け、Linkを考慮したC#出力先を登録します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="projectDirectory">元プロジェクトのディレクトリ。</param>
    /// <param name="include">プロジェクト項目のIncludeパス。</param>
    /// <param name="link">プロジェクト項目のLinkパス。</param>
    /// <param name="destination">出力先のパス。</param>
    /// <param name="sourceOutputs">文書IDと変換先ソースパスの対応表。</param>
    /// <param name="plannedOutputs">重複確認に使用する変換予定パス一覧。</param>
    /// <param name="reviews">出力する手動確認項目一覧。</param>
    private static void MapSourceDocument(Project project, string projectDirectory, string include, string? link,
        string destination, Dictionary<DocumentId, string> sourceOutputs,
        Dictionary<string, DocumentId> plannedOutputs, List<ManualReviewItem> reviews)
    {
        var source = Path.GetFullPath(Path.Combine(projectDirectory, include));
        var candidates = project.Documents.Where(document => document.FilePath is not null &&
            Path.GetFullPath(document.FilePath).Equals(source, StringComparison.OrdinalIgnoreCase) &&
            !sourceOutputs.ContainsKey(document.Id)).ToArray();
        if (!string.IsNullOrWhiteSpace(link))
        {
            // 同じ実ファイルを複数の論理パスで参照する場合がある。実パスだけでなくLinkを照合する。
            var logical = NormalizeProjectPath(link);
            var linked = candidates.Where(document =>
                NormalizeProjectPath(Path.Combine(document.Folders.Concat([document.Name]).ToArray()))
                    .Equals(logical, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (linked.Length > 0) candidates = linked;
        }
        var selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            reviews.Add(Review(project.Name, source, ReasonCode.MissingContentFile,
                $"VB Compile item was not loaded as a Roslyn document: {include}"));
            return;
        }
        // この辞書の検査対象は当該プロジェクトのVB Compile項目。全出力ファイルの衝突検査ではない。
        if (plannedOutputs.TryGetValue(destination, out var existing) && existing != selected.Id)
        {
            reviews.Add(Review(project.Name, source, ReasonCode.OutputPathCollision,
                $"Multiple VB Compile items map to the same output path: {destination}"));
            return;
        }
        plannedOutputs[destination] = selected.Id;
        sourceOutputs[selected.Id] = destination;
    }

    /// <summary>EmbeddedResourceのDependentUponが変換後Compile項目と同じ論理フォルダーで一致するか検証します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="sourceProject">変換元プロジェクトのパス。</param>
    /// <param name="destinationProject">変換後プロジェクトのパス。</param>
    /// <param name="root">処理対象のXMLルート要素。</param>
    /// <param name="sourceOutputs">文書IDと変換先ソースパスの対応表。</param>
    /// <param name="reviews">出力する手動確認項目一覧。</param>
    private static void ValidateResourceParents(Project project, string sourceProject, string destinationProject,
        XElement root, IReadOnlyDictionary<DocumentId, string> sourceOutputs, List<ManualReviewItem> reviews)
    {
        var compilePaths = root.Descendants().Where(x => x.Name.LocalName == "Compile")
            .Where(x => !string.IsNullOrWhiteSpace(x.Attribute("Include")?.Value))
            .ToLookup(x => NormalizeProjectPath(x.Elements().FirstOrDefault(e => e.Name.LocalName == "Link")?.Value ?? x.Attribute("Include")!.Value),
                x => x.Attribute("Include")!.Value, StringComparer.OrdinalIgnoreCase);
        var plannedPaths = sourceOutputs.Where(x => project.GetDocument(x.Key) is not null).Select(x => Path.GetFullPath(x.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectOutput = Path.GetDirectoryName(destinationProject)!;
        foreach (var resource in root.Descendants().Where(x => x.Name.LocalName == "EmbeddedResource"))
        {
            var include = resource.Attribute("Include")?.Value;
            var dependentUpon = resource.Elements().FirstOrDefault(x => x.Name.LocalName == "DependentUpon")?.Value;
            if (string.IsNullOrWhiteSpace(include) || string.IsNullOrWhiteSpace(dependentUpon)) continue;
            // DependentUponは実ファイルの配置ではなく、Linkを含むプロジェクト上の論理配置に対する名前。
            var logical = resource.Elements().FirstOrDefault(x => x.Name.LocalName == "Link")?.Value ?? include;
            var directory = Path.GetDirectoryName(logical) ?? "";
            var parent = NormalizeProjectPath(Path.Combine(directory, dependentUpon));
            if (!compilePaths[parent].Any(path =>
                plannedPaths.Contains(Path.GetFullPath(Path.Combine(projectOutput, path))) || File.Exists(Path.Combine(projectOutput, path))))
                reviews.Add(Review(project.Name, sourceProject, ReasonCode.ResourceParentMismatch,
                    $"EmbeddedResource parent does not match a generated Compile item: {include} -> {parent}"));
        }
    }

    /// <summary>Project XML内の相対パスを比較用の区切り文字へ正規化します。</summary>
    /// <param name="value">処理対象の値。</param>
    /// <returns>生成または変換した文字列。</returns>
    private static string NormalizeProjectPath(string value)
    {
        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var currentPrefix = "." + Path.DirectorySeparatorChar;
        while (normalized.StartsWith(currentPrefix, StringComparison.Ordinal)) normalized = normalized[currentPrefix.Length..];
        return normalized;
    }

    /// <summary>固定の相対パスを持つカスタムImportを、後続項目の確認より先にコピーします。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="projectDirectory">元プロジェクトのディレクトリ。</param>
    /// <param name="layout">変換後ファイルの配置情報。</param>
    /// <param name="import">コピー対象のImport要素。</param>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    /// <param name="files">ファイル操作ログの格納先。</param>
    /// <param name="reviews">出力する手動確認項目一覧。</param>
    /// <param name="ct">処理のキャンセル要求を通知するトークン。</param>
    /// <returns>非同期処理の完了を表すタスク。</returns>
    private static async Task CopyImportIfLocalAsync(Project project, string projectDirectory, OutputLayout layout,
        string import, Options options, List<FileCopyLogEntry> files, List<ManualReviewItem> reviews, CancellationToken ct)
    {
        if (import.Contains("$(", StringComparison.Ordinal) || Path.IsPathRooted(import)) return;
        await ProjectFileOperations.CopyItemAsync(project, projectDirectory, layout, import, "Import", options, files, reviews, ct);
    }

    /// <summary>MSBuild項目パスにワイルドカードが含まれるか判定します。</summary>
    /// <param name="value">処理対象の値。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private static bool ContainsWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    /// <summary>プロジェクト構成に関するManualReviewRequired項目を生成します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="path">処理対象のファイルパス。</param>
    /// <param name="reason">手動確認が必要になった理由。</param>
    /// <param name="details">ログに記録する詳細説明。</param>
    /// <returns>生成した手動確認項目。</returns>
    private static ManualReviewItem Review(string project, string path, ReasonCode reason, string details) =>
        new(project, path, 0, 0, "", reason, details);

}
