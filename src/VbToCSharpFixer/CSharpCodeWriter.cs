using System.Text;

namespace VbToCSharpFixer;

/// <summary>変換単位のインデントと改行を管理します。構文の意味判定は行いません。</summary>
internal sealed class CSharpCodeWriter
{
    internal int Indent { get; set; }

    /// <summary>インデントを管理しながらC#の波括弧ブロックを出力します。</summary>
    internal void Block(StringBuilder output, Action body, string closingSuffix = "")
    {
        Line(output, "{");
        Indent++;
        body();
        Indent--;
        Line(output, "}" + closingSuffix);
    }

    /// <summary>現在のインデントを付けて1行出力します。</summary>
    internal void Line(StringBuilder output, string value) => output.Append(' ', Indent * 4).AppendLine(value);
}
