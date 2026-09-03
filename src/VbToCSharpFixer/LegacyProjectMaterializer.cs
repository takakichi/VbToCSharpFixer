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

    public async Task<MaterializationResult> MaterializeAsync(
        Options options, IReadOnlyList<LoadedProject> projects, CancellationToken cancellationToken = default)
    {
        var layout = new OutputLayout(options);
        var files = new List<FileCopyLogEntry>();
        var changes = new List<ProjectConversionLogEntry>();
        var reviews = new List<ManualReviewItem>();
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
            await ConvertProjectAsync(options, layout, loaded.Project, projectOutputs, files, changes, reviews, cancellationToken);
        }

        var buildTarget = options.Solution is not null
            ? Path.Combine(layout.ConversionRoot, Path.GetFileName(options.Solution))
            : options.Project is not null && projectOutputs.TryGetValue(Path.GetFullPath(options.Project), out var projectTarget)
                ? projectTarget : null;
        return new(files, changes, reviews, directories, layout.ConversionRoot, buildTarget);
    }

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

    private static async Task ConvertProjectAsync(Options options, OutputLayout layout, Project project,
        IReadOnlyDictionary<string, string> projectOutputs,
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
            if (Path.GetExtension(include).Equals(".vb", StringComparison.OrdinalIgnoreCase))
                include = Path.ChangeExtension(include, ".cs");
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

            if (!Path.GetExtension(originalInclude).Equals(".vb", StringComparison.OrdinalIgnoreCase))
            {
                var link = item.Elements().FirstOrDefault(x => x.Name.LocalName == "Link")?.Value;
                var copied = await CopyItemAsync(project, projectDirectory, layout, originalInclude, itemType, options, files, reviews, ct, link);
                if (copied is not null)
                    item.Attribute("Include")!.Value = Path.GetRelativePath(Path.GetDirectoryName(destinationProject)!, copied);
            }
        }

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

    private static async Task CopyImportIfLocal(Project project, string projectDirectory, OutputLayout layout,
        string import, Options options, List<FileCopyLogEntry> files, List<ManualReviewItem> reviews, CancellationToken ct)
    {
        if (import.Contains("$(", StringComparison.Ordinal) || Path.IsPathRooted(import)) return;
        await CopyItemAsync(project, projectDirectory, layout, import, "Import", options, files, reviews, ct);
    }

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

    private static bool ContainsWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;
    private static ManualReviewItem Review(string project, string path, ReasonCode reason, string details) =>
        new(project, path, 0, 0, "", reason, details);

    private static Encoding DetectEncoding(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        _ = reader.Peek();
        return reader.CurrentEncoding;
    }
}
