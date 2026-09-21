using System.Text;

namespace VbToCSharpFixer;

/// <summary>変換単位のインデントと改行を管理します。構文の意味判定は行いません。</summary>
internal sealed class CSharpCodeWriter
{
    internal int Indent { get; set; }

    /// <summary>インデントを管理しながらC#の波括弧ブロックを出力します。</summary>
    /// <param name="output">生成したC#コードの出力先。</param>
    /// <param name="body">出力する本体処理。</param>
    /// <param name="closingSuffix">閉じ波括弧の後へ付加する文字列。</param>
    internal void Block(StringBuilder output, Action body, string closingSuffix = "")
    {
        Line(output, "{");
        Indent++;
        body();
        Indent--;
        Line(output, "}" + closingSuffix);
    }

    /// <summary>現在のインデントを付けて1行出力します。</summary>
    /// <param name="output">生成したC#コードの出力先。</param>
    /// <param name="value">処理対象の値。</param>
    internal void Line(StringBuilder output, string value) => output.Append(' ', Indent * 4).AppendLine(value);
}
