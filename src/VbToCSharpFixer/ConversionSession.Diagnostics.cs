using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace VbToCSharpFixer;

using static VbToCSharpFixer.CSharpTypeNames;

internal sealed partial class ConversionSession
{
    /// <summary>未対応式をManualReviewRequiredとして記録し、安全なプレースホルダーを返します。</summary>
    private string UnsupportedExpression(ExpressionSyntax node)
    {
        Review(node, ReasonCode.UnsupportedSyntax, $"Unsupported VB expression: {node.Kind()}");
        return $"/* ManualReviewRequired: {OneLine(node.ToString())} */ default";
    }

    /// <summary>変換位置、前後コード、シンボル根拠を変換ログへ記録します。</summary>
    private void Record(SyntaxNode node, FixType type, string after, SymbolClassification classification)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        var symbol = classification.Symbol;
        _fixes.Add(new(_project, _file, position.Line + 1, position.Character + 1, type,
            node.ToString(), after, classification.Reason, symbol?.Kind.ToString(),
            symbol?.ContainingType?.ToDisplayString(), symbol?.ContainingAssembly?.Identity.Name));
    }

    /// <summary>自動変換できない構文をManualReviewRequiredへ追加します。</summary>
    private void Review(SyntaxNode node, ReasonCode code, string details)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        _reviews.Add(new(_project, _file, position.Line + 1, position.Character + 1,
            OneLine(node.ToString()), code, details));
    }

    /// <summary>VBの先行コメントをC#行コメントとして出力します。</summary>
    private void WriteLeadingComments(SyntaxNode node, StringBuilder output)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(VBSyntaxKind.CommentTrivia))
            {
                Line(output, CommentText(trivia));
                continue;
            }
            if (!trivia.IsKind(VBSyntaxKind.DocumentationCommentTrivia)) continue;
            foreach (var line in trivia.ToFullString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var text = line.TrimStart();
                if (text.StartsWith("'''", StringComparison.Ordinal))
                    Line(output, "///" + text[3..]);
            }
        }
    }

    /// <summary>VB通常コメントを内容を維持したC#行コメントへ変換します。</summary>
    private static string CommentText(SyntaxTrivia trivia)
    {
        var text = trivia.ToString().TrimStart();
        if (text.StartsWith("'", StringComparison.Ordinal)) return "//" + text[1..];
        if (text.StartsWith("REM", StringComparison.OrdinalIgnoreCase)) return "//" + text[3..];
        return "// " + text;
    }

    /// <summary>宣言行またはブロック終了行のVB末尾コメントをC#末尾コメントとして返します。</summary>
    private static string TrailingComment(SyntaxNode node)
    {
        var comment = node.GetTrailingTrivia().FirstOrDefault(x => x.IsKind(VBSyntaxKind.CommentTrivia));
        return comment.RawKind == 0 ? "" : " " + CommentText(comment);
    }

    /// <summary>複数行文字列をログ向けの単一行へ整形します。</summary>
    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
}
