using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public sealed class OutputLayout
{
    private readonly Options _options;
    private readonly string? _inputRoot;
    public string OutputBase { get; }
    public string ConversionRoot { get; }

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

    public string SourceDestination(Project project, string sourcePath)
    {
        var sourceDirectory = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
        var relative = sourceDirectory is null ? Path.GetFileName(sourcePath) : Path.GetRelativePath(sourceDirectory, sourcePath);
        relative = LegacyProjectMaterializer.MapProjectPath(Path.ChangeExtension(relative, ".cs"));
        return SafeCombine(ProjectDirectory(project), relative, project.Name);
    }

    public string PathInProject(Project project, string relativePath) =>
        SafeCombine(ProjectDirectory(project), LegacyProjectMaterializer.MapProjectPath(relativePath), project.Name);

    private string SafeCombine(string root, string relative, string project)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (IsWithin(fullRoot, full) && IsWithin(OutputBase, full)) return full;
        return Path.Combine(OutputBase, "_external", SafeName(project), SafeName(Path.GetFileName(relative)));
    }

    public static bool IsWithin(string root, string path)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
