using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

public enum ExpressionMeaning { Method, Property, Array, Indexer, Value, Unresolved, Ambiguous }

public sealed record SymbolClassification(ExpressionMeaning Meaning, ISymbol? Symbol, ITypeSymbol? Type, string Reason);

public sealed class SymbolClassifier
{
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

        // VB may bind an omitted default property through the invocation operation rather
        // than directly exposing it from GetSymbolInfo on malformed/migrated source.
        var type = model.GetTypeInfo(node).Type;
        var memberInfo = model.GetSymbolInfo(node.Expression);
        if (memberInfo.Symbol is IPropertySymbol memberProperty)
            return ClassifyProperty(memberProperty);
        return new(ExpressionMeaning.Unresolved, null, type, "SemanticModel could not resolve invocation");
    }

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

    private static SymbolClassification ClassifyProperty(IPropertySymbol property)
    {
        var isIndexer = property.IsIndexer || property.Parameters.Length > 0;
        return new(isIndexer ? ExpressionMeaning.Indexer : ExpressionMeaning.Property, property, property.Type,
            isIndexer ? "Resolved as parameterized/default IPropertySymbol" : "Resolved as IPropertySymbol");
    }
}
