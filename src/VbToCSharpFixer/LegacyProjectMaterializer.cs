using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public sealed class LegacyProjectMaterializer
{
    public const string VisualBasicProjectTypeGuid = "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}";
    public const string CSharpProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
    private static readonly HashSet<string> FileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "Content", "None", "EmbeddedResource", "Resource", "AdditionalFiles",
        "ApplicationDefinition", "Page", "SplashScreen", "EntityDeploy", "TypeScriptCompile"
    };

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
            await ConvertProjectAsync(options, layout, loaded.Project, projectOutputs, sourceOutputs,
                files, changes, reviews, cancellationToken);
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

    /// <summary>旧形式VBプロジェクトXMLをC#用に変換し、登録ファイルをコピーします。</summary>
    private static async Task ConvertProjectAsync(Options options, OutputLayout layout, Project project,
        IReadOnlyDictionary<string, string> projectOutputs,
        Dictionary<DocumentId, string> sourceOutputs,
        List<FileCopyLogEntry> files, List<ProjectConversionLogEntry> changes,
        List<ManualReviewItem> reviews, CancellationToken ct)
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
                await CopyImportIfLocal(project, projectDirectory, layout, attribute.Value, options, files, reviews, ct);
        }

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
                    var copied = await CopyItemAsync(project, projectDirectory, layout, hintPath.Value, "Reference", options, files, reviews, ct);
                    if (copied is not null)
                        hintPath.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
                }
                continue;
            }
            if (itemType is "COMReference" or "COMFileReference")
            {
                reviews.Add(Review(project.Name, sourceProject, ReasonCode.UnsupportedComReference, $"COM reference retained: {include}"));
                if (itemType == "COMFileReference")
                    await CopyItemAsync(project, projectDirectory, layout, include, itemType, options, files, reviews, ct);
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
                    sourceOutputs, plannedOutputs, reviews);
                linkElement?.Remove();
            }
            else
            {
                var copied = await CopyItemAsync(project, projectDirectory, layout, originalInclude, itemType, options, files, reviews, ct, link);
                if (copied is not null)
                {
                    item.Attribute("Include")!.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
                    linkElement?.Remove();
                }
            }
        }

        ValidateResourceParents(project, sourceProject, destinationProject, root, sourceOutputs, reviews);

        if (!options.DryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationProject)!);
            await using var stream = File.Create(destinationProject);
            await document.SaveAsync(stream, SaveOptions.DisableFormatting, ct);
        }
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

    /// <summary>プロジェクト相対のカスタムMSBuild Importファイルを必要に応じてコピーします。</summary>
    private static async Task CopyImportIfLocal(Project project, string projectDirectory, OutputLayout layout,
        string import, Options options, List<FileCopyLogEntry> files, List<ManualReviewItem> reviews, CancellationToken ct)
    {
        if (import.Contains("$(", StringComparison.Ordinal) || Path.IsPathRooted(import)) return;
        await CopyItemAsync(project, projectDirectory, layout, import, "Import", options, files, reviews, ct);
    }

    /// <summary>プロジェクト項目を安全な出力パスへコピーし、実際の出力先を返します。</summary>
    private static async Task<string?> CopyItemAsync(Project project, string projectDirectory, OutputLayout layout,
        string include, string itemType, Options options, List<FileCopyLogEntry> files,
        List<ManualReviewItem> reviews, CancellationToken ct, string? link = null)
    {
        var source = Path.GetFullPath(Path.Combine(projectDirectory, include));
        var relativeDestination = link ?? include;
        var destination = layout.PathInProject(project, relativeDestination);
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

    /// <summary>VBのMy Project配下にある標準ファイルをC#のProperties構成へ割り当てます。</summary>
    public static string MapProjectPath(string value)
    {
        var normalized = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar);
        if (parts.Length > 1 && parts[0].Equals("My Project", StringComparison.OrdinalIgnoreCase) &&
            (parts[1].StartsWith("Resources", StringComparison.OrdinalIgnoreCase) ||
             parts[1].StartsWith("Settings", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("app.manifest", StringComparison.OrdinalIgnoreCase)))
        {
            parts[0] = "Properties";
            return string.Join(Path.DirectorySeparatorChar, parts);
        }
        return normalized;
    }

    /// <summary>MSBuild項目パスにワイルドカードが含まれるか判定します。</summary>
    private static bool ContainsWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

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
