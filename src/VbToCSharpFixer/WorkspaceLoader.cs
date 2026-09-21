using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace VbToCSharpFixer;

public sealed class WorkspaceLoader : IDisposable
{
    private readonly MSBuildWorkspace? _workspace;
    public List<string> Diagnostics { get; } = [];

    /// <summary>指定されたMSBuildワークスペースを保持するローダーを初期化します。</summary>
    /// <param name="workspace">保持するMSBuildWorkspace。不要な場合はnull。</param>
    private WorkspaceLoader(MSBuildWorkspace? workspace) => _workspace = workspace;

    /// <summary>CLIオプションに応じてSolution、Project、FolderまたはFileを読み込み、VB Compilationを構築します。</summary>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    /// <param name="cancellationToken">処理のキャンセル要求を通知するトークン。</param>
    /// <returns>読み込みに使用したローダーと、コンパイル済みVBプロジェクトの一覧を含むタスク。</returns>
    public static async Task<(WorkspaceLoader Loader, IReadOnlyList<LoadedProject> Projects)> LoadAsync(Options options, CancellationToken cancellationToken = default)
    {
        // Solution／Project入力ではMSBuildWorkspaceを使い、参照、Imports、Defineなど
        // 実際のプロジェクト設定を含むCompilationを取得する。
        if (options.Solution is not null || options.Project is not null)
        {
            if (!MSBuildLocator.IsRegistered)
                RegisterMsBuild();
            var workspace = MSBuildWorkspace.Create();
            var loader = new WorkspaceLoader(workspace);
            workspace.WorkspaceFailed += (_, e) => loader.Diagnostics.Add(e.Diagnostic.Message);
            IEnumerable<Project> projects;
            if (options.Solution is not null)
            {
                projects = (await workspace.OpenSolutionAsync(options.Solution, cancellationToken: cancellationToken)).Projects;
            }
            else
            {
                var rootProject = await workspace.OpenProjectAsync(options.Project!, cancellationToken: cancellationToken);
                projects = ReferencedProjectClosure(rootProject);
            }
            return (loader, await CompileVisualBasicProjects(projects, cancellationToken));
        }

        // Folder／File入力にはプロジェクト設定がないため、実行環境の参照だけを持つ
        // AdhocWorkspaceを構築して、同じ変換パイプラインへ渡す。
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

    /// <summary>ルートプロジェクトから到達できるProjectReferenceを再帰的に列挙します。</summary>
    /// <param name="root">処理対象のXMLルート要素。</param>
    /// <returns>ルートから参照可能なプロジェクトの一覧。</returns>
    private static IReadOnlyList<Project> ReferencedProjectClosure(Project root)
    {
        var result = new List<Project>();
        var pending = new Stack<Project>();
        var visited = new HashSet<ProjectId>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!visited.Add(project.Id)) continue;
            result.Add(project);
            foreach (var reference in project.ProjectReferences)
            {
                var referenced = project.Solution.GetProject(reference.ProjectId);
                if (referenced is not null) pending.Push(referenced);
            }
        }
        return result;
    }

    /// <summary>Visual Studioまたはスタンドアロン.NET SDKのMSBuildを検出して登録します。</summary>
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

    /// <summary>SDKディレクトリ名から比較可能なバージョンを取得します。</summary>
    /// <param name="value">処理対象の値。</param>
    /// <returns>比較可能なSDKバージョン。解析できない場合は0.0。</returns>
    private static Version ParseSdkVersion(string value)
    {
        var numeric = value.Split('-', 2)[0];
        return Version.TryParse(numeric, out var version) ? version : new Version(0, 0);
    }

    /// <summary>VBプロジェクトに限定してCompilationを非同期に生成します。</summary>
    /// <param name="projects">処理対象のプロジェクト一覧。</param>
    /// <param name="ct">処理のキャンセル要求を通知するトークン。</param>
    /// <returns>コンパイル済みVBプロジェクトの一覧を含むタスク。</returns>
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

    /// <summary>単一ファイル／フォルダ解析で利用する実行環境の参照アセンブリを列挙します。</summary>
    /// <returns>実行環境の各アセンブリを表すメタデータ参照。</returns>
    private static IEnumerable<MetadataReference> PlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));

    /// <summary>保持しているMSBuildWorkspaceを破棄します。</summary>
    public void Dispose() => _workspace?.Dispose();
}
