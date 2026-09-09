using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using CSharpSyntaxFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;
using VBSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace VbToCSharpFixer;

/// <summary>
/// A conservative VB syntax-tree translator. SemanticModel decides every call/indexer
/// distinction; unsupported constructs are emitted as review comments, never guessed.
/// </summary>
public sealed class VbToCSharpConverter
{
    private readonly SymbolClassifier _classifier = new();
    private readonly List<FixResult> _fixes = [];
    private readonly List<ManualReviewItem> _reviews = [];
    private readonly HashSet<string> _visualBasicRuntimeTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _runtimeAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _withTargets = new();
    private bool _needsVisualBasicUsing;
    private SemanticModel _model = null!;
    private string _project = "";
    private string _file = "";
    private int _indent;

    /// <summary>VB SyntaxTree全体を意味解析結果に基づいてC#ソースへ変換します。</summary>
    public ConversionResult Convert(SyntaxTree tree, SemanticModel model, string projectName, string? rootNamespace = null)
    {
        _fixes.Clear();
        _reviews.Clear();
        _visualBasicRuntimeTypes.Clear();
        _runtimeAliases.Clear();
        _sourceIdentifiers.Clear();
        _withTargets.Clear();
        _needsVisualBasicUsing = false;
        _model = model;
        _project = projectName;
        _file = tree.FilePath;
        _indent = 0;
        var root = (CompilationUnitSyntax)tree.GetRoot();
        foreach (var token in root.DescendantTokens().Where(x => x.IsKind(VBSyntaxKind.IdentifierToken)))
            _sourceIdentifiers.Add(token.ValueText);
        var body = new StringBuilder();
        var globalMembers = root.Members.Where(IsGlobalNamespace).ToArray();
        var rootedMembers = root.Members.Where(x => !IsGlobalNamespace(x)).ToArray();
        if (!string.IsNullOrWhiteSpace(rootNamespace) && rootedMembers.Length > 0)
        {
            Line(body, $"namespace {rootNamespace}");
            Block(body, () => { foreach (var member in rootedMembers) WriteStatement(member, body); });
        }
        else
        {
            foreach (var member in rootedMembers) WriteStatement(member, body);
        }
        foreach (var member in globalMembers) WriteStatement(member, body);

        var output = new StringBuilder();
        var imports = root.Imports.SelectMany(x => x.ImportsClauses).Select(x => x.ToString()).ToList();
        if (_needsVisualBasicUsing && !imports.Contains("Microsoft.VisualBasic", StringComparer.Ordinal))
            imports.Add("Microsoft.VisualBasic");
        foreach (var import in imports.Distinct(StringComparer.Ordinal))
            output.Append("using ").Append(import).AppendLine(";");
        foreach (var alias in _runtimeAliases.OrderBy(x => x.Value, StringComparer.Ordinal))
            output.Append("using ").Append(alias.Value).Append(" = global::").Append(alias.Key).AppendLine(";");
        if (imports.Count > 0 || _runtimeAliases.Count > 0) output.AppendLine();
        output.Append(body);
        return new(output.ToString(), _fixes.ToArray(), _reviews.ToArray(), _visualBasicRuntimeTypes.Order().ToArray());
    }

    /// <summary>テストや部分変換向けに単一のVB式をC#表現へ変換します。</summary>
    public string ConvertExpression(ExpressionSyntax expression, SemanticModel model, string projectName = "Test")
    {
        _model = model;
        _project = projectName;
        _file = expression.SyntaxTree.FilePath;
        _fixes.Clear();
        _reviews.Clear();
        _visualBasicRuntimeTypes.Clear();
        _runtimeAliases.Clear();
        _sourceIdentifiers.Clear();
        _withTargets.Clear();
        _needsVisualBasicUsing = false;
        foreach (var token in expression.SyntaxTree.GetRoot().DescendantTokens().Where(x => x.IsKind(VBSyntaxKind.IdentifierToken)))
            _sourceIdentifiers.Add(token.ValueText);
        return Expr(expression);
    }

    /// <summary>VBステートメントの種類に応じたC#構文を出力します。</summary>
    private void WriteStatement(StatementSyntax statement, StringBuilder output)
    {
        WriteLeadingComments(statement, output);
        switch (statement)
        {
            case NamespaceBlockSyntax n:
                var namespaceName = n.NamespaceStatement.Name.ToString();
                if (namespaceName.StartsWith("Global.", StringComparison.OrdinalIgnoreCase))
                    namespaceName = namespaceName["Global.".Length..];
                Line(output, $"namespace {namespaceName}");
                Block(output, () => { foreach (var m in n.Members) WriteStatement(m, output); });
                break;
            case ClassBlockSyntax c:
                WriteType(c.ClassStatement, c.Members, "class", output);
                break;
            case StructureBlockSyntax s:
                WriteType(s.StructureStatement, s.Members, "struct", output);
                break;
            case InterfaceBlockSyntax i:
                WriteType(i.InterfaceStatement, i.Members, "interface", output);
                break;
            case ModuleBlockSyntax m:
                WriteType(m.ModuleStatement, m.Members, "static class", output);
                break;
            case EnumBlockSyntax e:
                WriteEnum(e, output);
                break;
            case MethodBlockSyntax m:
                WriteMethod(m, output);
                break;
            case MethodStatementSyntax declaration:
                Line(output, MethodSignature(declaration) + ";");
                break;
            case PropertyBlockSyntax p:
                WriteProperty(p, output);
                break;
            case PropertyStatementSyntax p:
                Line(output, PropertySignature(p) + " { get; set; }");
                break;
            case FieldDeclarationSyntax f:
                WriteDeclaration(f.Modifiers.ToString(), f.Declarators, output, true);
                break;
            case LocalDeclarationStatementSyntax l:
                WriteDeclaration("", l.Declarators, output, false);
                break;
            case AssignmentStatementSyntax a:
                Line(output, $"{Expr(a.Left)} {AssignmentOperator(a.Kind())} {ExprForTarget(a.Right, _model.GetTypeInfo(a.Left).Type)};");
                break;
            case ExpressionStatementSyntax e:
                Line(output, Expr(e.Expression) + ";");
                break;
            case CallStatementSyntax c:
                Line(output, Expr(c.Invocation) + ";");
                break;
            case ReturnStatementSyntax r:
                var method = _model.GetEnclosingSymbol(r.SpanStart) as IMethodSymbol;
                Line(output, r.Expression is null ? "return;" : $"return {ExprForTarget(r.Expression, method?.ReturnType)};");
                break;
            case ThrowStatementSyntax t:
                Line(output, t.Expression is null ? "throw;" : $"throw {Expr(t.Expression)};");
                break;
            case TryBlockSyntax t:
                WriteTryBlock(t, output);
                break;
            case ForBlockSyntax f:
                WriteForBlock(f, output);
                break;
            case ForEachBlockSyntax f:
                WriteForEachBlock(f, output);
                break;
            case WithBlockSyntax w:
                WriteWithBlock(w, output);
                break;
            case ContinueStatementSyntax c when c.BlockKeyword.IsKind(VBSyntaxKind.ForKeyword):
                Line(output, "continue;");
                break;
            case ExitStatementSyntax e when e.BlockKeyword.IsKind(VBSyntaxKind.ForKeyword):
                Line(output, "break;");
                break;
            case MultiLineIfBlockSyntax i:
                Line(output, $"if ({Expr(i.IfStatement.Condition)})");
                Block(output, () => { foreach (var x in i.Statements) WriteStatement(x, output); });
                foreach (var e in i.ElseIfBlocks)
                {
                    Line(output, $"else if ({Expr(e.ElseIfStatement.Condition)})");
                    Block(output, () => { foreach (var x in e.Statements) WriteStatement(x, output); });
                }
                if (i.ElseBlock is not null)
                {
                    Line(output, "else");
                    Block(output, () => { foreach (var x in i.ElseBlock.Statements) WriteStatement(x, output); });
                }
                break;
            case EmptyStatementSyntax:
                break;
            default:
                Review(statement, ReasonCode.UnsupportedSyntax, $"Unsupported VB statement: {statement.Kind()}");
                Line(output, $"// ManualReviewRequired: unsupported {statement.Kind()}: {OneLine(statement.ToString())}");
                break;
        }
    }

    /// <summary>クラス、構造体、InterfaceまたはModuleの宣言とメンバーを出力します。</summary>
    private void WriteType(TypeStatementSyntax type, SyntaxList<StatementSyntax> members, string keyword, StringBuilder output)
    {
        var access = Access(type.Modifiers);
        var inheritance = type.Parent switch
        {
            TypeBlockSyntax block when block.Inherits.Count > 0 => " : " + string.Join(", ", block.Inherits.SelectMany(x => x.Types).Select(Type)),
            _ => ""
        };
        Line(output, $"{access}{keyword} {type.Identifier.ValueText}{inheritance}".TrimStart());
        Block(output, () => { foreach (var m in members) WriteStatement(m, output); });
    }

    /// <summary>VBのEnum宣言、基底型、属性および各列挙値をC#として出力します。</summary>
    private void WriteEnum(EnumBlockSyntax block, StringBuilder output)
    {
        var statement = block.EnumStatement;
        WriteAttributes(statement.AttributeLists, output);
        var symbol = _model.GetDeclaredSymbol(statement) as INamedTypeSymbol;
        var name = EscapeIdentifier(symbol?.Name ?? statement.Identifier.ValueText);
        var underlyingType = statement.UnderlyingType is null ? "" : " : " + Type(statement.UnderlyingType.Type());
        Line(output, $"{Access(statement.Modifiers)}enum {name}{underlyingType}".TrimStart());
        Line(output, "{");
        _indent++;
        var members = block.Members.OfType<EnumMemberDeclarationSyntax>().ToArray();
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            WriteLeadingComments(member, output);
            WriteAttributes(member.AttributeLists, output);
            var memberSymbol = _model.GetDeclaredSymbol(member) as IFieldSymbol;
            var memberName = EscapeIdentifier(memberSymbol?.Name ?? member.Identifier.ValueText);
            var initializer = member.Initializer is null ? "" : " = " + Expr(member.Initializer.Value);
            Line(output, $"{memberName}{initializer}{(index + 1 < members.Length ? "," : "")}");
        }
        _indent--;
        Line(output, "}");
    }

    /// <summary>VB属性リストをC#属性として出力します。</summary>
    private void WriteAttributes(SyntaxList<AttributeListSyntax> lists, StringBuilder output)
    {
        foreach (var list in lists)
            Line(output, "[" + string.Join(", ", list.Attributes.Select(Attribute)) + "]");
    }

    /// <summary>VB属性の名前、位置引数および名前付き引数をC#表現へ変換します。</summary>
    private string Attribute(AttributeSyntax attribute)
    {
        var symbol = _model.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
        var name = (symbol?.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? attribute.Name.ToString()).Replace("Global.", "global::", StringComparison.Ordinal);
        if (attribute.ArgumentList is null) return name;
        var arguments = attribute.ArgumentList.Arguments.Select(argument =>
        {
            if (argument is not SimpleArgumentSyntax simple) return argument.ToString();
            if (simple.NameColonEquals is null) return Expr(simple.Expression);
            var requestedName = simple.NameColonEquals.Name.Identifier.ValueText;
            var constructorParameter = symbol?.Parameters.FirstOrDefault(x => x.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
            if (constructorParameter is not null)
                return $"{EscapeIdentifier(constructorParameter.Name)}: {ExprForTarget(simple.Expression, constructorParameter.Type)}";
            var namedMember = symbol?.ContainingType.GetMembers()
                .FirstOrDefault(x => x.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase) && x is IFieldSymbol or IPropertySymbol);
            var memberName = EscapeIdentifier(namedMember?.Name ?? requestedName);
            var memberType = namedMember switch { IFieldSymbol field => field.Type, IPropertySymbol property => property.Type, _ => null };
            return $"{memberName} = {ExprForTarget(simple.Expression, memberType)}";
        });
        return $"{name}({string.Join(", ", arguments)})";
    }

    /// <summary>VBメソッドブロックをC#メソッドとして出力します。</summary>
    private void WriteMethod(MethodBlockSyntax method, StringBuilder output)
    {
        Line(output, MethodSignature(method.SubOrFunctionStatement));
        Block(output, () => { foreach (var s in method.Statements) WriteStatement(s, output); });
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

        var limit = CreateUniqueTemporaryName("__forLimit");
        var step = CreateUniqueTemporaryName("__forStep");
        Line(output, "{");
        _indent++;
        Line(output, declaration
            ? $"{typeName} {control} = {Expr(statement.FromValue)};"
            : $"{control} = {Expr(statement.FromValue)};");
        Line(output, $"{typeName} {limit} = {Expr(statement.ToValue)};");
        Line(output, $"{typeName} {step} = {(statement.StepClause is null ? "1" : Expr(statement.StepClause.StepValue))};");
        Line(output, $"for (; ({step} >= 0 ? {control} <= {limit} : {control} >= {limit}); {control} += {step})");
        Block(output, () => { foreach (var child in block.Statements) WriteStatement(child, output); });
        _indent--;
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
        _indent++;
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
        _indent--;
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

    /// <summary>Forで安全に扱う組み込み数値型をC#キーワードへ対応付けます。</summary>
    private static string? CSharpNumericType(ITypeSymbol type) => type.SpecialType switch
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
    private static string? CSharpTypeName(ITypeSymbol type)
    {
        var keyword = type.SpecialType switch
        {
            SpecialType.System_Object => "object",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Char => "char",
            SpecialType.System_String => "string",
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
            var namespaceName = named.ContainingNamespace?.ToDisplayString();
            prefix = string.IsNullOrEmpty(namespaceName) ? "" : "global::" + namespaceName + ".";
        }
        if (named.Arity == 0) return prefix + named.Name;
        var arguments = named.TypeArguments.Skip(named.TypeArguments.Length - named.Arity)
            .Select(CSharpTypeName).ToArray();
        return arguments.Any(x => x is null)
            ? null
            : prefix + named.Name + "<" + string.Join(", ", arguments!) + ">";
    }

    /// <summary>ソース識別子および既に生成した名前と衝突しない一時変数名を作成します。</summary>
    private string CreateUniqueTemporaryName(string baseName)
    {
        var candidate = baseName;
        for (var suffix = 2; _sourceIdentifiers.Contains(candidate); suffix++) candidate = baseName + suffix;
        _sourceIdentifiers.Add(candidate);
        return candidate;
    }

    /// <summary>VBメソッド宣言からC#のメソッドシグネチャを生成します。</summary>
    private string MethodSignature(MethodStatementSyntax method)
    {
        var access = Access(method.Modifiers);
        var shared = method.Modifiers.Any(VBSyntaxKind.SharedKeyword) ? "static " : "";
        var returnType = method.Kind() == VBSyntaxKind.SubStatement ? "void" : Type(method.AsClause?.Type());
        var parameters = string.Join(", ", method.ParameterList?.Parameters.Select(Parameter) ?? []);
        return $"{access}{shared}{returnType} {method.Identifier.ValueText}({parameters})".TrimStart();
    }

    /// <summary>VBプロパティとアクセサーブロックをC#として出力します。</summary>
    private void WriteProperty(PropertyBlockSyntax property, StringBuilder output)
    {
        Line(output, PropertySignature(property.PropertyStatement));
        Block(output, () =>
        {
            foreach (var accessor in property.Accessors)
            {
                Line(output, accessor.Kind() == VBSyntaxKind.GetAccessorBlock ? "get" : "set");
                Block(output, () => { foreach (var s in accessor.Statements) WriteStatement(s, output); });
            }
        });
    }

    /// <summary>通常プロパティまたはIndexerのC#シグネチャを生成します。</summary>
    private string PropertySignature(PropertyStatementSyntax property)
    {
        var access = Access(property.Modifiers);
        var type = Type(property.AsClause?.Type());
        var parameters = property.ParameterList?.Parameters ?? default;
        if (parameters.Count > 0)
            return $"{access}{type} this[{string.Join(", ", parameters.Select(Parameter))}]".TrimStart();
        return $"{access}{type} {property.Identifier.ValueText}".TrimStart();
    }

    /// <summary>フィールドまたはローカル変数の宣言をC#として出力します。</summary>
    private void WriteDeclaration(string modifierText, SeparatedSyntaxList<VariableDeclaratorSyntax> declarators, StringBuilder output, bool field)
    {
        foreach (var d in declarators)
        {
            foreach (var name in d.Names)
            {
                var type = Type(d.AsClause?.Type());
                if (!field && d.AsClause is null && d.Initializer is not null) type = "var";
                var prefix = field ? AccessText(modifierText) : "";
                var declared = _model.GetDeclaredSymbol(name);
                var targetType = declared switch
                {
                    ILocalSymbol local => local.Type,
                    IFieldSymbol declaredField => declaredField.Type,
                    _ => null
                };
                var init = d.Initializer is null ? "" : " = " + ExprForTarget(d.Initializer.Value, targetType);
                Line(output, $"{prefix}{type} {name.Identifier.ValueText}{init};".TrimStart());
            }
        }
    }

    /// <summary>VB式を種類別にC#式へ変換します。</summary>
    private string Expr(ExpressionSyntax node, bool suppressImplicitCall = false)
    {
        string result = node switch
        {
            InvocationExpressionSyntax invocation => Invocation(invocation),
            MemberAccessExpressionSyntax member => Member(member, suppressImplicitCall),
            IdentifierNameSyntax id => Identifier(id, suppressImplicitCall),
            MeExpressionSyntax => "this",
            MyBaseExpressionSyntax => "base",
            LiteralExpressionSyntax literal => Literal(literal),
            ParenthesizedExpressionSyntax p => $"({Expr(p.Expression)})",
            BinaryExpressionSyntax b => Binary(b),
            UnaryExpressionSyntax u => Unary(u),
            ObjectCreationExpressionSyntax o => ObjectCreation(o),
            PredefinedCastExpressionSyntax c => PredefinedCast(c),
            CTypeExpressionSyntax c => $"({Type(c.Type)}){Expr(c.Expression)}",
            DirectCastExpressionSyntax c => $"({Type(c.Type)}){Expr(c.Expression)}",
            TryCastExpressionSyntax c => $"{Expr(c.Expression)} as {Type(c.Type)}",
            TernaryConditionalExpressionSyntax c => $"{Expr(c.Condition)} ? {Expr(c.WhenTrue)} : {Expr(c.WhenFalse)}",
            _ => UnsupportedExpression(node)
        };
        return result;
    }

    /// <summary>呼び出し式をメソッド、配列またはIndexerとして意味的に変換します。</summary>
    private string Invocation(InvocationExpressionSyntax node)
    {
        var classification = _classifier.ClassifyInvocation(node, _model);
        var parameters = classification.Symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => default
        };
        var args = Arguments(node.ArgumentList, parameters);
        string after;
        FixType fixType;
        switch (classification.Meaning)
        {
            case ExpressionMeaning.Method:
                if (classification.Symbol is IMethodSymbol method && IsVisualBasicRuntimeMethod(method))
                {
                    after = $"{VisualBasicRuntimeTypeAccess(method)}.{method.Name}({args})";
                    fixType = FixType.VbRuntimeCall;
                }
                else
                {
                    after = $"{Expr(node.Expression, true)}({args})";
                    fixType = FixType.MethodCall;
                }
                break;
            case ExpressionMeaning.Array:
                after = $"{Expr(node.Expression, true)}[{args}]";
                fixType = FixType.ArrayAccess;
                break;
            case ExpressionMeaning.Indexer:
                after = $"{IndexerTarget(node.Expression, classification.Symbol as IPropertySymbol)}[{args}]";
                fixType = FixType.Indexer;
                break;
            default:
                Review(node, classification.Meaning == ExpressionMeaning.Ambiguous ? ReasonCode.AmbiguousSymbol : ReasonCode.UnresolvedSymbol, classification.Reason);
                return $"/* ManualReviewRequired */ {node}";
        }
        Record(node, fixType, after, classification);
        return after;
    }

    /// <summary>Itemプロパティを除去してC#Indexerの対象式を生成します。</summary>
    private string IndexerTarget(ExpressionSyntax expression, IPropertySymbol? property)
    {
        if (expression is MemberAccessExpressionSyntax member &&
            property is not null && string.Equals(member.Name.Identifier.ValueText, property.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(property.Name, "Item", StringComparison.OrdinalIgnoreCase))
        {
            if (HasOmittedWithReceiver(member))
                return _withTargets.Count > 0 ? _withTargets.Peek() : UnsupportedExpression(member);
            return Expr(member.Expression, true);
        }
        return Expr(expression, true);
    }

    /// <summary>メンバーアクセスを変換し、引数なしメソッドには呼び出し括弧を追加します。</summary>
    private string Member(MemberAccessExpressionSyntax node, bool suppressImplicitCall)
    {
        string receiver;
        if (HasOmittedWithReceiver(node))
        {
            if (_withTargets.Count == 0) return UnsupportedExpression(node);
            receiver = _withTargets.Peek();
        }
        else
        {
            receiver = Expr(node.Expression);
        }
        var symbol = _model.GetSymbolInfo(node).Symbol;
        var memberName = symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } enumField
            ? EscapeIdentifier(enumField.Name)
            : EscapeIdentifier(node.Name.Identifier.ValueText);
        var value = $"{receiver}.{memberName}";
        if (suppressImplicitCall) return value;
        var classification = _classifier.ClassifyExpression(node, _model);
        if (classification.Symbol is IMethodSymbol { Parameters.Length: 0 } && classification.Meaning == ExpressionMeaning.Method)
        {
            var method = (IMethodSymbol)classification.Symbol;
            var isRuntime = IsVisualBasicRuntimeMethod(method);
            var after = isRuntime ? $"{VisualBasicRuntimeTypeAccess(method)}.{method.Name}()" : value + "()";
            Record(node, isRuntime ? FixType.VbRuntimeCall : FixType.MethodCall, after, classification);
            return after;
        }
        return value;
    }

    /// <summary>識別子を変換し、暗黙の引数なしメソッド呼び出しを補正します。</summary>
    private string Identifier(IdentifierNameSyntax node, bool suppressImplicitCall)
    {
        var name = node.Identifier.ValueText switch { "Me" => "this", "MyBase" => "base", var x => EscapeIdentifier(x) };
        if (suppressImplicitCall) return name;
        var classification = _classifier.ClassifyExpression(node, _model);
        var resolvedSymbol = classification.Symbol ?? _model.GetSymbolInfo(node).Symbol;
        if (resolvedSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            return EnumTypeName(enumType);
        if (resolvedSymbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } enumField)
        {
            var memberName = EscapeIdentifier(enumField.Name);
            if (IsInsideDeclaringEnum(node, enumField)) return memberName;
            return $"{EnumTypeName(enumField.ContainingType)}.{memberName}";
        }
        if (classification.Symbol is { } symbol && IsVisualBasicRuntimeValueMember(symbol))
        {
            var after = $"{VisualBasicRuntimeTypeAccess(symbol.ContainingType)}.{symbol.Name}";
            Record(node, FixType.VbRuntimeMember, after, classification);
            return after;
        }
        if (classification.Symbol is IMethodSymbol { Parameters.Length: 0 })
        {
            var method = (IMethodSymbol)classification.Symbol;
            var isRuntime = IsVisualBasicRuntimeMethod(method);
            var after = isRuntime ? $"{VisualBasicRuntimeTypeAccess(method)}.{method.Name}()" : name + "()";
            Record(node, isRuntime ? FixType.VbRuntimeCall : FixType.MethodCall, after, classification);
            return after;
        }
        return name;
    }

    /// <summary>VB引数リスト内の各式をC#へ変換して連結します。</summary>
    private string Arguments(ArgumentListSyntax? list, ImmutableArray<IParameterSymbol> parameters = default)
    {
        if (list is null) return "";
        return string.Join(", ", list.Arguments.Select((argument, index) =>
        {
            if (argument is not SimpleArgumentSyntax simple) return argument.ToString();
            IParameterSymbol? parameter = null;
            if (!parameters.IsDefaultOrEmpty)
            {
                parameter = simple.NameColonEquals is not null
                    ? parameters.FirstOrDefault(x => x.Name.Equals(simple.NameColonEquals.Name.Identifier.ValueText, StringComparison.OrdinalIgnoreCase))
                    : parameters[Math.Min(index, parameters.Length - 1)];
            }
            var targetType = parameter is { IsParams: true, Type: IArrayTypeSymbol array } && index >= parameters.Length - 1
                ? array.ElementType : parameter?.Type;
            var prefix = simple.NameColonEquals is null ? "" : EscapeIdentifier(simple.NameColonEquals.Name.Identifier.ValueText) + ": ";
            return prefix + ExprForTarget(simple.Expression, targetType);
        }));
    }

    /// <summary>Object生成式のコンストラクター引数にも必要なEnum変換を適用します。</summary>
    private string ObjectCreation(ObjectCreationExpressionSyntax expression)
    {
        var constructor = _model.GetSymbolInfo(expression).Symbol as IMethodSymbol;
        return $"new {Type(expression.Type)}({Arguments(expression.ArgumentList, constructor?.Parameters ?? default)})";
    }

    /// <summary>CIntやCStrなどのVB組み込み変換をConversionsクラス呼び出しへ変換します。</summary>
    private string PredefinedCast(PredefinedCastExpressionSyntax expression)
    {
        var method = expression.Keyword.ValueText.ToUpperInvariant() switch
        {
            "CBOOL" => "ToBoolean", "CBYTE" => "ToByte", "CSBYTE" => "ToSByte",
            "CSHORT" => "ToShort", "CUSHORT" => "ToUShort", "CINT" => "ToInteger",
            "CUINT" => "ToUInteger", "CLNG" => "ToLong", "CULNG" => "ToULong",
            "CSNG" => "ToSingle", "CDBL" => "ToDouble", "CDEC" => "ToDecimal",
            "CCHAR" => "ToChar", "CDATE" => "ToDate", "CSTR" => "ToString",
            "COBJ" => null,
            _ => ""
        };
        var value = expression.Expression.IsKind(VBSyntaxKind.NothingLiteralExpression)
            ? "(object)null" : Expr(expression.Expression);
        if (method is null) return $"(object)({value})";
        if (method.Length == 0) return UnsupportedExpression(expression);
        var conversions = _model.Compilation.GetTypeByMetadataName("Microsoft.VisualBasic.CompilerServices.Conversions");
        if (conversions is null)
        {
            Review(expression, ReasonCode.MissingReference, "Microsoft.VisualBasic.CompilerServices.Conversions could not be resolved.");
            return $"/* ManualReviewRequired */ {expression}";
        }
        var after = $"{VisualBasicRuntimeTypeAccess(conversions)}.{method}({value})";
        Record(expression, FixType.VbRuntimeCall, after,
            new SymbolClassification(ExpressionMeaning.Value, conversions, _model.GetTypeInfo(expression).Type,
                $"VB predefined conversion {expression.Keyword.ValueText} mapped to Conversions.{method}"));
        return after;
    }

    /// <summary>Enumと整数型の間でC#に明示変換が必要な場合だけキャストを追加します。</summary>
    private string ExprForTarget(ExpressionSyntax expression, ITypeSymbol? targetType)
    {
        var value = Expr(expression);
        if (targetType is null) return value;
        var sourceType = _model.GetTypeInfo(expression).Type;
        if (sourceType is null || SymbolEqualityComparer.Default.Equals(sourceType, targetType)) return value;
        if (targetType.TypeKind == TypeKind.Enum && IsIntegral(sourceType))
            return $"({EnumTypeName((INamedTypeSymbol)targetType)})({value})";
        if (sourceType.TypeKind == TypeKind.Enum && IsIntegral(targetType))
            return $"({TypeName(targetType)})({value})";
        return value;
    }

    /// <summary>Enumメンバー参照が同じEnum宣言の初期化式内にあるか判定します。</summary>
    private bool IsInsideDeclaringEnum(SyntaxNode node, IFieldSymbol field)
    {
        var block = node.Ancestors().OfType<EnumBlockSyntax>().FirstOrDefault();
        var containing = block is null ? null : _model.GetDeclaredSymbol(block.EnumStatement);
        return SymbolEqualityComparer.Default.Equals(containing, field.ContainingType);
    }

    /// <summary>VBリテラルを対応するC#リテラル表現へ変換します。</summary>
    private static string Literal(LiteralExpressionSyntax literal) => literal.Kind() switch
    {
        VBSyntaxKind.NothingLiteralExpression => "null",
        VBSyntaxKind.TrueLiteralExpression => "true",
        VBSyntaxKind.FalseLiteralExpression => "false",
        VBSyntaxKind.StringLiteralExpression => StringLiteral((string)literal.Token.Value!),
        VBSyntaxKind.CharacterLiteralExpression => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral((char)literal.Token.Value!, true),
        _ => literal.Token.ValueText
    };

    /// <summary>実タブを維持しつつC#として安全な文字列リテラルを生成します。</summary>
    private static string StringLiteral(string value)
    {
        var output = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\t': output.Append('\t'); break;
                case '\0': output.Append("\\0"); break;
                case '\a': output.Append("\\a"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\v': output.Append("\\v"); break;
                case '\u2028': output.Append("\\u2028"); break;
                case '\u2029': output.Append("\\u2029"); break;
                default:
                    if (char.IsControl(character)) output.Append("\\u").Append(((int)character).ToString("x4"));
                    else output.Append(character);
                    break;
            }
        }
        return output.Append('"').ToString();
    }

    /// <summary>VB型構文をC#の組み込み型、Genericまたは配列型表現へ変換します。</summary>
    private string Type(TypeSyntax? type)
    {
        if (type is not null)
        {
            var resolvedType = _model.GetTypeInfo(type).Type ?? _model.GetSymbolInfo(type).Symbol as ITypeSymbol;
            if (resolvedType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                return EnumTypeName(enumType);
        }
        return type switch
        {
            null => "object",
            PredefinedTypeSyntax p => p.Keyword.Kind() switch
            {
                VBSyntaxKind.StringKeyword => "string", VBSyntaxKind.IntegerKeyword => "int",
                VBSyntaxKind.LongKeyword => "long", VBSyntaxKind.ShortKeyword => "short",
                VBSyntaxKind.ULongKeyword => "ulong", VBSyntaxKind.UIntegerKeyword => "uint",
                VBSyntaxKind.UShortKeyword => "ushort", VBSyntaxKind.SByteKeyword => "sbyte",
                VBSyntaxKind.BooleanKeyword => "bool", VBSyntaxKind.ObjectKeyword => "object",
                VBSyntaxKind.DecimalKeyword => "decimal", VBSyntaxKind.DoubleKeyword => "double",
                VBSyntaxKind.SingleKeyword => "float", VBSyntaxKind.ByteKeyword => "byte",
                VBSyntaxKind.CharKeyword => "char", VBSyntaxKind.DateKeyword => "global::System.DateTime",
                _ => p.Keyword.ValueText
            },
            GenericNameSyntax g => $"{g.Identifier.ValueText}<{string.Join(", ", g.TypeArgumentList.Arguments.Select(Type))}>",
            ArrayTypeSyntax a => Type(a.ElementType) + string.Concat(a.RankSpecifiers.Select(r => "[" + new string(',', r.Rank - 1) + "]")),
            IdentifierNameSyntax identifier => EscapeIdentifier(identifier.Identifier.ValueText),
            _ => type.ToString().Replace("Global.", "global::", StringComparison.Ordinal)
        };
    }

    /// <summary>VBパラメーターをByRef指定を含むC#パラメーターへ変換します。</summary>
    private string Parameter(ParameterSyntax p)
    {
        var modifier = p.Modifiers.Any(VBSyntaxKind.ByRefKeyword) ? "ref " : "";
        return $"{modifier}{Type(p.AsClause?.Type())} {p.Identifier.Identifier.ValueText}";
    }

    /// <summary>VBアクセス修飾子からC#アクセス修飾子を生成します。</summary>
    private static string Access(SyntaxTokenList modifiers) =>
        modifiers.Any(VBSyntaxKind.PublicKeyword) ? "public " :
        modifiers.Any(VBSyntaxKind.ProtectedKeyword) ? "protected " :
        modifiers.Any(VBSyntaxKind.PrivateKeyword) ? "private " : "internal ";

    /// <summary>文字列化されたVB修飾子からフィールド用C#アクセス修飾子を生成します。</summary>
    private static string AccessText(string modifiers) =>
        modifiers.Contains("Public", StringComparison.OrdinalIgnoreCase) ? "public " :
        modifiers.Contains("Private", StringComparison.OrdinalIgnoreCase) ? "private " : "";

    /// <summary>VB代入ステートメント種別をC#代入演算子へ対応付けます。</summary>
    private static string AssignmentOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.AddAssignmentStatement => "+=", VBSyntaxKind.SubtractAssignmentStatement => "-=",
        VBSyntaxKind.MultiplyAssignmentStatement => "*=", VBSyntaxKind.DivideAssignmentStatement => "/=", _ => "="
    };

    /// <summary>参照同一性を専用処理し、それ以外のVB二項式をC#演算子で出力します。</summary>
    private string Binary(BinaryExpressionSyntax expression)
    {
        if (expression.IsKind(VBSyntaxKind.IsExpression) || expression.IsKind(VBSyntaxKind.IsNotExpression))
        {
            var comparison = $"object.ReferenceEquals({Expr(expression.Left)}, {Expr(expression.Right)})";
            return expression.IsKind(VBSyntaxKind.IsNotExpression) ? "!" + comparison : comparison;
        }
        var leftType = _model.GetTypeInfo(expression.Left).Type;
        var rightType = _model.GetTypeInfo(expression.Right).Type;
        var left = rightType?.TypeKind == TypeKind.Enum && leftType is not null && IsIntegral(leftType)
            ? ExprForTarget(expression.Left, rightType) : Expr(expression.Left);
        var right = leftType?.TypeKind == TypeKind.Enum && rightType is not null && IsIntegral(rightType)
            ? ExprForTarget(expression.Right, leftType) : Expr(expression.Right);
        return $"{left} {BinaryOperator(expression.Kind())} {right}";
    }

    /// <summary>VB二項演算子を対応するC#演算子へ変換します。</summary>
    private static string BinaryOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.EqualsExpression => "==", VBSyntaxKind.NotEqualsExpression => "!=",
        VBSyntaxKind.AndAlsoExpression => "&&", VBSyntaxKind.OrElseExpression => "||",
        VBSyntaxKind.AndExpression => "&", VBSyntaxKind.OrExpression => "|",
        VBSyntaxKind.ExclusiveOrExpression => "^", VBSyntaxKind.LeftShiftExpression => "<<",
        VBSyntaxKind.RightShiftExpression => ">>",
        VBSyntaxKind.ModuloExpression => "%", VBSyntaxKind.ConcatenateExpression => "+",
        _ => kind switch
        {
            VBSyntaxKind.AddExpression => "+", VBSyntaxKind.SubtractExpression => "-",
            VBSyntaxKind.MultiplyExpression => "*", VBSyntaxKind.DivideExpression => "/",
            VBSyntaxKind.LessThanExpression => "<", VBSyntaxKind.LessThanOrEqualExpression => "<=",
            VBSyntaxKind.GreaterThanExpression => ">", VBSyntaxKind.GreaterThanOrEqualExpression => ">=", _ => "/*?*/"
        }
    };

    /// <summary>VB単項演算子を対応するC#演算子へ変換します。</summary>
    private static string UnaryOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.NotExpression => "!", VBSyntaxKind.UnaryMinusExpression => "-", _ => "+"
    };

    /// <summary>NotをBooleanの論理否定またはEnum・整数のビット反転として意味的に変換します。</summary>
    private string Unary(UnaryExpressionSyntax expression)
    {
        var operation = UnaryOperator(expression.Kind());
        if (expression.IsKind(VBSyntaxKind.NotExpression))
        {
            var type = _model.GetTypeInfo(expression.Operand).Type;
            if (type?.TypeKind == TypeKind.Enum || type is not null && IsIntegral(type)) operation = "~";
        }
        return operation + Expr(expression.Operand);
    }

    /// <summary>列挙型を含む整数型か判定します。</summary>
    private static bool IsIntegral(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte or
        SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or
        SpecialType.System_Int64 or SpecialType.System_UInt64;

    /// <summary>C#コード中で使用するEnum型名を正式な大文字・小文字で返します。</summary>
    private static string EnumTypeName(INamedTypeSymbol type)
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
    private static string TypeName(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_SByte => "sbyte", SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 => "short", SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int", SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long", SpecialType.System_UInt64 => "ulong",
        _ => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
    };

    /// <summary>C#予約語と一致する識別子を@付き識別子へ変換します。</summary>
    private static string EscapeIdentifier(string name) =>
        CSharpSyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ||
        CSharpSyntaxFacts.GetContextualKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name : name;

    /// <summary>未対応式をManualReviewRequiredとして記録し、安全なプレースホルダーを返します。</summary>
    private string UnsupportedExpression(ExpressionSyntax node)
    {
        Review(node, ReasonCode.UnsupportedSyntax, $"Unsupported VB expression: {node.Kind()}");
        return $"/* ManualReviewRequired: {OneLine(node.ToString())} */ default";
    }

    /// <summary>変換位置、前後コード、シンボル根拠を変換ログへ記録します。</summary>
    private void Record(SyntaxNode node, FixType type, string after, SymbolClassification classification)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        var symbol = classification.Symbol;
        _fixes.Add(new(_project, _file, position.Line + 1, position.Character + 1, type,
            node.ToString(), after, classification.Reason, symbol?.Kind.ToString(),
            symbol?.ContainingType?.ToDisplayString(), symbol?.ContainingAssembly?.Identity.Name));
    }

    /// <summary>自動変換できない構文をManualReviewRequiredへ追加します。</summary>
    private void Review(SyntaxNode node, ReasonCode code, string details)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        _reviews.Add(new(_project, _file, position.Line + 1, position.Character + 1,
            OneLine(node.ToString()), code, details));
    }

    /// <summary>VBの先行コメントをC#行コメントとして出力します。</summary>
    private void WriteLeadingComments(SyntaxNode node, StringBuilder output)
    {
        foreach (var trivia in node.GetLeadingTrivia().Where(t => t.IsKind(VBSyntaxKind.CommentTrivia)))
            Line(output, "//" + trivia.ToString().TrimStart('\''));
    }

    /// <summary>インデントを管理しながらC#の波括弧ブロックを出力します。</summary>
    private void Block(StringBuilder output, Action body)
    {
        Line(output, "{"); _indent++; body(); _indent--; Line(output, "}");
    }

    /// <summary>現在のインデントを付けて1行出力します。</summary>
    private void Line(StringBuilder output, string value) => output.Append(' ', _indent * 4).AppendLine(value);

    /// <summary>複数行文字列をログ向けの単一行へ整形します。</summary>
    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>メソッドがMicrosoft.VisualBasicランタイム由来かをAssemblyとNamespaceから判定します。</summary>
    private static bool IsVisualBasicRuntimeMethod(IMethodSymbol method) =>
        IsVisualBasicRuntimeSymbol(method);

    /// <summary>静的なMicrosoft.VisualBasicフィールドまたはプロパティか判定します。</summary>
    private static bool IsVisualBasicRuntimeValueMember(ISymbol symbol) =>
        symbol.IsStatic && (symbol is IFieldSymbol || symbol is IPropertySymbol) && IsVisualBasicRuntimeSymbol(symbol);

    /// <summary>シンボルがMicrosoft.VisualBasicアセンブリと名前空間に属するか判定します。</summary>
    private static bool IsVisualBasicRuntimeSymbol(ISymbol symbol) =>
        symbol.ContainingAssembly?.Identity.Name is { } assemblyName &&
        (assemblyName.Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase) ||
         assemblyName.Equals("Microsoft.VisualBasic.Core", StringComparison.OrdinalIgnoreCase)) &&
        (symbol.ContainingNamespace?.ToDisplayString().Equals("Microsoft.VisualBasic", StringComparison.Ordinal) == true ||
         symbol.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.VisualBasic.", StringComparison.Ordinal) == true);

    /// <summary>VBランタイム型について通常名または衝突回避aliasによるC#アクセス表現を返します。</summary>
    private string VisualBasicRuntimeTypeAccess(IMethodSymbol method) => VisualBasicRuntimeTypeAccess(method.ContainingType);

    /// <summary>VBランタイム型について通常名または衝突回避aliasによるC#アクセス表現を返します。</summary>
    private string VisualBasicRuntimeTypeAccess(INamedTypeSymbol containingType)
    {
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

    /// <summary>VBのGlobal名前空間宣言でありRootNamespaceを適用しないメンバーか判定します。</summary>
    private static bool IsGlobalNamespace(StatementSyntax statement) =>
        statement is NamespaceBlockSyntax block &&
        block.NamespaceStatement.Name.ToString().StartsWith("Global.", StringComparison.OrdinalIgnoreCase);
}

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
