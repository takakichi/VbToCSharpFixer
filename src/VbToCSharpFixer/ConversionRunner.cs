namespace VbToCSharpFixer;

/// <summary>読み込み、構成出力、ソース変換、検証、ログ出力の順序を管理します。</summary>
internal sealed class ConversionRunner
{
    internal async Task<ConversionRunSummary> RunAsync(Options options)
    {
        Directory.CreateDirectory(options.Output);
        var (loader, projects) = await WorkspaceLoader.LoadAsync(options);
        using (loader)
        {
            var fixes = new List<FixResult>();
            var reviews = new List<ManualReviewItem>();
            var materialization = await new LegacyProjectMaterializer().MaterializeAsync(options, projects);
            reviews.AddRange(materialization.ManualReviews);
            var layout = new OutputLayout(options);
            var projectConverter = new ProjectSourceConverter();
            var projectOperations = materialization.ProjectOperations.ToList();
            var fileCount = 0;
            foreach (var loaded in projects)
            {
                fileCount += await projectConverter.ConvertAsync(loaded, options, layout, materialization,
                    fixes, reviews, projectOperations);
            }
            var buildValidation = await new GeneratedBuildValidator().ValidateAsync(
                materialization.BuildTarget, options.SkipBuild, options.DryRun);
            if (buildValidation.Log is not null) projectOperations.Add(buildValidation.Log);
            if (buildValidation.Review is not null) reviews.Add(buildValidation.Review);
            await ConversionLogger.WriteAsync(options.Output, fixes, reviews, loader.Diagnostics,
                materialization.FileOperations, projectOperations,
                fileCount, options.DryRun);
            return new(fileCount, fixes.Count, reviews.Count);
        }
    }
}

internal sealed record ConversionRunSummary(int FileCount, int FixCount, int ReviewCount)
{
    // レビューが残る場合の終了コード2は、従来のCLI契約として維持する。
    internal int ExitCode => ReviewCount == 0 ? 0 : 2;
}
