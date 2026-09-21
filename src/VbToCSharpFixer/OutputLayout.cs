using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public sealed class OutputLayout
{
    private readonly Options _options;
    private readonly string? _inputRoot;
    public string OutputBase { get; }
    public string ConversionRoot { get; }

    /// <summary>入力種別と出力先から変換成果物の基準ディレクトリを決定します。</summary>
    /// <param name="options">変換処理に使用するコマンドラインオプション。</param>
    public OutputLayout(Options options)
    {
        _options = options;
        _inputRoot = options.Solution is not null ? Path.GetDirectoryName(options.Solution) :
            options.Project is not null ? Path.GetDirectoryName(options.Project) :
            options.Folder is not null ? options.Folder : Path.GetDirectoryName(options.File!);
        var name = options.Solution is not null ? Path.GetFileNameWithoutExtension(options.Solution) :
            options.Project is not null ? Path.GetFileNameWithoutExtension(options.Project) : "Input";
        OutputBase = Path.Combine(options.Output, "converted");
        ConversionRoot = Path.Combine(OutputBase, SafeName(name));
    }

    /// <summary>指定プロジェクトの出力ディレクトリを返します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <returns>プロジェクトの出力ディレクトリ。</returns>
    public string ProjectDirectory(Project project)
    {
        if (project.FilePath is null) return Path.Combine(ConversionRoot, SafeName(project.Name));
        var sourceDirectory = Path.GetDirectoryName(project.FilePath)!;
        if (_options.Project is not null)
        {
            if (Path.GetFullPath(project.FilePath).Equals(Path.GetFullPath(_options.Project), StringComparison.OrdinalIgnoreCase))
                return ConversionRoot;
            return Path.Combine(OutputBase, SafeName(project.Name));
        }
        if (_options.Solution is null) return ConversionRoot;
        var relative = Path.GetRelativePath(_inputRoot!, sourceDirectory);
        return SafeCombine(ConversionRoot, relative, project.Name);
    }

    /// <summary>VBソースに対応するC#ソースの安全な出力パスを返します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="sourcePath">コピー元ファイルのパス。</param>
    /// <returns>C#ソースの出力先絶対パス。</returns>
    public string SourceDestination(Project project, string sourcePath)
    {
        var sourceDirectory = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
        var relative = sourceDirectory is null ? Path.GetFileName(sourcePath) : Path.GetRelativePath(sourceDirectory, sourcePath);
        relative = ProjectPathMapper.Map(Path.ChangeExtension(relative, ".cs"));
        return SafeCombine(ProjectDirectory(project), relative, project.Name);
    }

    /// <summary>プロジェクト相対パスを出力側の絶対パスへ変換します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="relativePath">基準ディレクトリからの相対パス。</param>
    /// <returns>プロジェクト出力側の絶対パス。</returns>
    public string PathInProject(Project project, string relativePath) =>
        SafeCombine(ProjectDirectory(project), ProjectPathMapper.Map(relativePath), project.Name);

    /// <summary>出力領域外への逸脱を防ぎながらルートと相対パスを結合します。</summary>
    /// <param name="root">処理対象のXMLルート要素。</param>
    /// <param name="relative">検証する相対パス。</param>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <returns>出力領域内に収まる結合済み絶対パス。</returns>
    private string SafeCombine(string root, string relative, string project)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (IsWithin(fullRoot, full) && IsWithin(OutputBase, full)) return full;
        // 相対パスがプロジェクト外を指す場合は出力領域内へ退避する。
        // ここでは配置だけを決める。同名ファイルの一意性は保証しない。
        return Path.Combine(OutputBase, "_external", SafeName(project), SafeName(Path.GetFileName(relative)));
    }

    /// <summary>指定パスがルート自身またはルート配下に存在するか判定します。</summary>
    /// <param name="root">処理対象のXMLルート要素。</param>
    /// <param name="path">処理対象のファイルパス。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    public static bool IsWithin(string root, string path)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ファイル名に利用できない文字を置換して安全な名前を返します。</summary>
    /// <param name="name">処理対象の名前。</param>
    /// <returns>無効な文字を置換したファイル名。</returns>
    public static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
