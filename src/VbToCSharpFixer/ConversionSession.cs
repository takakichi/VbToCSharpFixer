using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace VbToCSharpFixer;

using static VbToCSharpFixer.CSharpTypeNames;

/// <summary>単一ソースの変換状態。構文別の実装は同名のpartialファイルにまとめます。</summary>
internal sealed partial class ConversionSession
{
    private readonly SymbolClassifier _classifier = new();
    private readonly List<FixResult> _fixes = [];
    private readonly List<ManualReviewItem> _reviews = [];
    private readonly HashSet<string> _visualBasicRuntimeTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _runtimeAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _withTargets = new();
    private readonly Stack<string> _selectEndLabels = new();
    private readonly Stack<IReadOnlyDictionary<string, string>> _labelMaps = new();
    private readonly Stack<LoopScope> _loops = new();
    private IParameterSymbol? _setterValue;
    private ILocalSymbol? _functionValue;
    private string? _functionValueName;
    private string? _functionExitLabel;
    private bool _needsVisualBasicUsing;
    private readonly SemanticModel _model;
    private readonly string _project;
    private readonly string _file;
    private readonly CSharpCodeWriter _writer = new();

    private readonly SyntaxTree _tree;
    private readonly ReferenceKindResolver _referenceKinds;

    // 変換ごとに生成するため、前のファイルのalias・ラベル・レビュー項目を持ち越さない。
    /// <summary>1ファイル分の構文木、意味情報および変換状態を初期化します。</summary>
    /// <param name="tree">変換対象のVB構文木。</param>
    /// <param name="model">構文木に対応する意味モデル。</param>
    /// <param name="projectName">ログに記録するプロジェクト名。</param>
    /// <param name="referenceKinds">ByRefとoutの違いを復元する判定サービス。</param>
    internal ConversionSession(SyntaxTree tree, SemanticModel model, string projectName, ReferenceKindResolver referenceKinds)
    {
        _tree = tree;
        _model = model;
        _project = projectName;
        _file = tree.FilePath;
        _referenceKinds = referenceKinds;
        foreach (var token in tree.GetRoot().DescendantTokens().Where(x => x.IsKind(VBSyntaxKind.IdentifierToken)))
            _sourceIdentifiers.Add(token.ValueText);
    }

    internal string ConvertExpression(ExpressionSyntax expression) => Expr(expression);

    private RefKind EffectiveRefKind(IParameterSymbol parameter) => _referenceKinds.Resolve(parameter, _model.Compilation);

    /// <summary>VB SyntaxTree全体を意味解析結果に基づいてC#ソースへ変換します。</summary>
    /// <param name="rootNamespace">生成コードに適用するルート名前空間。</param>
    /// <returns>生成コード、変換記録および手動確認項目を含む変換結果。</returns>
    internal ConversionResult Convert(string? rootNamespace)
    {
        var root = (CompilationUnitSyntax)_tree.GetRoot();
        var body = new StringBuilder();
        // VBのRootNamespaceはGlobalで始まる名前空間には適用されないため、
        // 通常メンバーとGlobalメンバーを分けてからそれぞれ出力する。
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

        // 本体変換中に必要なVBランタイム型と衝突回避aliasが確定する。
        // そのためusing群は本体を変換した後に組み立て、最終結果では先頭へ配置する。
        var output = new StringBuilder();
        var imports = ImportClauses().Select(ImportText).ToList();
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

    /// <summary>VBステートメントの種類に応じたC#構文を出力します。</summary>
    /// <param name="statement">変換または判定の対象となるVBステートメント。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
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
            case ConstructorBlockSyntax c:
                WriteConstructor(c, output);
                break;
            case MethodStatementSyntax declaration:
                Line(output, MethodSignature(declaration) + ";" + TrailingComment(declaration));
                break;
            case PropertyBlockSyntax p:
                WriteProperty(p, output);
                break;
            case PropertyStatementSyntax p:
                if (_model.GetDeclaredSymbol(p) is IPropertySymbol named && UsesPropertyMethods(named))
                    WritePropertyMethods(p, default, named, output);
                else
                    Line(output, PropertySignature(p) + (_model.GetDeclaredSymbol(p) is IPropertySymbol { IsReadOnly: true } ? " { get; }" : " { get; set; }"));
                break;
            case FieldDeclarationSyntax f:
                WriteDeclaration(f.Modifiers.ToString(), f.Declarators, output, true);
                break;
            case LocalDeclarationStatementSyntax l:
                WriteDeclaration("", l.Declarators, output, false);
                break;
            case AssignmentStatementSyntax a:
                if (TryWritePropertyAssignment(a, output)) break;
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
                if (_functionValue is not null && SymbolEqualityComparer.Default.Equals(method, _functionValue.ContainingSymbol))
                {
                    // VBのReturnも関数戻り値変数を設定する。Finallyからの更新を返却前に反映する。
                    if (r.Expression is not null)
                        Line(output, $"{_functionValueName} = {ExprForTarget(r.Expression, method?.ReturnType)};");
                    Line(output, $"goto {_functionExitLabel};");
                    break;
                }
                Line(output, r.Expression is null ? "return;" : $"return {ExprForTarget(r.Expression, method?.ReturnType)};");
                break;
            case ThrowStatementSyntax t:
                Line(output, t.Expression is null ? "throw;" : $"throw {Expr(t.Expression)};");
                break;
            case TryBlockSyntax t:
                WriteTryBlock(t, output);
                break;
            case UsingBlockSyntax u:
                WriteUsingBlock(u, output);
                break;
            case ForBlockSyntax f:
                WithLoopScope(VBSyntaxKind.ForKeyword, () => WriteForBlock(f, output), output);
                break;
            case ForEachBlockSyntax f:
                WithLoopScope(VBSyntaxKind.ForKeyword, () => WriteForEachBlock(f, output), output);
                break;
            case WhileBlockSyntax w:
                WithLoopScope(VBSyntaxKind.WhileKeyword, () => WriteWhileBlock(w, output), output);
                break;
            case WithBlockSyntax w:
                WriteWithBlock(w, output);
                break;
            case SingleLineIfStatementSyntax i:
                WriteSingleLineIf(i, output);
                break;
            case SelectBlockSyntax s:
                WriteSelectBlock(s, output);
                break;
            case LabelStatementSyntax l:
                WriteLabel(l, output);
                break;
            case GoToStatementSyntax g:
                WriteGoTo(g, output);
                break;
            case ContinueStatementSyntax c when c.BlockKeyword.Kind() is VBSyntaxKind.ForKeyword or VBSyntaxKind.WhileKeyword:
                WriteLoopTransfer(c, c.BlockKeyword.Kind(), true, output);
                break;
            case ExitStatementSyntax e when e.BlockKeyword.Kind() is VBSyntaxKind.ForKeyword or VBSyntaxKind.WhileKeyword:
                WriteLoopTransfer(e, e.BlockKeyword.Kind(), false, output);
                break;
            case ExitStatementSyntax e when e.BlockKeyword.IsKind(VBSyntaxKind.SelectKeyword) && _selectEndLabels.Count > 0:
                Line(output, $"goto {_selectEndLabels.Peek()};");
                break;
            case ExitStatementSyntax e when e.BlockKeyword.IsKind(VBSyntaxKind.SubKeyword) &&
                                                 _model.GetEnclosingSymbol(e.SpanStart) is IMethodSymbol { ReturnsVoid: true }:
                Line(output, "return;");
                break;
            case ExitStatementSyntax e when e.BlockKeyword.IsKind(VBSyntaxKind.FunctionKeyword) &&
                _model.GetEnclosingSymbol(e.SpanStart) is IMethodSymbol { ReturnsVoid: false } exitMethod:
                Line(output, _functionExitLabel is not null ? $"goto {_functionExitLabel};" :
                    $"return default({CSharpTypeName(exitMethod.ReturnType)});");
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

    /// <summary>ソース識別子および既に生成した名前と衝突しない一時変数名を作成します。</summary>
    /// <param name="baseName">一時識別子の基になる名前。</param>
    /// <returns>既存の識別子と衝突しない一時変数名。</returns>
    private string CreateUniqueTemporaryName(string baseName)
    {
        var candidate = baseName;
        for (var suffix = 2; _sourceIdentifiers.Contains(candidate); suffix++) candidate = baseName + suffix;
        _sourceIdentifiers.Add(candidate);
        return candidate;
    }

    /// <summary>VBのGlobal名前空間宣言でありRootNamespaceを適用しないメンバーか判定します。</summary>
    /// <param name="statement">変換または判定の対象となるVBステートメント。</param>
    /// <returns>条件を満たす場合はtrue、それ以外はfalse。</returns>
    private static bool IsGlobalNamespace(StatementSyntax statement) =>
        statement is NamespaceBlockSyntax block &&
        block.NamespaceStatement.Name.ToString().StartsWith("Global.", StringComparison.OrdinalIgnoreCase);
    private void Block(StringBuilder output, Action body, string closingSuffix = "") => _writer.Block(output, body, closingSuffix);
    private void Line(StringBuilder output, string value) => _writer.Line(output, value);
}
