using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
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
    private bool _needsVisualBasicUsing;
    private SemanticModel _model = null!;
    private string _project = "";
    private string _file = "";
    private int _indent;

    public ConversionResult Convert(SyntaxTree tree, SemanticModel model, string projectName, string? rootNamespace = null)
    {
        _fixes.Clear();
        _reviews.Clear();
        _visualBasicRuntimeTypes.Clear();
        _runtimeAliases.Clear();
        _sourceIdentifiers.Clear();
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
        _needsVisualBasicUsing = false;
        foreach (var token in expression.SyntaxTree.GetRoot().DescendantTokens().Where(x => x.IsKind(VBSyntaxKind.IdentifierToken)))
            _sourceIdentifiers.Add(token.ValueText);
        return Expr(expression);
    }

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
                Line(output, $"{Expr(a.Left)} {AssignmentOperator(a.Kind())} {Expr(a.Right)};");
                break;
            case ExpressionStatementSyntax e:
                Line(output, Expr(e.Expression) + ";");
                break;
            case CallStatementSyntax c:
                Line(output, Expr(c.Invocation) + ";");
                break;
            case ReturnStatementSyntax r:
                Line(output, r.Expression is null ? "return;" : $"return {Expr(r.Expression)};");
                break;
            case ThrowStatementSyntax t:
                Line(output, t.Expression is null ? "throw;" : $"throw {Expr(t.Expression)};");
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

    private void WriteMethod(MethodBlockSyntax method, StringBuilder output)
    {
        Line(output, MethodSignature(method.SubOrFunctionStatement));
        Block(output, () => { foreach (var s in method.Statements) WriteStatement(s, output); });
    }

    private string MethodSignature(MethodStatementSyntax method)
    {
        var access = Access(method.Modifiers);
        var shared = method.Modifiers.Any(VBSyntaxKind.SharedKeyword) ? "static " : "";
        var returnType = method.Kind() == VBSyntaxKind.SubStatement ? "void" : Type(method.AsClause?.Type());
        var parameters = string.Join(", ", method.ParameterList?.Parameters.Select(Parameter) ?? []);
        return $"{access}{shared}{returnType} {method.Identifier.ValueText}({parameters})".TrimStart();
    }

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

    private string PropertySignature(PropertyStatementSyntax property)
    {
        var access = Access(property.Modifiers);
        var type = Type(property.AsClause?.Type());
        var parameters = property.ParameterList?.Parameters ?? default;
        if (parameters.Count > 0)
            return $"{access}{type} this[{string.Join(", ", parameters.Select(Parameter))}]".TrimStart();
        return $"{access}{type} {property.Identifier.ValueText}".TrimStart();
    }

    private void WriteDeclaration(string modifierText, SeparatedSyntaxList<VariableDeclaratorSyntax> declarators, StringBuilder output, bool field)
    {
        foreach (var d in declarators)
        {
            foreach (var name in d.Names)
            {
                var type = Type(d.AsClause?.Type());
                if (!field && d.AsClause is null && d.Initializer is not null) type = "var";
                var prefix = field ? AccessText(modifierText) : "";
                var init = d.Initializer is null ? "" : " = " + Expr(d.Initializer.Value);
                Line(output, $"{prefix}{type} {name.Identifier.ValueText}{init};".TrimStart());
            }
        }
    }

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
            BinaryExpressionSyntax b => $"{Expr(b.Left)} {BinaryOperator(b.Kind())} {Expr(b.Right)}",
            UnaryExpressionSyntax u => $"{UnaryOperator(u.Kind())}{Expr(u.Operand)}",
            ObjectCreationExpressionSyntax o => $"new {Type(o.Type)}({Arguments(o.ArgumentList)})",
            CTypeExpressionSyntax c => $"({Type(c.Type)}){Expr(c.Expression)}",
            DirectCastExpressionSyntax c => $"({Type(c.Type)}){Expr(c.Expression)}",
            TryCastExpressionSyntax c => $"{Expr(c.Expression)} as {Type(c.Type)}",
            TernaryConditionalExpressionSyntax c => $"{Expr(c.Condition)} ? {Expr(c.WhenTrue)} : {Expr(c.WhenFalse)}",
            _ => UnsupportedExpression(node)
        };
        return result;
    }

    private string Invocation(InvocationExpressionSyntax node)
    {
        var classification = _classifier.ClassifyInvocation(node, _model);
        var args = Arguments(node.ArgumentList);
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

    private string IndexerTarget(ExpressionSyntax expression, IPropertySymbol? property)
    {
        if (expression is MemberAccessExpressionSyntax member &&
            property is not null && string.Equals(member.Name.Identifier.ValueText, property.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(property.Name, "Item", StringComparison.OrdinalIgnoreCase))
            return Expr(member.Expression, true);
        return Expr(expression, true);
    }

    private string Member(MemberAccessExpressionSyntax node, bool suppressImplicitCall)
    {
        var value = $"{Expr(node.Expression)}.{node.Name.Identifier.ValueText}";
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

    private string Identifier(IdentifierNameSyntax node, bool suppressImplicitCall)
    {
        var name = node.Identifier.ValueText switch { "Me" => "this", "MyBase" => "base", var x => x };
        if (suppressImplicitCall) return name;
        var classification = _classifier.ClassifyExpression(node, _model);
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

    private string Arguments(ArgumentListSyntax? list) => list is null ? "" :
        string.Join(", ", list.Arguments.Select(a => a is SimpleArgumentSyntax s ? Expr(s.Expression) : a.ToString()));

    private static string Literal(LiteralExpressionSyntax literal) => literal.Kind() switch
    {
        VBSyntaxKind.NothingLiteralExpression => "null",
        VBSyntaxKind.TrueLiteralExpression => "true",
        VBSyntaxKind.FalseLiteralExpression => "false",
        VBSyntaxKind.StringLiteralExpression => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral((string)literal.Token.Value!, true),
        VBSyntaxKind.CharacterLiteralExpression => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral((char)literal.Token.Value!, true),
        _ => literal.Token.ValueText
    };

    private static string Type(TypeSyntax? type) => type switch
    {
        null => "object",
        PredefinedTypeSyntax p => p.Keyword.Kind() switch
        {
            VBSyntaxKind.StringKeyword => "string", VBSyntaxKind.IntegerKeyword => "int",
            VBSyntaxKind.LongKeyword => "long", VBSyntaxKind.ShortKeyword => "short",
            VBSyntaxKind.BooleanKeyword => "bool", VBSyntaxKind.ObjectKeyword => "object",
            VBSyntaxKind.DecimalKeyword => "decimal", VBSyntaxKind.DoubleKeyword => "double",
            VBSyntaxKind.SingleKeyword => "float", VBSyntaxKind.ByteKeyword => "byte",
            VBSyntaxKind.CharKeyword => "char", VBSyntaxKind.DateKeyword => "DateTime",
            _ => p.Keyword.ValueText
        },
        GenericNameSyntax g => $"{g.Identifier.ValueText}<{string.Join(", ", g.TypeArgumentList.Arguments.Select(Type))}>",
        ArrayTypeSyntax a => Type(a.ElementType) + string.Concat(a.RankSpecifiers.Select(r => "[" + new string(',', r.Rank - 1) + "]")),
        _ => type.ToString().Replace("Global.", "global::", StringComparison.Ordinal)
    };

    private static string Parameter(ParameterSyntax p)
    {
        var modifier = p.Modifiers.Any(VBSyntaxKind.ByRefKeyword) ? "ref " : "";
        return $"{modifier}{Type(p.AsClause?.Type())} {p.Identifier.Identifier.ValueText}";
    }

    private static string Access(SyntaxTokenList modifiers) =>
        modifiers.Any(VBSyntaxKind.PublicKeyword) ? "public " :
        modifiers.Any(VBSyntaxKind.ProtectedKeyword) ? "protected " :
        modifiers.Any(VBSyntaxKind.PrivateKeyword) ? "private " : "internal ";

    private static string AccessText(string modifiers) =>
        modifiers.Contains("Public", StringComparison.OrdinalIgnoreCase) ? "public " :
        modifiers.Contains("Private", StringComparison.OrdinalIgnoreCase) ? "private " : "";

    private static string AssignmentOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.AddAssignmentStatement => "+=", VBSyntaxKind.SubtractAssignmentStatement => "-=",
        VBSyntaxKind.MultiplyAssignmentStatement => "*=", VBSyntaxKind.DivideAssignmentStatement => "/=", _ => "="
    };

    private static string BinaryOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.EqualsExpression => "==", VBSyntaxKind.NotEqualsExpression => "!=",
        VBSyntaxKind.AndAlsoExpression => "&&", VBSyntaxKind.OrElseExpression => "||",
        VBSyntaxKind.AndExpression => "&", VBSyntaxKind.OrExpression => "|",
        VBSyntaxKind.ModuloExpression => "%", VBSyntaxKind.ConcatenateExpression => "+",
        _ => kind switch
        {
            VBSyntaxKind.AddExpression => "+", VBSyntaxKind.SubtractExpression => "-",
            VBSyntaxKind.MultiplyExpression => "*", VBSyntaxKind.DivideExpression => "/",
            VBSyntaxKind.LessThanExpression => "<", VBSyntaxKind.LessThanOrEqualExpression => "<=",
            VBSyntaxKind.GreaterThanExpression => ">", VBSyntaxKind.GreaterThanOrEqualExpression => ">=", _ => "/*?*/"
        }
    };

    private static string UnaryOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.NotExpression => "!", VBSyntaxKind.UnaryMinusExpression => "-", _ => "+"
    };

    private string UnsupportedExpression(ExpressionSyntax node)
    {
        Review(node, ReasonCode.UnsupportedSyntax, $"Unsupported VB expression: {node.Kind()}");
        return $"/* ManualReviewRequired: {OneLine(node.ToString())} */ default";
    }

    private void Record(SyntaxNode node, FixType type, string after, SymbolClassification classification)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        var symbol = classification.Symbol;
        _fixes.Add(new(_project, _file, position.Line + 1, position.Character + 1, type,
            node.ToString(), after, classification.Reason, symbol?.Kind.ToString(),
            symbol?.ContainingType?.ToDisplayString(), symbol?.ContainingAssembly?.Identity.Name));
    }

    private void Review(SyntaxNode node, ReasonCode code, string details)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        _reviews.Add(new(_project, _file, position.Line + 1, position.Character + 1,
            OneLine(node.ToString()), code, details));
    }

    private void WriteLeadingComments(SyntaxNode node, StringBuilder output)
    {
        foreach (var trivia in node.GetLeadingTrivia().Where(t => t.IsKind(VBSyntaxKind.CommentTrivia)))
            Line(output, "//" + trivia.ToString().TrimStart('\''));
    }

    private void Block(StringBuilder output, Action body)
    {
        Line(output, "{"); _indent++; body(); _indent--; Line(output, "}");
    }

    private void Line(StringBuilder output, string value) => output.Append(' ', _indent * 4).AppendLine(value);
    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static bool IsVisualBasicRuntimeMethod(IMethodSymbol method) =>
        method.ContainingAssembly?.Identity.Name is { } assemblyName &&
        (assemblyName.Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase) ||
         assemblyName.Equals("Microsoft.VisualBasic.Core", StringComparison.OrdinalIgnoreCase)) &&
        (method.ContainingNamespace?.ToDisplayString().Equals("Microsoft.VisualBasic", StringComparison.Ordinal) == true ||
         method.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.VisualBasic.", StringComparison.Ordinal) == true);

    private string VisualBasicRuntimeTypeAccess(IMethodSymbol method)
    {
        var fullType = method.ContainingType.ToDisplayString();
        _visualBasicRuntimeTypes.Add(fullType);
        var namespaceName = method.ContainingNamespace.ToDisplayString();
        var directType = namespaceName.Equals("Microsoft.VisualBasic", StringComparison.Ordinal);
        if (directType && !_sourceIdentifiers.Contains(method.ContainingType.Name))
        {
            _needsVisualBasicUsing = true;
            return method.ContainingType.Name;
        }

        if (_runtimeAliases.TryGetValue(fullType, out var existing)) return existing;
        var aliasBase = "VB" + method.ContainingType.Name;
        var alias = aliasBase;
        for (var suffix = 2; _sourceIdentifiers.Contains(alias) || _runtimeAliases.Values.Contains(alias, StringComparer.OrdinalIgnoreCase); suffix++)
            alias = aliasBase + suffix;
        _runtimeAliases.Add(fullType, alias);
        return alias;
    }

    private static bool IsGlobalNamespace(StatementSyntax statement) =>
        statement is NamespaceBlockSyntax block &&
        block.NamespaceStatement.Name.ToString().StartsWith("Global.", StringComparison.OrdinalIgnoreCase);
}

internal static class SyntaxExtensions
{
    public static TypeSyntax? Type(this AsClauseSyntax? clause) => clause switch
    {
        SimpleAsClauseSyntax simple => simple.Type,
        AsNewClauseSyntax created when created.NewExpression is ObjectCreationExpressionSyntax creation => creation.Type,
        _ => null
    };
}
