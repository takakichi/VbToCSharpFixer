using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace VbToCSharpFixer;

public sealed class WorkspaceLoader : IDisposable
{
    private readonly MSBuildWorkspace? _workspace;
    public List<string> Diagnostics { get; } = [];

    private WorkspaceLoader(MSBuildWorkspace? workspace) => _workspace = workspace;

    public static async Task<(WorkspaceLoader Loader, IReadOnlyList<LoadedProject> Projects)> LoadAsync(Options options, CancellationToken cancellationToken = default)
    {
        if (options.Solution is not null || options.Project is not null)
        {
            if (!MSBuildLocator.IsRegistered)
                RegisterMsBuild();
            var workspace = MSBuildWorkspace.Create();
            var loader = new WorkspaceLoader(workspace);
            workspace.WorkspaceFailed += (_, e) => loader.Diagnostics.Add(e.Diagnostic.Message);
            var projects = options.Solution is not null
                ? (await workspace.OpenSolutionAsync(options.Solution, cancellationToken: cancellationToken)).Projects
                : [await workspace.OpenProjectAsync(options.Project!, cancellationToken: cancellationToken)];
            return (loader, await CompileVisualBasicProjects(projects, cancellationToken));
        }

        var files = options.Folder is not null
            ? Directory.EnumerateFiles(options.Folder, "*.vb", SearchOption.AllDirectories)
            : [options.File!];
        var adhoc = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(projectId, VersionStamp.Create(), "Input", "Input", LanguageNames.VisualBasic,
            compilationOptions: new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest),
            metadataReferences: PlatformReferences());
        var solution = adhoc.CurrentSolution.AddProject(info);
        foreach (var path in files)
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), Path.GetFileName(path),
                SourceText.From(await System.IO.File.ReadAllTextAsync(path, cancellationToken)), filePath: Path.GetFullPath(path));
        var project = solution.GetProject(projectId)!;
        var compilation = (await project.GetCompilationAsync(cancellationToken))!;
        return (new WorkspaceLoader(null), [new LoadedProject(project, compilation)]);
    }

    private static void RegisterMsBuild()
    {
        var instance = MSBuildLocator.QueryVisualStudioInstances()
            .OrderByDescending(x => x.Version).FirstOrDefault();
        if (instance is not null)
        {
            MSBuildLocator.RegisterInstance(instance);
            return;
        }

        // A machine may have only a standalone dotnet SDK, which older Locator
        // versions do not report as a Visual Studio instance.
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        var sdk = roots.SelectMany(root =>
                Directory.Exists(Path.Combine(root!, "sdk"))
                    ? Directory.EnumerateDirectories(Path.Combine(root!, "sdk"))
                    : [])
            .Where(path => File.Exists(Path.Combine(path, "MSBuild.dll")))
            .Select(path => new { Path = path, Version = ParseSdkVersion(Path.GetFileName(path)) })
            .OrderByDescending(x => x.Version).FirstOrDefault();
        if (sdk is null)
            throw new InvalidOperationException("MSBuild was not found. Install Visual Studio Build Tools or a .NET SDK.");
        MSBuildLocator.RegisterMSBuildPath(sdk.Path);
    }

    private static Version ParseSdkVersion(string value)
    {
        var numeric = value.Split('-', 2)[0];
        return Version.TryParse(numeric, out var version) ? version : new Version(0, 0);
    }

    private static async Task<IReadOnlyList<LoadedProject>> CompileVisualBasicProjects(IEnumerable<Project> projects, CancellationToken ct)
    {
        var result = new List<LoadedProject>();
        foreach (var project in projects.Where(p => p.Language == LanguageNames.VisualBasic))
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null) result.Add(new(project, compilation));
        }
        return result;
    }

    private static IEnumerable<MetadataReference> PlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));

    public void Dispose() => _workspace?.Dispose();
}
