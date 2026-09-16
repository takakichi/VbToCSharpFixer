namespace VbToCSharpFixer;

public static class Program
{
    /// <summary>入力の解析、変換、構成出力、検証およびログ生成を統括します。</summary>
    public static async Task<int> Main(string[] args)
    {
        Options? options = null;
        try
        {
            options = Options.Parse(args);
            var summary = await new ConversionRunner().RunAsync(options);
            Console.WriteLine($"Processed {summary.FileCount} file(s), {summary.FixCount} semantic fix(es), {summary.ReviewCount} manual review item(s).");
            Console.WriteLine($"Output: {options.Output}");
            return summary.ExitCode;
        }
        catch (ArgumentException e) when (options is null)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            // 実行中のArgumentExceptionもスタックを残す。引数解析エラーとは区別する。
            Console.Error.WriteLine(e);
            if (options is not null)
                await ConversionLogger.WriteFailureAsync(options, e);
            return 1;
        }
    }

}
