using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using static VbToCSharpFixer.LegacyProjectMaterializer;

namespace VbToCSharpFixer;

/// <summary>旧形式プロジェクトのXMLを編集し、コピーと保存の計画を組み立てます。</summary>
internal static class ProjectConversionPlanner
{
    private static readonly HashSet<string> FileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "Content", "None", "EmbeddedResource", "Resource", "AdditionalFiles",
        "ApplicationDefinition", "Page", "SplashScreen", "EntityDeploy", "TypeScriptCompile"
    };

    /// <summary>旧形式VBプロジェクトXMLをC#用に編集し、書き込みを行わず出力計画を返します。</summary>
    internal static ProjectMaterializationPlan? CreatePlan(Options options, OutputLayout layout, Project project,
        IReadOnlyDictionary<string, string> projectOutputs,
        Dictionary<DocumentId, string> sourceOutputs,
        List<FileCopyLogEntry> files, List<ProjectConversionLogEntry> changes,
        List<ManualReviewItem> reviews)
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
            return null;
        }

        var plan = new ProjectMaterializationPlan(document, destinationProject);
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
                PlanImportIfLocal(project, projectDirectory, layout, attribute.Value, options, files, reviews, plan);
        }

        foreach (var item in root.Descendants().Where(x => x.Attribute("Include") is not null).ToArray())
        {
            var itemType = item.Name.LocalName;
            var include = item.Attribute("Include")!.Value;
            if (ContainsWildcard(include))
            {
                plan.Record(() => reviews.Add(Review(project.Name, sourceProject, ReasonCode.WildcardProjectItem, $"{itemType}: {include}")));
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
                    plan.Record(() => reviews.Add(Review(project.Name, referencedSource, ReasonCode.MissingProjectFile,
                        $"Referenced VB project was not loaded for conversion: {include}")));
                }
                continue;
            }
            if (itemType.Equals("Reference", StringComparison.OrdinalIgnoreCase))
            {
                var hintPath = item.Elements().FirstOrDefault(x => x.Name.LocalName == "HintPath");
                if (hintPath is not null)
                {
                    var copied = PlanCopyItem(project, projectDirectory, layout, hintPath.Value, "Reference", options, files, reviews, plan);
                    if (copied is not null)
                        hintPath.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
                }
                continue;
            }
            if (itemType is "COMReference" or "COMFileReference")
            {
                plan.Record(() => reviews.Add(Review(project.Name, sourceProject, ReasonCode.UnsupportedComReference, $"COM reference retained: {include}")));
                if (itemType == "COMFileReference")
                    PlanCopyItem(project, projectDirectory, layout, include, itemType, options, files, reviews, plan);
                continue;
            }
            if (!FileItemTypes.Contains(itemType)) continue;

            var originalInclude = include;
            var linkElement = item.Elements().FirstOrDefault(x => x.Name.LocalName == "Link");
            var link = linkElement?.Value;
            var isVisualBasicSource = Path.GetExtension(include).Equals(".vb", StringComparison.OrdinalIgnoreCase);
            if (isVisualBasicSource)
                include = Path.ChangeExtension(link ?? include, ".cs");
            include = MapProjectPath(include);
            item.Attribute("Include")!.Value = include;
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
                var destination = layout.PathInProject(project, include);
                MapSourceDocument(project, projectDirectory, originalInclude, link, destination,
                    sourceOutputs, plannedOutputs, reviews, plan);
                linkElement?.Remove();
            }
            else
            {
                var copied = PlanCopyItem(project, projectDirectory, layout, originalInclude, itemType, options, files, reviews, plan, link);
                if (copied is not null)
                {
                    item.Attribute("Include")!.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
                    linkElement?.Remove();
                }
            }
        }

        // コピー後に実在ファイルも含めて照合する。dry-runではコピーせず従来どおり検証する。
        plan.ValidateResources = () => ValidateResourceParents(project, sourceProject, destinationProject, root, sourceOutputs, reviews);

        plan.RecordCompletion = () =>
        {
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
        };
        return plan;
    }

    /// <summary>VB Compile項目をRoslyn Documentへ対応付け、Linkを考慮したC#出力先を登録します。</summary>
    private static void MapSourceDocument(Project project, string projectDirectory, string include, string? link,
        string destination, Dictionary<DocumentId, string> sourceOutputs,
        Dictionary<string, DocumentId> plannedOutputs, List<ManualReviewItem> reviews, ProjectMaterializationPlan plan)
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
            plan.Record(() => reviews.Add(Review(project.Name, source, ReasonCode.MissingContentFile,
                $"VB Compile item was not loaded as a Roslyn document: {include}")));
            return;
        }
        // この辞書の検査対象は当該プロジェクトのVB Compile項目。全出力ファイルの衝突検査ではない。
        if (plannedOutputs.TryGetValue(destination, out var existing) && existing != selected.Id)
        {
            plan.Record(() => reviews.Add(Review(project.Name, source, ReasonCode.OutputPathCollision,
                $"Multiple VB Compile items map to the same output path: {destination}")));
            return;
        }
        plannedOutputs[destination] = selected.Id;
        sourceOutputs[selected.Id] = destination;
    }

    /// <summary>EmbeddedResourceのDependentUponが変換後Compile項目と同じ論理フォルダーで一致するか検証します。</summary>
    private static void ValidateResourceParents(Project project, string sourceProject, string destinationProject,
        XElement root, IReadOnlyDictionary<DocumentId, string> sourceOutputs, List<ManualReviewItem> reviews)
    {
        var compilePaths = root.Descendants().Where(x => x.Name.LocalName == "Compile")
            .Select(x => x.Attribute("Include")?.Value).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => NormalizeProjectPath(x!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedPaths = sourceOutputs.Where(x => project.GetDocument(x.Key) is not null).Select(x => Path.GetFullPath(x.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectOutput = Path.GetDirectoryName(destinationProject)!;
        foreach (var resource in root.Descendants().Where(x => x.Name.LocalName == "EmbeddedResource"))
        {
            var include = resource.Attribute("Include")?.Value;
            var dependentUpon = resource.Elements().FirstOrDefault(x => x.Name.LocalName == "DependentUpon")?.Value;
            if (string.IsNullOrWhiteSpace(include) || string.IsNullOrWhiteSpace(dependentUpon)) continue;
            var directory = Path.GetDirectoryName(include) ?? "";
            var parent = NormalizeProjectPath(Path.Combine(directory, dependentUpon));
            var parentPath = Path.GetFullPath(Path.Combine(projectOutput, parent));
            if (!compilePaths.Contains(parent) || !plannedPaths.Contains(parentPath) && !File.Exists(parentPath))
                reviews.Add(Review(project.Name, sourceProject, ReasonCode.ResourceParentMismatch,
                    $"EmbeddedResource parent does not match a generated Compile item: {include} -> {parent}"));
        }
    }

    /// <summary>Project XML内の相対パスを比較用の区切り文字へ正規化します。</summary>
    private static string NormalizeProjectPath(string value)
    {
        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var currentPrefix = "." + Path.DirectorySeparatorChar;
        while (normalized.StartsWith(currentPrefix, StringComparison.Ordinal)) normalized = normalized[currentPrefix.Length..];
        return normalized;
    }

    /// <summary>固定の相対パスを持つカスタムImportだけをコピー計画へ登録します。</summary>
    private static void PlanImportIfLocal(Project project, string projectDirectory, OutputLayout layout,
        string import, Options options, List<FileCopyLogEntry> files, List<ManualReviewItem> reviews, ProjectMaterializationPlan plan)
    {
        if (import.Contains("$(", StringComparison.Ordinal) || Path.IsPathRooted(import)) return;
        PlanCopyItem(project, projectDirectory, layout, import, "Import", options, files, reviews, plan);
    }

    /// <summary>コピー元と出力先を検証して計画へ登録し、出力予定パスを返します。</summary>
    private static string? PlanCopyItem(Project project, string projectDirectory, OutputLayout layout,
        string include, string itemType, Options options, List<FileCopyLogEntry> files,
        List<ManualReviewItem> reviews, ProjectMaterializationPlan plan, string? link = null)
    {
        var source = Path.GetFullPath(Path.Combine(projectDirectory, include));
        var relativeDestination = link ?? include;
        var destination = layout.PathInProject(project, relativeDestination);
        if (!File.Exists(source))
        {
            var code = itemType == "Reference" ? ReasonCode.MissingReference : ReasonCode.MissingContentFile;
            plan.Record(() => reviews.Add(Review(project.Name, source, code, $"Project item not found: {include}")));
            plan.Record(() => files.Add(new(project.Name, source, destination, itemType, "Copy", "Missing", null)));
            return null;
        }
        if (!OutputLayout.IsWithin(layout.OutputBase, destination))
        {
            plan.Record(() => reviews.Add(Review(project.Name, source, ReasonCode.InvalidRelativePath, $"Unsafe output path: {destination}")));
            return null;
        }
        plan.AddCopy(source, destination, () =>
        {
            files.Add(new(project.Name, source, destination, itemType, "Copy", options.DryRun ? "Planned" : "Copied", new FileInfo(source).Length));
            if (!OutputLayout.IsWithin(projectDirectory, source))
                reviews.Add(Review(project.Name, source, ReasonCode.ExternalLinkedFile, $"External linked file copied to {destination}"));
        });
        return destination;
    }

    /// <summary>MSBuild項目パスにワイルドカードが含まれるか判定します。</summary>
    private static bool ContainsWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    /// <summary>プロジェクト構成に関するManualReviewRequired項目を生成します。</summary>
    private static ManualReviewItem Review(string project, string path, ReasonCode reason, string details) =>
        new(project, path, 0, 0, "", reason, details);

}
