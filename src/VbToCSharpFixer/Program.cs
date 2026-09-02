using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            Directory.CreateDirectory(options.Output);
            var (loader, projects) = await WorkspaceLoader.LoadAsync(options);
            using (loader)
            {
                var fixes = new List<FixResult>();
                var reviews = new List<ManualReviewItem>();
                var converter = new VbToCSharpConverter();
                var validation = new ValidationService();
                var fileCount = 0;
                foreach (var loaded in projects)
                {
                    var convertedSources = new List<(string Source, string Path)>();
                    var compilationErrors = loaded.Compilation.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                    foreach (var document in loaded.Project.Documents.Where(d => !IsGeneratedBuildDocument(loaded.Project, d)))
                    {
                        var tree = await document.GetSyntaxTreeAsync();
                        if (tree is null) continue;
                        fileCount++;
                        var model = loaded.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                        var result = converter.Convert(tree, model, loaded.Project.Name);
                        var relative = RelativeSourcePath(loaded.Project, document);
                        var destination = Path.Combine(options.Output, "converted", SafeName(loaded.Project.Name), Path.ChangeExtension(relative, ".cs"));
                        convertedSources.Add((result.CSharp, destination));
                        fixes.AddRange(result.Fixes);
                        reviews.AddRange(result.ManualReviews);
                        foreach (var diagnostic in compilationErrors.Where(d => d.Location.SourceTree == tree))
                        {
                            var p = diagnostic.Location.GetLineSpan().StartLinePosition;
                            reviews.Add(new(loaded.Project.Name, tree.FilePath, p.Line + 1, p.Character + 1,
                                "", ReasonCode.CompilationError, diagnostic.ToString()));
                        }

                        if (!options.DryRun)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            await File.WriteAllTextAsync(destination, result.CSharp);
                            foreach (var diagnostic in validation.ValidateSyntax(result.CSharp, destination))
                            {
                                var p = diagnostic.Location.GetLineSpan().StartLinePosition;
                                reviews.Add(new(loaded.Project.Name, destination, p.Line + 1, p.Character + 1,
                                    "", ReasonCode.UnsupportedSyntax, "Generated C# syntax: " + diagnostic));
                            }
                        }
                    }

                    foreach (var diagnostic in validation.ValidateCompilation(
                                 convertedSources, loaded.Compilation.References, loaded.Project.AssemblyName ?? loaded.Project.Name))
                    {
                        var p = diagnostic.Location.GetLineSpan();
                        reviews.Add(new(loaded.Project.Name, p.Path, p.StartLinePosition.Line + 1,
                            p.StartLinePosition.Character + 1, "", ReasonCode.CompilationError,
                            "Generated C# compilation: " + diagnostic));
                    }
                }
                await ConversionLogger.WriteAsync(options.Output, fixes, reviews, loader.Diagnostics, fileCount, options.DryRun);
                Console.WriteLine($"Processed {fileCount} file(s), {fixes.Count} semantic fix(es), {reviews.Count} manual review item(s).");
                Console.WriteLine($"Output: {options.Output}");
                return reviews.Count == 0 ? 0 : 2;
            }
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static string RelativeSourcePath(Project project, Document document)
    {
        if (document.FilePath is null) return document.Name;
        var root = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
        return root is null ? document.Name : Path.GetRelativePath(root, document.FilePath);
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static bool IsGeneratedBuildDocument(Project project, Document document)
    {
        if (document.FilePath is null || project.FilePath is null) return false;
        var projectDirectory = Path.GetDirectoryName(project.FilePath)!;
        var relative = Path.GetRelativePath(projectDirectory, document.FilePath);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
