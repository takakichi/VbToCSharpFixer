using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;


internal static class SyntaxExtensions
{
    /// <summary>As句またはAs New句から宣言型のSyntaxを取得します。</summary>
    public static TypeSyntax? Type(this AsClauseSyntax? clause) => clause switch
    {
        SimpleAsClauseSyntax simple => simple.Type,
        AsNewClauseSyntax created when created.NewExpression is ObjectCreationExpressionSyntax creation => creation.Type,
        _ => null
    };
}
