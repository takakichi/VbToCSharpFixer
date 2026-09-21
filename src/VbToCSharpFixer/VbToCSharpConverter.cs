using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

/// <summary>意味解析に基づく保守的なVB→C#変換の公開窓口です。</summary>
public sealed class VbToCSharpConverter
{
    // セッションをまたぐのは参照照合キャッシュだけ。同じインスタンスの呼び出しは逐次実行する。
    private readonly ReferenceKindResolver _referenceKinds = new();

    /// <summary>ファイル専用の状態で変換し、生成コードと判断根拠を返します。</summary>
    /// <param name="tree">変換対象のVB構文木。</param>
    /// <param name="model">構文木に対応する意味モデル。</param>
    /// <param name="projectName">ログに記録するプロジェクト名。</param>
    /// <param name="rootNamespace">生成コードに適用するルート名前空間。</param>
    /// <returns>生成コード、変換記録および手動確認項目を含む変換結果。</returns>
    public ConversionResult Convert(SyntaxTree tree, SemanticModel model, string projectName, string? rootNamespace = null) =>
        new ConversionSession(tree, model, projectName, _referenceKinds).Convert(rootNamespace);

    /// <summary>テストや部分変換向けに単一のVB式を変換します。</summary>
    /// <param name="expression">変換または判定の対象となるVB式。</param>
    /// <param name="model">構文木に対応する意味モデル。</param>
    /// <param name="projectName">ログに記録するプロジェクト名。</param>
    /// <returns>生成または変換した文字列。</returns>
    public string ConvertExpression(ExpressionSyntax expression, SemanticModel model, string projectName = "Test") =>
        new ConversionSession(expression.SyntaxTree, model, projectName, _referenceKinds).ConvertExpression(expression);
}
