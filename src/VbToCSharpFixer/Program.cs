using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public static class Program
{
    /// <summary>入力の解析、変換、構成出力、検証およびログ生成を統括します。</summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var summary = await new ConversionRunner().RunAsync(options);
            Console.WriteLine($"Processed {summary.FileCount} file(s), {summary.FixCount} semantic fix(es), {summary.ReviewCount} manual review item(s).");
            Console.WriteLine($"Output: {options.Output}");
            return summary.ExitCode;
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

}
