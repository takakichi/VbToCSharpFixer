using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

/// <summary>意味解析に基づく保守的なVB→C#変換の公開窓口です。</summary>
public sealed class VbToCSharpConverter
{
    // セッションをまたぐのは参照照合キャッシュだけ。同じインスタンスの呼び出しは逐次実行する。
    private readonly ReferenceKindResolver _referenceKinds = new();

    /// <summary>ファイル専用の状態で変換し、生成コードと判断根拠を返します。</summary>
    public ConversionResult Convert(SyntaxTree tree, SemanticModel model, string projectName, string? rootNamespace = null) =>
        new ConversionSession(tree, model, projectName, _referenceKinds).Convert(rootNamespace);

    /// <summary>テストや部分変換向けに単一のVB式を変換します。</summary>
    public string ConvertExpression(ExpressionSyntax expression, SemanticModel model, string projectName = "Test") =>
        new ConversionSession(expression.SyntaxTree, model, projectName, _referenceKinds).ConvertExpression(expression);
}
