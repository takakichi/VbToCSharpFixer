using Microsoft.CodeAnalysis;
using CSharpSyntaxFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;

namespace VbToCSharpFixer;

/// <summary>用途別の型名表記と識別子のエスケープを提供します。</summary>
internal static class CSharpTypeNames
{
    // 完全修飾名と最小修飾名は呼び出し側の用途が異なるため、表記を一律に置き換えない。
    /// <summary>Forで安全に扱う組み込み数値型をC#キーワードへ対応付けます。</summary>
    /// <param name="type">処理対象の型または型構文。</param>
    /// <returns>対応するC#数値型キーワード。対象外の型の場合はnull。</returns>
    internal static string? CSharpNumericType(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_SByte => "sbyte",
        SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        SpecialType.System_Decimal => "decimal",
        _ => null
    };

    /// <summary>Roslyn型シンボルをGlobal Importsに依存しないC#型名へ変換します。</summary>
    /// <param name="type">処理対象の型または型構文。</param>
    /// <returns>完全修飾されたC#型名。安全に表現できない場合はnull。</returns>
    internal static string? CSharpTypeName(ITypeSymbol type)
    {
        var keyword = type.SpecialType switch
        {
            SpecialType.System_Object => "object",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Char => "char",
            SpecialType.System_String => "string",
            _ => CSharpNumericType(type)
        };
        if (keyword is not null) return keyword;
        if (type is IArrayTypeSymbol array)
        {
            var element = CSharpTypeName(array.ElementType);
            return element is null ? null : element + "[" + new string(',', array.Rank - 1) + "]";
        }
        if (type is ITypeParameterSymbol parameter) return parameter.Name;
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error || named.IsAnonymousType) return null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
            return CSharpTypeName(named.TypeArguments[0]) is { } nullableElement ? nullableElement + "?" : null;

        string prefix;
        if (named.ContainingType is not null)
        {
            var containing = CSharpTypeName(named.ContainingType);
            if (containing is null) return null;
            prefix = containing + ".";
        }
        else
        {
            var namespaceName = named.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";
            prefix = string.IsNullOrEmpty(namespaceName) ? "" : "global::" + namespaceName + ".";
        }
        if (named.Arity == 0) return prefix + named.Name;
        var arguments = named.TypeArguments.Skip(named.TypeArguments.Length - named.Arity)
            .Select(CSharpTypeName).ToArray();
        return arguments.Any(x => x is null)
            ? null
            : prefix + named.Name + "<" + string.Join(", ", arguments!) + ">";
    }

    /// <summary>C#コード中で使用するEnum型名を正式な大文字・小文字で返します。</summary>
    /// <param name="type">処理対象の型または型構文。</param>
    /// <returns>完全修飾されたC#列挙型名。</returns>
    internal static string EnumTypeName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var arguments = current.TypeArguments.Length == 0
                ? "" : "<" + string.Join(", ", current.TypeArguments.Select(TypeName)) + ">";
            names.Push(EscapeIdentifier(current.Name) + arguments);
        }
        return string.Join(".", names);
    }

    /// <summary>型シンボルをC#の組み込み型名または最小修飾型名へ変換します。</summary>
    /// <param name="type">処理対象の型または型構文。</param>
    /// <returns>C#の組み込み型名または最小修飾型名。</returns>
    internal static string TypeName(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_SByte => "sbyte", SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 => "short", SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int", SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long", SpecialType.System_UInt64 => "ulong",
        _ => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
    };

    /// <summary>C#予約語と一致する識別子を@付き識別子へ変換します。</summary>
    /// <param name="name">処理対象の名前。</param>
    /// <returns>必要に応じて@を付加したC#識別子。</returns>
    internal static string EscapeIdentifier(string name) =>
        CSharpSyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ||
        CSharpSyntaxFacts.GetContextualKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name : name;
}
