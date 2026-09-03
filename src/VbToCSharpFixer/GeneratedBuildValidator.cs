using System.Diagnostics;

namespace VbToCSharpFixer;

public sealed class GeneratedBuildValidator
{
    public async Task<(ProjectConversionLogEntry? Log, ManualReviewItem? Review)> ValidateAsync(
        string? target, bool skipBuild, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (target is null || skipBuild || dryRun) return (null, null);
        if (!File.Exists(target))
        {
            var details = $"Generated build target does not exist: {target}";
            return (new("BuildValidation", target, target, "MSBuild", "Failed"),
                new("BuildValidation", target, 0, 0, "", ReasonCode.GeneratedProjectBuildFailure, details));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(target)!
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("/t:Build");
        startInfo.ArgumentList.Add("/p:Configuration=Debug");
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/v:minimal");
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await standardOutput + Environment.NewLine + await standardError).Trim();
            if (process.ExitCode == 0)
                return (new("BuildValidation", target, target, "MSBuild: " + OneLine(output), "Success"), null);
            return (new("BuildValidation", target, target, "MSBuild", $"Failed ({process.ExitCode})"),
                new("BuildValidation", target, 0, 0, "", ReasonCode.GeneratedProjectBuildFailure, output));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (new("BuildValidation", target, target, "MSBuild", "Failed"),
                new("BuildValidation", target, 0, 0, "", ReasonCode.GeneratedProjectBuildFailure, e.Message));
        }
    }

    private static string OneLine(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 1000 ? line : line[..1000] + "...";
    }
}
