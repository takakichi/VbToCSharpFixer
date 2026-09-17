using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbToCSharpFixer;

using static CSharpTypeNames;

internal sealed partial class ConversionSession
{
    // 引数付きでもDefaultでなければC#のindexerではない。宣言と参照に同じ規則を使う。
    private static bool UsesPropertyMethods(IPropertySymbol property) =>
        !property.IsIndexer && property.Parameters.Length > 0;

    private static string PropertyMethodName(IPropertySymbol property, bool getter)
    {
        var stem = (getter ? "Get_" : "Set_") + property.Name;
        var name = stem;
        // 別ファイルからの参照でも同じ名前になるよう、変換セッションの一時名に依存させない。
        for (var suffix = 2; property.ContainingType.GetMembers(name).Any(m => m is not IMethodSymbol { AssociatedSymbol: IPropertySymbol }); suffix++) name = stem + suffix;
        return EscapeIdentifier(name);
    }

    private static string PropertyValueName(IPropertySymbol property) =>
        EscapeIdentifier(property.SetMethod!.Parameters.Last().Name);

    private void WritePropertyMethods(PropertyStatementSyntax declaration, SyntaxList<AccessorBlockSyntax> accessors,
        IPropertySymbol property, StringBuilder output)
    {
        foreach (var getter in new[] { true, false })
        {
            var method = getter ? property.GetMethod : property.SetMethod;
            if (method is null) continue;
            var parameters = declaration.ParameterList!.Parameters.Select(p => Parameter(p) +
                (p.Default is null ? "" : " = " + ExprForTarget(p.Default.Value, (_model.GetDeclaredSymbol(p) as IParameterSymbol)?.Type))).ToList();
            // 必須の設定値を先頭に置き、省略可能なプロパティ引数を合法なC#宣言に保つ。
            if (!getter) parameters.Insert(0, CSharpTypeName(property.Type) + " " + PropertyValueName(property));
            var modifiers = property.IsStatic ? "static " : property.IsAbstract ? "abstract " :
                property.IsOverride ? "override " : property.IsVirtual ? "virtual " : "";
            var signature = AccessibilityText(method.DeclaredAccessibility) + modifiers +
                (getter ? CSharpTypeName(property.Type) : "void") + " " + PropertyMethodName(property, getter) +
                "(" + string.Join(", ", parameters) + ")";
            var accessor = accessors.FirstOrDefault(a => a.IsKind(getter ? SyntaxKind.GetAccessorBlock : SyntaxKind.SetAccessorBlock));
            if (accessor is null) { Line(output, signature + ";"); continue; }
            Line(output, signature);
            // メソッド化したSetにはVBの実引数名を残す。通常のC# setのvalue置換は適用しない。
            var previous = _setterValue;
            _setterValue = null;
            try
            {
                WithLabelScope(accessor.Statements, () => Block(output, () =>
                {
                    foreach (var statement in accessor.Statements) WriteStatement(statement, output);
                }));
            }
            finally { _setterValue = previous; }
        }
    }

    private string PropertyReceiver(IPropertyReferenceOperation reference)
    {
        if (reference.Property.IsStatic) return CSharpTypeName(reference.Property.ContainingType) + ".";
        if (reference.Instance is null || reference.Instance.IsImplicit) return "";
        if (reference.Instance.Syntax is ExpressionSyntax expression) return Expr(expression) + ".";
        return "";
    }

    private string PropertyArgument(IArgumentOperation argument)
    {
        if (argument.IsImplicit && argument.Value.ConstantValue.HasValue)
        {
            var value = argument.Value.ConstantValue.Value;
            if (value is null) return "null";
            var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatPrimitive(value, true, false);
            return argument.Parameter?.Type.TypeKind == TypeKind.Enum ? "(" + CSharpTypeName(argument.Parameter.Type) + ")" + literal : literal;
        }
        return argument.Value.Syntax is ExpressionSyntax expression
            ? ExprForTarget(expression, argument.Parameter?.Type) : "/* ManualReviewRequired: property argument */ default";
    }

    private string PropertyRead(IPropertyReferenceOperation reference) =>
        PropertyReceiver(reference) + PropertyMethodName(reference.Property, true) + "(" +
        string.Join(", ", reference.Arguments.Select(a => EscapeIdentifier(a.Parameter!.Name) + ": " + PropertyArgument(a))) + ")";

    private bool TryWritePropertyAssignment(AssignmentStatementSyntax assignment, StringBuilder output)
    {
        if (_model.GetOperation(assignment.Left) is not IPropertyReferenceOperation reference || !UsesPropertyMethods(reference.Property)) return false;
        var property = reference.Property;
        var receiver = PropertyReceiver(reference);
        var arguments = new List<string>();
        // 複合代入では受信側・添字・getter・右辺・setterの順を保ち、式を二度評価しない。
        Block(output, () =>
        {
            if (!property.IsStatic && reference.Instance is { IsImplicit: false } instance &&
                instance.Syntax is ExpressionSyntax expression && expression is not MeExpressionSyntax and not MyBaseExpressionSyntax)
            {
                var temporary = CreateUniqueTemporaryName("__propertyTarget");
                var byRef = instance.Type?.IsValueType == true ? "ref " : "";
                Line(output, $"{byRef}var {temporary} = {byRef}{Expr(expression)};");
                receiver = temporary + ".";
            }
            foreach (var argument in reference.Arguments)
            {
                var temporary = CreateUniqueTemporaryName("__propertyArgument");
                Line(output, $"{CSharpTypeName(argument.Parameter!.Type)} {temporary} = {PropertyArgument(argument)};");
                arguments.Add(EscapeIdentifier(argument.Parameter.Name) + ": " + temporary);
            }
            var value = ExprForTarget(assignment.Right, property.Type);
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentStatement))
            {
                var oldValue = CreateUniqueTemporaryName("__propertyOldValue");
                Line(output, $"var {oldValue} = {receiver}{PropertyMethodName(property, true)}({string.Join(", ", arguments)});");
                value = oldValue + " " + AssignmentOperator(assignment.Kind()).TrimEnd('=') + " (" + value + ")";
            }
            arguments.Add(PropertyValueName(property) + ": " + value);
            Line(output, receiver + PropertyMethodName(property, false) + "(" + string.Join(", ", arguments) + ");");
        });
        return true;
    }
}
