using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public enum FixType { MethodCall, ArrayAccess, Indexer, Property, Unchanged }
public enum ReasonCode
{
    UnresolvedSymbol, MissingReference, AmbiguousSymbol, UnsupportedDefaultProperty,
    UnsupportedIndexer, ProjectLoadFailure, CompilationError, UnsupportedSyntax,
    MissingProjectFile, MissingContentFile, UnsupportedProjectType, UnsupportedComReference,
    UnsupportedApplicationFramework, StartupObjectUnresolved, ResourceGeneratorConversionFailure,
    ExternalLinkedFile, OutputPathCollision, InvalidRelativePath, ProjectConversionFailure,
    SolutionConversionFailure, GeneratedProjectBuildFailure, WildcardProjectItem
}

public sealed record FixResult(
    string Project, string File, int Line, int Column, FixType FixType,
    string Before, string After, string Reason, string? SymbolKind,
    string? DeclaringType, string? AssemblyOrProject);

public sealed record ManualReviewItem(
    string Project, string File, int Line, int Column, string Code,
    ReasonCode ReasonCode, string Details);

public sealed record ConversionResult(
    string CSharp, IReadOnlyList<FixResult> Fixes,
    IReadOnlyList<ManualReviewItem> ManualReviews);

public sealed record LoadedProject(Project Project, Compilation Compilation);

public sealed record FileCopyLogEntry(
    string Project, string SourcePath, string DestinationPath, string ItemType,
    string Action, string Result, long? FileSize);

public sealed record ProjectConversionLogEntry(
    string Project, string SourcePath, string DestinationPath, string Change, string Result);

public sealed record MaterializationResult(
    IReadOnlyList<FileCopyLogEntry> FileOperations,
    IReadOnlyList<ProjectConversionLogEntry> ProjectOperations,
    IReadOnlyList<ManualReviewItem> ManualReviews,
    IReadOnlyDictionary<ProjectId, string> ProjectOutputDirectories,
    string ConversionRoot,
    string? BuildTarget);

public sealed record Options(
    string? Solution, string? Project, string? Folder, string? File,
    string Output, bool DryRun, bool Verbose, bool SkipBuild = false)
{
    public static Options Parse(string[] args)
    {
        string? solution = null, project = null, folder = null, file = null, output = null;
        var dryRun = false;
        var verbose = false;
        var skipBuild = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--solution": solution = Next(args, ref i); break;
                case "--project": project = Next(args, ref i); break;
                case "--folder": folder = Next(args, ref i); break;
                case "--file": file = Next(args, ref i); break;
                case "--output": output = Next(args, ref i); break;
                case "--dry-run": dryRun = true; break;
                case "--verbose": verbose = true; break;
                case "--skip-build": skipBuild = true; break;
                case "--help": throw new ArgumentException(Usage);
                default: throw new ArgumentException($"Unknown argument: {args[i]}\n{Usage}");
            }
        }

        var count = new[] { solution, project, folder, file }.Count(x => x is not null);
        if (count != 1 || output is null) throw new ArgumentException(Usage);
        return new(solution, project, folder, file, Path.GetFullPath(output), dryRun, verbose, skipBuild);
    }

    private static string Next(string[] args, ref int i) =>
        ++i < args.Length ? Path.GetFullPath(args[i]) : throw new ArgumentException(Usage);

    public const string Usage = "Usage: VbToCSharpFixer (--solution x.sln | --project x.vbproj | --folder dir | --file x.vb) --output dir [--dry-run] [--verbose] [--skip-build]";
}
