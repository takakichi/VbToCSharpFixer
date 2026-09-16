using NUnit.Framework;

namespace VbToCSharpFixer.Tests;

[TestFixture]
public sealed class CliConversionTests
{
    [Test]
    public async Task Failure_before_conversion_writes_exception_log_and_preserves_previous_errors()
    {
        var root = Path.Combine(Path.GetTempPath(), "VbToCSharpCliTests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(Path.Combine(output, "logs"));
        var errorLog = Path.Combine(output, "logs", "error.log");
        await File.WriteAllTextAsync(errorLog, "Previous failure\n");
        try
        {
            var input = Path.Combine(root, "missing.vb");
            var exitCode = await Program.Main(["--file", input, "--output", output, "--skip-build"]);
            var log = await File.ReadAllTextAsync(errorLog);
            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(log, Does.StartWith("Previous failure\n"));
                Assert.That(log, Does.Contain("System.IO.FileNotFoundException"));
                Assert.That(log, Does.Contain(input));
                Assert.That(log, Does.Contain("WorkspaceLoader.LoadAsync"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task File_conversion_preserves_exit_code_logs_and_dry_run_contract(bool dryRun, bool needsReview)
    {
        var root = Path.Combine(Path.GetTempPath(), "VbToCSharpCliTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "Input.vb");
            var output = Path.Combine(root, "out");
            var source = needsReview
                ? "Public Class Example\nPublic Event Changed()\nEnd Class"
                : "Public Class Example\nPublic Function Value() As Integer\nReturn 42\nEnd Function\nEnd Class";
            await File.WriteAllTextAsync(input, source);
            var args = new List<string> { "--file", input, "--output", output, "--skip-build" };
            if (dryRun) args.Add("--dry-run");

            var exitCode = await Program.Main(args.ToArray());

            // dry-runも変換・診断・ログ出力を行う。ソース成果物だけを作成しない。
            Assert.That(exitCode, Is.EqualTo(needsReview ? 2 : 0));
            var summary = await File.ReadAllTextAsync(Path.Combine(output, "summary.txt"));
            Assert.That(summary, Does.Contain("Files: 1\n"));
            Assert.That(summary, Does.Contain($"Dry run: {dryRun}\n"));
            foreach (var log in new[] { "conversion.log", "file-copy.log", "project-conversion.log", "manual-review.csv" })
                Assert.That(File.Exists(Path.Combine(output, "logs", log)), Is.True, log);
            Assert.That(await File.ReadAllTextAsync(input), Is.EqualTo(source));
            if (dryRun)
                Assert.That(Directory.Exists(Path.Combine(output, "converted")), Is.False);
            else
            {
                var generated = Directory.GetFiles(Path.Combine(output, "converted"), "*.cs", SearchOption.AllDirectories);
                Assert.That(generated, Has.Length.EqualTo(1));
                Assert.That(await File.ReadAllTextAsync(generated[0]), Does.Contain(needsReview ? "ManualReviewRequired" : "return 42;"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
