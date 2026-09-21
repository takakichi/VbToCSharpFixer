using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

using static VbToCSharpFixer.CSharpTypeNames;

internal sealed partial class ConversionSession
{
    /// <summary>メソッドがMicrosoft.VisualBasicランタイム由来かをAssemblyとNamespaceから判定します。</summary>
    /// <param name="method">処理対象のメソッド。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private static bool IsVisualBasicRuntimeMethod(IMethodSymbol method) =>
        IsVisualBasicRuntimeSymbol(method);

    /// <summary>静的なMicrosoft.VisualBasicフィールドまたはプロパティか判定します。</summary>
    /// <param name="symbol">処理対象のシンボル。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private static bool IsVisualBasicRuntimeValueMember(ISymbol symbol) =>
        symbol.IsStatic && (symbol is IFieldSymbol || symbol is IPropertySymbol) && IsVisualBasicRuntimeSymbol(symbol);

    /// <summary>シンボルがMicrosoft.VisualBasicアセンブリと名前空間に属するか判定します。</summary>
    /// <param name="symbol">処理対象のシンボル。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private static bool IsVisualBasicRuntimeSymbol(ISymbol symbol) =>
        symbol.ContainingAssembly?.Identity.Name is { } assemblyName &&
        (assemblyName.Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase) ||
         assemblyName.Equals("Microsoft.VisualBasic.Core", StringComparison.OrdinalIgnoreCase)) &&
        (symbol.ContainingNamespace?.ToDisplayString().Equals("Microsoft.VisualBasic", StringComparison.Ordinal) == true ||
         symbol.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.VisualBasic.", StringComparison.Ordinal) == true);

    /// <summary>VBランタイム型について通常名または衝突回避aliasによるC#アクセス表現を返します。</summary>
    /// <param name="method">処理対象のメソッド。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string VisualBasicRuntimeTypeAccess(IMethodSymbol method) => VisualBasicRuntimeTypeAccess(method.ContainingType);

    /// <summary>VBランタイム型について通常名または衝突回避aliasによるC#アクセス表現を返します。</summary>
    /// <param name="containingType">アクセス表現を生成するVBランタイム型。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string VisualBasicRuntimeTypeAccess(INamedTypeSymbol containingType)
    {
        // ソース全体の識別子を事前収集しているため、参照より後で宣言される同名型も検出できる。
        // aliasの採番順は生成コードの一部なので、探索順と大文字小文字を無視した比較を維持する。
        var fullType = containingType.ToDisplayString();
        _visualBasicRuntimeTypes.Add(fullType);
        var namespaceName = containingType.ContainingNamespace.ToDisplayString();
        var directType = namespaceName.Equals("Microsoft.VisualBasic", StringComparison.Ordinal);
        if (directType && !_sourceIdentifiers.Contains(containingType.Name))
        {
            _needsVisualBasicUsing = true;
            return containingType.Name;
        }

        if (_runtimeAliases.TryGetValue(fullType, out var existing)) return existing;
        var aliasBase = "VB" + containingType.Name;
        var alias = aliasBase;
        for (var suffix = 2; _sourceIdentifiers.Contains(alias) || _runtimeAliases.Values.Contains(alias, StringComparer.OrdinalIgnoreCase); suffix++)
            alias = aliasBase + suffix;
        _runtimeAliases.Add(fullType, alias);
        return alias;
    }
}
