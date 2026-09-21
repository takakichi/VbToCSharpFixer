using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

internal sealed partial class ConversionSession
{
    /// <summary>ファイルとプロジェクト共通のImportsを、ファイル側の別名を優先して列挙します。</summary>
    /// <returns>優先順位を反映したImports句の列挙。</returns>
    private IEnumerable<ImportsClauseSyntax> ImportClauses()
    {
        var file = ((CompilationUnitSyntax)_tree.GetRoot()).Imports.SelectMany(x => x.ImportsClauses).ToArray();
        // 同じ別名が両方にある場合、VBと同様にファイル側を残してプロジェクト側を除外する。
        var aliases = file.OfType<SimpleImportsClauseSyntax>().Where(x => x.Alias is not null)
            .Select(x => x.Alias!.Identifier.ValueText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return file.Concat(((_model.Compilation.Options as VisualBasicCompilationOptions)?.GlobalImports ?? [])
            .Select(x => x.Clause).Where(x => x is not SimpleImportsClauseSyntax { Alias: { } alias } ||
                !aliases.Contains(alias.Identifier.ValueText)));
    }

    /// <summary>VBのImports句をC#のusingディレクティブ本文へ変換します。</summary>
    /// <param name="clause">変換対象のImports句。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string ImportText(ImportsClauseSyntax clause)
    {
        if (clause is SimpleImportsClauseSyntax simple)
        {
            var symbol = ResolveImport(simple);
            var target = symbol switch
            {
                INamedTypeSymbol type => CSharpTypeNames.CSharpTypeName(type),
                INamespaceSymbol ns => ns.ToDisplayString(),
                _ => null
            };
            if (target is not null)
            {
                if (simple.Alias is not null)
                    return CSharpTypeNames.EscapeIdentifier(simple.Alias.Identifier.ValueText) + " = " + target;
                // 型そのもののImportsはC#のusing staticへ変換する。
                if (symbol is INamedTypeSymbol)
                    return "static " + (target.StartsWith("global::", StringComparison.Ordinal) ? target : "global::" + target);
                return simple.Name.ToString().StartsWith("Global.", StringComparison.OrdinalIgnoreCase) ? "global::" + target : target;
            }
        }
        return clause.ToString().Replace("Global.", "global::", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Imports句が参照する名前空間または型を解決します。</summary>
    /// <param name="clause">解決対象のImports句。</param>
    /// <returns>解決した名前空間または型。解決できない場合はnull。</returns>
    private INamespaceOrTypeSymbol? ResolveImport(SimpleImportsClauseSyntax clause)
    {
        if (clause.SyntaxTree == _tree && _model.GetSymbolInfo(clause.Name).Symbol is INamespaceOrTypeSymbol symbol)
            return symbol;
        var name = clause.Name.ToString();
        if (name.StartsWith("Global.", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        INamespaceOrTypeSymbol current = _model.Compilation.GlobalNamespace;
        foreach (var part in name.Split('.'))
        {
            var next = current.GetMembers().OfType<INamespaceOrTypeSymbol>()
                .FirstOrDefault(x => x.Name.Equals(part.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    /// <summary>Imports追加後の名前衝突を避けるため、型の完全修飾が必要か判定します。</summary>
    /// <param name="type">VBで解決された型。</param>
    /// <param name="position">名前解決を行うソース位置。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private bool NeedsQualifiedType(INamedTypeSymbol type, int position)
    {
        if (type.TypeKind == TypeKind.Error) return false;
        // using追加で別の同名型が候補になる場合でも、元のSemanticModelが選んだ型は変えない。
        var candidates = ImportClauses().OfType<SimpleImportsClauseSyntax>()
            .Where(x => x.Alias is null).Select(ResolveImport).OfType<INamespaceOrTypeSymbol>()
            .SelectMany(x => x.GetTypeMembers(type.Name, type.Arity))
            .Concat(_model.LookupNamespacesAndTypes(position, name: type.Name).OfType<INamedTypeSymbol>());
        return candidates.Any(other => !SymbolEqualityComparer.Default.Equals(other.OriginalDefinition, type.OriginalDefinition));
    }
}
