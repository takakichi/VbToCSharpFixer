using System.Xml.Linq;

namespace VbToCSharpFixer;

/// <summary>変換済みXMLとファイルコピーを、計画した順序で出力します。</summary>
internal sealed class ProjectMaterializationPlan(XDocument document, string destination)
{
    private readonly List<PlanStep> _steps = [];
    internal Action ValidateResources { get; set; } = () => { };
    internal Action RecordCompletion { get; set; } = () => { };

    internal void AddCopy(string source, string target, Action recordCompletion) =>
        _steps.Add(new(source, target, recordCompletion));

    // 欠落ファイルや未対応項目のログも同じ列に置き、成功したコピーとの記録順を維持する。
    internal void Record(Action record) => _steps.Add(new(null, null, record));

    internal async Task ExecuteAsync(bool dryRun, CancellationToken ct)
    {
        // dry-runでも同じ計画をたどる。書き込みだけを省き、ログと資源の検証は実行する。
        foreach (var step in _steps)
        {
            if (!dryRun && step.Source is not null && step.Destination is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(step.Destination)!);
                await using var input = File.OpenRead(step.Source);
                await using var output = File.Create(step.Destination);
                await input.CopyToAsync(output, ct);
            }
            step.RecordCompletion();
        }
        ValidateResources();
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var stream = File.Create(destination);
            await document.SaveAsync(stream, SaveOptions.DisableFormatting, ct);
        }
        RecordCompletion();
    }

    private sealed record PlanStep(string? Source, string? Destination, Action RecordCompletion);
}
