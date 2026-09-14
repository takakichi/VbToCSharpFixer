using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using CSharpSyntaxFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;
using VBSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace VbToCSharpFixer;

using static VbToCSharpFixer.CSharpTypeNames;

internal sealed partial class ConversionSession
{
    /// <summary>Usingの宣言または式を従来形式のC# usingブロックへ変換します。</summary>
    private void WriteUsingBlock(UsingBlockSyntax block, StringBuilder output)
    {
        if (!TryUsingResources(block.UsingStatement, out var resources) || resources.Count == 0)
        {
            Review(block, ReasonCode.UnsupportedSyntax, "Using resources could not be converted safely");
            Line(output, $"// ManualReviewRequired: unsupported UsingBlock: {OneLine(block.ToString())}");
            return;
        }

        void WriteResource(int index)
        {
            var headerSuffix = index == 0 ? TrailingComment(block.UsingStatement) : "";
            var endSuffix = index == 0 ? TrailingComment(block.EndUsingStatement) : "";
            Line(output, $"using ({resources[index]}){headerSuffix}");
            Block(output, () =>
            {
                if (index + 1 < resources.Count)
                {
                    WriteResource(index + 1);
                    return;
                }
                foreach (var statement in block.Statements) WriteStatement(statement, output);
                WriteLeadingComments(block.EndUsingStatement, output);
            }, endSuffix);
        }

        WriteResource(0);
    }

    /// <summary>Using文の式または変数宣言を評価順序どおりのC#リソース指定へ変換します。</summary>
    private bool TryUsingResources(UsingStatementSyntax statement, out IReadOnlyList<string> resources)
    {
        var converted = new List<string>();
        if (statement.Expression is not null)
        {
            converted.Add(Expr(statement.Expression));
            resources = converted;
            return true;
        }

        foreach (var declarator in statement.Variables)
        {
            foreach (var name in declarator.Names)
            {
                if (_model.GetDeclaredSymbol(name) is not ILocalSymbol local ||
                    CSharpTypeName(local.Type) is not { } type)
                {
                    resources = [];
                    return false;
                }

                ExpressionSyntax? initializer = declarator.Initializer?.Value;
                if (initializer is null && declarator.AsClause is AsNewClauseSyntax asNew)
                    initializer = asNew.NewExpression;
                if (initializer is null)
                {
                    resources = [];
                    return false;
                }
                converted.Add($"{type} {EscapeIdentifier(name.Identifier.ValueText)} = {ExprForTarget(initializer, local.Type)}");
            }
        }
        resources = converted;
        return converted.Count > 0;
    }

    /// <summary>単行IfのThenおよびElseステートメントを通常のC#ブロックへ変換します。</summary>
    private void WriteSingleLineIf(SingleLineIfStatementSyntax statement, StringBuilder output)
    {
        Line(output, $"if ({Expr(statement.Condition)})");
        Block(output, () => { foreach (var child in statement.Statements) WriteStatement(child, output); });
        if (statement.ElseClause is null) return;
        Line(output, "else");
        Block(output, () => { foreach (var child in statement.ElseClause.Statements) WriteStatement(child, output); });
    }

    /// <summary>VBのSelect Caseを選択式の一度評価とif／else if連鎖へ変換します。</summary>
    private void WriteSelectBlock(SelectBlockSyntax block, StringBuilder output)
    {
        var selectType = _model.GetTypeInfo(block.SelectStatement.Expression).Type;
        var valueName = CreateUniqueTemporaryName("__selectValue");
        var cases = new List<(CaseBlockSyntax Block, string? Condition, bool IsElse)>();
        foreach (var caseBlock in block.CaseBlocks)
        {
            var isElse = caseBlock.CaseStatement.Cases.Any(x => x is ElseCaseClauseSyntax);
            if (isElse)
            {
                cases.Add((caseBlock, null, true));
                continue;
            }
            var conditions = caseBlock.CaseStatement.Cases
                .Select(clause => SelectCaseCondition(clause, valueName, selectType)).ToArray();
            if (conditions.Any(x => x is null))
            {
                Review(block, ReasonCode.UnsupportedSyntax, "Select Case contains a comparison that cannot be preserved safely");
                Line(output, $"// ManualReviewRequired: unsupported SelectBlock: {OneLine(block.ToString())}");
                return;
            }
            cases.Add((caseBlock, string.Join(" || ", conditions.Select(x => x!.Contains(" && ", StringComparison.Ordinal) ? $"({x})" : x)), false));
        }

        var endLabel = CreateUniqueTemporaryName("__selectEnd");
        Line(output, "{");
        _writer.Indent++;
        Line(output, $"var {valueName} = {Expr(block.SelectStatement.Expression)};");
        _selectEndLabels.Push(endLabel);
        try
        {
            var wroteCondition = false;
            foreach (var item in cases)
            {
                if (item.IsElse)
                    Line(output, wroteCondition ? "else" : "if (true)");
                else
                {
                    Line(output, wroteCondition ? $"else if ({item.Condition})" : $"if ({item.Condition})");
                    wroteCondition = true;
                }
                Block(output, () => { foreach (var child in item.Block.Statements) WriteStatement(child, output); });
            }
        }
        finally
        {
            _selectEndLabels.Pop();
        }
        Line(output, endLabel + ":;");
        _writer.Indent--;
        Line(output, "}");
    }

    /// <summary>Select Caseの単一値、範囲、比較句をC#条件式へ変換します。</summary>
    private string? SelectCaseCondition(CaseClauseSyntax clause, string valueName, ITypeSymbol? selectType)
    {
        return clause switch
        {
            SimpleCaseClauseSyntax simple => SelectComparison(valueName, simple.Value, "==", selectType),
            RangeCaseClauseSyntax range => CombineSelectRange(
                SelectComparison(valueName, range.LowerBound, ">=", selectType),
                SelectComparison(valueName, range.UpperBound, "<=", selectType)),
            RelationalCaseClauseSyntax relational => SelectComparison(valueName, relational.Value,
                RelationalOperator(relational.OperatorToken.ValueText), selectType),
            _ => null
        };
    }

    /// <summary>Select Caseの範囲比較を両端が安全な場合だけ結合します。</summary>
    private static string? CombineSelectRange(string? lower, string? upper) =>
        lower is null || upper is null ? null : $"{lower} && {upper}";

    /// <summary>Select Caseの比較を型に応じてC#演算子またはVB Operators呼び出しへ変換します。</summary>
    private string? SelectComparison(string left, ExpressionSyntax rightExpression, string? operation, ITypeSymbol? selectType)
    {
        if (operation is null || selectType is null) return null;
        var compareText = UsesTextComparison();
        if (selectType.SpecialType == SpecialType.System_String)
        {
            var compare = VisualBasicOperatorAccess("CompareString");
            return compare is null ? null : $"{compare}({left}, {ExprForTarget(rightExpression, selectType)}, {compareText.ToString().ToLowerInvariant()}) {operation} 0";
        }
        if (selectType.SpecialType == SpecialType.System_Object)
        {
            var method = operation switch
            {
                "==" => "ConditionalCompareObjectEqual", "!=" => "ConditionalCompareObjectNotEqual",
                "<" => "ConditionalCompareObjectLess", "<=" => "ConditionalCompareObjectLessEqual",
                ">" => "ConditionalCompareObjectGreater", ">=" => "ConditionalCompareObjectGreaterEqual",
                _ => null
            };
            var compare = method is null ? null : VisualBasicOperatorAccess(method);
            return compare is null ? null : $"{compare}({left}, {Expr(rightExpression)}, {compareText.ToString().ToLowerInvariant()})";
        }
        var conversion = _model.ClassifyConversion(rightExpression, selectType);
        if (!conversion.Exists || conversion.IsUserDefined || conversion.IsNarrowing &&
            !(selectType.TypeKind == TypeKind.Enum && _model.GetTypeInfo(rightExpression).Type is { } source && IsIntegral(source)))
            return null;
        return $"{left} {operation} {ExprForTarget(rightExpression, selectType)}";
    }

    /// <summary>VBのSelect Case比較演算子をC#演算子へ変換します。</summary>
    private static string? RelationalOperator(string operation) => operation switch
    {
        "=" => "==", "<>" => "!=", "<" => "<", "<=" => "<=", ">" => ">", ">=" => ">=", _ => null
    };

    /// <summary>Microsoft.VisualBasic.CompilerServices.Operatorsの比較メソッド参照を生成します。</summary>
    private string? VisualBasicOperatorAccess(string method)
    {
        var operators = _model.Compilation.GetTypeByMetadataName("Microsoft.VisualBasic.CompilerServices.Operators");
        if (operators is null) return null;
        return $"{VisualBasicRuntimeTypeAccess(operators)}.{method}";
    }

    /// <summary>メソッド内LabelをC#で有効かつ衝突しない名前へ対応付けて本体を変換します。</summary>
    private void WithLabelScope(IEnumerable<StatementSyntax> statements, Action body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in statements.SelectMany(x => x.DescendantNodesAndSelf()).OfType<LabelStatementSyntax>())
        {
            var source = label.LabelToken.ValueText;
            if (map.ContainsKey(source)) continue;
            var baseName = CSharpSyntaxFacts.IsValidIdentifier(source) ? EscapeIdentifier(source) : "__label_" + source;
            map[source] = CreateUniqueTemporaryName(baseName);
        }
        _labelMaps.Push(map);
        try { body(); }
        finally { _labelMaps.Pop(); }
    }

    /// <summary>現在のVBファイルまたはProject既定値がOption Compare Textか判定します。</summary>
    private bool UsesTextComparison()
    {
        var root = (CompilationUnitSyntax)_model.SyntaxTree.GetRoot();
        var option = root.Options.LastOrDefault(x => x.NameKeyword.IsKind(VBSyntaxKind.CompareKeyword));
        return option is null
            ? (_model.Compilation.Options as VisualBasicCompilationOptions)?.OptionCompareText == true
            : option.ValueKeyword.IsKind(VBSyntaxKind.TextKeyword);
    }

    /// <summary>VB Labelを現在のメソッド内対応表に基づくC# Labelとして出力します。</summary>
    private void WriteLabel(LabelStatementSyntax statement, StringBuilder output)
    {
        var source = statement.LabelToken.ValueText;
        if (_labelMaps.Count > 0 && _labelMaps.Peek().TryGetValue(source, out var label))
            Line(output, label + ":");
        else
        {
            Review(statement, ReasonCode.UnresolvedSymbol, $"Label could not be resolved: {source}");
            Line(output, $"// ManualReviewRequired: unresolved label {source}");
        }
    }

    /// <summary>通常のVB GoToを現在のメソッド内Labelへ変換します。</summary>
    private void WriteGoTo(GoToStatementSyntax statement, StringBuilder output)
    {
        var source = statement.Label.LabelToken.ValueText;
        if (_labelMaps.Count > 0 && _labelMaps.Peek().TryGetValue(source, out var label))
            Line(output, $"goto {label};");
        else
        {
            Review(statement, ReasonCode.UnresolvedSymbol, $"GoTo target could not be resolved: {source}");
            Line(output, $"// ManualReviewRequired: unresolved GoTo {source}");
        }
    }

    /// <summary>VBのTry、Catch、Finallyブロックを同じ順序のC#例外処理へ変換します。</summary>
    private void WriteTryBlock(TryBlockSyntax block, StringBuilder output)
    {
        Line(output, "try");
        Block(output, () => { foreach (var statement in block.Statements) WriteStatement(statement, output); });
        foreach (var catchBlock in block.CatchBlocks) WriteCatchBlock(catchBlock, output);
        if (block.FinallyBlock is not null)
        {
            WriteLeadingComments(block.FinallyBlock.FinallyStatement, output);
            Line(output, "finally");
            Block(output, () =>
            {
                foreach (var statement in block.FinallyBlock.Statements) WriteStatement(statement, output);
            });
        }
    }

    /// <summary>VBのCatch宣言、例外型、Whenフィルターおよび本体をC#へ変換します。</summary>
    private void WriteCatchBlock(CatchBlockSyntax block, StringBuilder output)
    {
        var statement = block.CatchStatement;
        WriteLeadingComments(statement, output);
        var identifier = statement.IdentifierName?.Identifier.ValueText;
        var declaration = identifier is null
            ? ""
            : $" ({(statement.AsClause is null ? "global::System.Exception" : CatchType(statement.AsClause))} {identifier})";
        var filter = statement.WhenClause is null ? "" : $" when ({Expr(statement.WhenClause.Filter)})";
        Line(output, "catch" + declaration + filter);
        Block(output, () => { foreach (var child in block.Statements) WriteStatement(child, output); });
    }

    /// <summary>Catch例外型をGlobal Importsに依存しないC#完全修飾名として返します。</summary>
    private string CatchType(SimpleAsClauseSyntax clause)
    {
        var type = _model.GetTypeInfo(clause.Type).Type;
        return type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("Global.", "global::", StringComparison.Ordinal) ?? Type(clause.Type);
    }

    /// <summary>安全に型付けされたVB数値Forを、上限とStepを一度だけ評価するC#ループへ変換します。</summary>
    private void WriteForBlock(ForBlockSyntax block, StringBuilder output)
    {
        var statement = block.ForStatement;
        if (!TryGetForControl(statement, out var control, out var declaration, out var controlType, out var typeName) ||
            block.NextStatement.ControlVariables.Count > 1 ||
            !CanAssignForValue(statement.FromValue, controlType) ||
            !CanAssignForValue(statement.ToValue, controlType) ||
            statement.StepClause is not null && !CanAssignForValue(statement.StepClause.StepValue, controlType))
        {
            Review(block, ReasonCode.UnsupportedSyntax,
                "For requires a simple numeric variable and non-narrowing start, limit and step values");
            Line(output, $"// ManualReviewRequired: unsupported ForBlock: {OneLine(block.ToString())}");
            return;
        }

        // 終了値とStepを条件式へ直接埋め込むと、副作用のある式を反復ごとに再評価してしまう。
        // 開始値 → 終了値 → Stepの評価順と、各式を一度だけ評価するVBの動作を維持する。
        var limit = CreateUniqueTemporaryName("__forLimit");
        var step = CreateUniqueTemporaryName("__forStep");
        Line(output, "{");
        _writer.Indent++;
        Line(output, declaration
            ? $"{typeName} {control} = {Expr(statement.FromValue)};"
            : $"{control} = {Expr(statement.FromValue)};");
        Line(output, $"{typeName} {limit} = {Expr(statement.ToValue)};");
        Line(output, $"{typeName} {step} = {(statement.StepClause is null ? "1" : Expr(statement.StepClause.StepValue))};");
        Line(output, $"for (; ({step} >= 0 ? {control} <= {limit} : {control} >= {limit}); {control} += {step})");
        Block(output, () => { foreach (var child in block.Statements) WriteStatement(child, output); });
        _writer.Indent--;
        Line(output, "}");
    }

    /// <summary>安全に解決できるVBのFor Eachを、制御変数の代入可能性を維持したC# foreachへ変換します。</summary>
    private void WriteForEachBlock(ForEachBlockSyntax block, StringBuilder output)
    {
        var statement = block.ForEachStatement;
        if (!TryGetForEachControl(statement, out var control, out var declaration, out var controlType) ||
            block.NextStatement.ControlVariables.Count > 1 ||
            !CanSafelyEnumerate(statement, controlType) ||
            CSharpTypeName(controlType) is not { } typeName)
        {
            Review(block, ReasonCode.UnsupportedSyntax,
                "For Each requires a statically enumerable expression and a safely convertible simple control variable");
            Line(output, $"// ManualReviewRequired: unsupported ForEachBlock: {OneLine(block.ToString())}");
            return;
        }

        // C#のforeach変数は再代入できないため、VBの制御変数とは別に用意する。
        // 既存変数への代入なら、ループを抜けた後も最後の要素がその変数に残る。
        var item = CreateUniqueTemporaryName("__forEachItem");
        Line(output, $"foreach ({typeName} {item} in {Expr(statement.Expression)})");
        Block(output, () =>
        {
            Line(output, declaration ? $"{typeName} {control} = {item};" : $"{control} = {item};");
            foreach (var child in block.Statements) WriteStatement(child, output);
        });
    }

    /// <summary>For Each制御変数を宣言または既存の単純変数として意味解析します。</summary>
    private bool TryGetForEachControl(ForEachStatementSyntax statement, out string control,
        out bool declaration, out ITypeSymbol controlType)
    {
        control = "";
        declaration = false;
        controlType = null!;
        switch (statement.ControlVariable)
        {
            case VariableDeclaratorSyntax variable when variable.Names.Count == 1:
                var name = variable.Names[0];
                if (_model.GetDeclaredSymbol(name) is not ILocalSymbol { IsConst: false } local) return false;
                control = name.Identifier.ValueText;
                declaration = true;
                controlType = local.Type;
                return !local.Type.IsAnonymousType;
            case IdentifierNameSyntax identifier:
                var symbol = _model.GetSymbolInfo(identifier).Symbol;
                if (symbol is ILocalSymbol { IsConst: false } existingLocal)
                    controlType = existingLocal.Type;
                else if (symbol is IParameterSymbol parameter)
                    controlType = parameter.Type;
                else if (symbol is IFieldSymbol { IsConst: false, IsReadOnly: false } field)
                    controlType = field.Type;
                else
                    return false;
                control = Expr(identifier, true);
                return !controlType.IsAnonymousType;
            default:
                return false;
        }
    }

    /// <summary>For Eachの列挙型と要素変換がC# foreachでも安全に表現できるか判定します。</summary>
    private bool CanSafelyEnumerate(ForEachStatementSyntax statement, ITypeSymbol controlType)
    {
        var collectionType = _model.GetTypeInfo(statement.Expression).Type;
        if (collectionType is null || collectionType.SpecialType == SpecialType.System_Object) return false;
        var info = _model.GetForEachStatementInfo(statement);
        if (info.ElementType is null || info.GetEnumeratorMethod?.ReducedFrom is not null) return false;
        var conversion = info.ElementConversion;
        return conversion.Exists && !conversion.IsUserDefined &&
               (!conversion.IsNarrowing || conversion.IsReference) &&
               SymbolEqualityComparer.Default.Equals(
                   _model.GetTypeInfo(statement.ControlVariable).Type ?? controlType, controlType);
    }

    /// <summary>参照型または読み取り専用の値型を対象とするVB WithブロックをC#へ変換します。</summary>
    private void WriteWithBlock(WithBlockSyntax block, StringBuilder output)
    {
        var targetExpression = block.WithStatement.Expression;
        var targetType = _model.GetTypeInfo(targetExpression).Type;
        // 値型を一時変数へ移すとコピーになる。元の格納場所への書き戻しを保証できないため、
        // 値型は読み取り専用と判定できる本体だけを許可し、Objectの遅延バインディングも推測しない。
        if (targetType is null || targetType.SpecialType == SpecialType.System_Object ||
            !targetType.IsReferenceType && (!targetType.IsValueType || !IsReadOnlyValueTypeWith(block)))
        {
            Review(block, ReasonCode.UnsupportedSyntax,
                "With requires a resolved reference type or a read-only value-type body");
            Line(output, $"// ManualReviewRequired: unsupported WithBlock: {OneLine(block.ToString())}");
            return;
        }

        var target = CreateUniqueTemporaryName("__withTarget");
        Line(output, "{");
        _writer.Indent++;
        Line(output, $"var {target} = {Expr(targetExpression)};");
        _withTargets.Push(target);
        try
        {
            foreach (var statement in block.Statements) WriteStatement(statement, output);
        }
        finally
        {
            _withTargets.Pop();
        }
        _writer.Indent--;
        Line(output, "}");
    }

    /// <summary>値型Withの本体が代入や呼び出しを含まない読み取り専用か保守的に判定します。</summary>
    private static bool IsReadOnlyValueTypeWith(WithBlockSyntax block)
    {
        var nodes = block.Statements.SelectMany(x => x.DescendantNodesAndSelf())
            .Where(x => !x.Ancestors().OfType<WithBlockSyntax>().Any(nested => nested != block));
        return !nodes.Any(node => node switch
        {
            AssignmentStatementSyntax assignment => IsWithBasedExpression(assignment.Left),
            InvocationExpressionSyntax invocation => invocation.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>().Any(HasOmittedWithReceiver),
            _ => false
        });
    }

    /// <summary>式が現在のWith対象を起点とする先頭ドットのメンバー参照か判定します。</summary>
    private static bool IsWithBasedExpression(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(HasOmittedWithReceiver);

    /// <summary>VB With内で左辺が省略されたメンバーアクセスか判定します。</summary>
    private static bool HasOmittedWithReceiver(MemberAccessExpressionSyntax member) =>
        member.Expression is null || member.Expression.IsMissing;

    /// <summary>For制御変数を宣言または既存の単純変数として解決し、C#数値型を返します。</summary>
    private bool TryGetForControl(ForStatementSyntax statement, out string control, out bool declaration,
        out ITypeSymbol controlType, out string typeName)
    {
        control = "";
        declaration = false;
        controlType = null!;
        typeName = "";
        ISymbol? symbol;
        switch (statement.ControlVariable)
        {
            case VariableDeclaratorSyntax variable when variable.Names.Count == 1:
                var name = variable.Names[0];
                symbol = _model.GetDeclaredSymbol(name);
                if (symbol is not ILocalSymbol local || local.IsConst) return false;
                control = name.Identifier.ValueText;
                declaration = true;
                controlType = local.Type;
                break;
            case IdentifierNameSyntax identifier:
                symbol = _model.GetSymbolInfo(identifier).Symbol;
                if (symbol is ILocalSymbol { IsConst: false } existingLocal)
                    controlType = existingLocal.Type;
                else if (symbol is IParameterSymbol parameter)
                    controlType = parameter.Type;
                else if (symbol is IFieldSymbol { IsConst: false, IsReadOnly: false } field)
                    controlType = field.Type;
                else
                    return false;
                control = Expr(identifier, true);
                break;
            default:
                return false;
        }

        typeName = CSharpNumericType(controlType) ?? "";
        return typeName.Length > 0;
    }

    /// <summary>For境界値が制御変数型へユーザー定義変換や縮小変換なしで代入可能か判定します。</summary>
    private bool CanAssignForValue(ExpressionSyntax expression, ITypeSymbol targetType)
    {
        var conversion = _model.ClassifyConversion(expression, targetType);
        return conversion.Exists && !conversion.IsNarrowing && !conversion.IsUserDefined;
    }
}
