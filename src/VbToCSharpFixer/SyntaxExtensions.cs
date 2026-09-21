using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;


internal static class SyntaxExtensions
{
    /// <summary>As句またはAs New句から宣言型のSyntaxを取得します。</summary>
    /// <param name="clause">処理対象のImports句またはCase句。</param>
    /// <returns>宣言から取得した型構文。存在しない場合はnull。</returns>
    public static TypeSyntax? Type(this AsClauseSyntax? clause) => clause switch
    {
        SimpleAsClauseSyntax simple => simple.Type,
        AsNewClauseSyntax created when created.NewExpression is ObjectCreationExpressionSyntax creation => creation.Type,
        _ => null
    };
}
