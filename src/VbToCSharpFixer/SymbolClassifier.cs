using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

public enum ExpressionMeaning { Method, Property, Array, Indexer, Value, Unresolved, Ambiguous }

public sealed record SymbolClassification(ExpressionMeaning Meaning, ISymbol? Symbol, ITypeSymbol? Type, string Reason);

public sealed class SymbolClassifier
{
    /// <summary>VBの呼び出し式をメソッド、配列、Indexerまたは未解決として分類します。</summary>
    /// <param name="node">変換または記録の対象となる構文ノード。</param>
    /// <param name="model">構文木に対応する意味モデル。</param>
    /// <returns>呼び出し式の意味と解決済みシンボルを含む分類結果。</returns>
    public SymbolClassification ClassifyInvocation(InvocationExpressionSyntax node, SemanticModel model)
    {
        var info = model.GetSymbolInfo(node);
        if (info.Symbol is IMethodSymbol method)
            return new(ExpressionMeaning.Method, method, method.ReturnType, "Resolved as IMethodSymbol by SemanticModel");
        if (info.Symbol is IPropertySymbol property)
            return ClassifyProperty(property);
        if (info.Symbol is not null)
            return new(ExpressionMeaning.Value, info.Symbol, model.GetTypeInfo(node).Type, $"Resolved as {info.Symbol.Kind}");
        if (info.CandidateSymbols.Length > 1)
            return new(ExpressionMeaning.Ambiguous, null, model.GetTypeInfo(node).Type, $"{info.CandidateSymbols.Length} candidate symbols");

        var expressionType = model.GetTypeInfo(node.Expression).Type;
        if (expressionType is IArrayTypeSymbol array)
            return new(ExpressionMeaning.Array, null, array.ElementType, "Invocation target resolved as IArrayTypeSymbol");

        // 移行途中などの不完全なVBでは、省略された既定プロパティが呼び出し全体ではなく
        // 呼び出し対象側にだけ結び付く場合があるため、対象式のシンボルも確認する。
        var type = model.GetTypeInfo(node).Type;
        var memberInfo = model.GetSymbolInfo(node.Expression);
        if (memberInfo.Symbol is IPropertySymbol memberProperty)
            return ClassifyProperty(memberProperty);
        return new(ExpressionMeaning.Unresolved, null, type, "SemanticModel could not resolve invocation");
    }

    /// <summary>一般のVB式がメソッド、プロパティまたは値のどれに解決されるか分類します。</summary>
    /// <param name="node">変換または記録の対象となる構文ノード。</param>
    /// <param name="model">構文木に対応する意味モデル。</param>
    /// <returns>式の意味と解決済みシンボルを含む分類結果。</returns>
    public SymbolClassification ClassifyExpression(ExpressionSyntax node, SemanticModel model)
    {
        var info = model.GetSymbolInfo(node);
        if (info.Symbol is IMethodSymbol method)
            return new(ExpressionMeaning.Method, method, method.ReturnType, "Resolved as IMethodSymbol by SemanticModel");
        if (info.Symbol is IPropertySymbol property)
            return new(ExpressionMeaning.Property, property, property.Type, "Resolved as IPropertySymbol by SemanticModel");
        if (info.Symbol is not null)
            return new(ExpressionMeaning.Value, info.Symbol, model.GetTypeInfo(node).Type, $"Resolved as {info.Symbol.Kind}");
        return info.CandidateSymbols.Length > 1
            ? new(ExpressionMeaning.Ambiguous, null, model.GetTypeInfo(node).Type, $"{info.CandidateSymbols.Length} candidate symbols")
            : new(ExpressionMeaning.Unresolved, null, model.GetTypeInfo(node).Type, "SemanticModel could not resolve expression");
    }

    /// <summary>Defaultの意味情報からIndexerを判定します。引数付きの通常プロパティとは区別します。</summary>
    /// <param name="property">処理対象のプロパティ。</param>
    /// <returns>プロパティの種類を表す分類結果。</returns>
    private static SymbolClassification ClassifyProperty(IPropertySymbol property)
    {
        var isIndexer = property.IsIndexer;
        return new(isIndexer ? ExpressionMeaning.Indexer : ExpressionMeaning.Property, property, property.Type,
            isIndexer ? "Resolved as default IPropertySymbol" : "Resolved as IPropertySymbol");
    }
}
