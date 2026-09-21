using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace VbToCSharpFixer;

using static VbToCSharpFixer.CSharpTypeNames;

internal sealed partial class ConversionSession
{
    /// <summary>クラス、構造体、InterfaceまたはModuleの宣言とメンバーを出力します。</summary>
    /// <param name="type">処理対象の型または型構文。</param>
    /// <param name="members">出力する型メンバーの一覧。</param>
    /// <param name="keyword">生成するC#型キーワード。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    private void WriteType(TypeStatementSyntax type, SyntaxList<StatementSyntax> members, string keyword, StringBuilder output)
    {
        var access = Access(type.Modifiers);
        var symbol = _model.GetDeclaredSymbol(type) as INamedTypeSymbol;
        // VBは分割宣言の一方だけにPartialを指定できるが、C#では全てに必要。
        var partial = type.Modifiers.Any(VBSyntaxKind.PartialKeyword) || symbol?.DeclaringSyntaxReferences.Length > 1
            ? "partial " : "";
        if (partial.Length > 0 && symbol is not null) access = AccessibilityText(symbol.DeclaredAccessibility);
        var typeKeyword = keyword == "static class" ? $"static {partial}class" : partial + keyword;
        var inheritance = type.Parent switch
        {
            TypeBlockSyntax block when block.Inherits.Count > 0 => " : " + string.Join(", ", block.Inherits.SelectMany(x => x.Types).Select(Type)),
            _ => ""
        };
        Line(output, $"{access}{typeKeyword} {type.Identifier.ValueText}{inheritance}".TrimStart());
        Block(output, () => { foreach (var m in members) WriteStatement(m, output); });
    }

    /// <summary>VBのEnum宣言、基底型、属性および各列挙値をC#として出力します。</summary>
    /// <param name="block">変換対象のVBブロック。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    private void WriteEnum(EnumBlockSyntax block, StringBuilder output)
    {
        var statement = block.EnumStatement;
        WriteAttributes(statement.AttributeLists, output);
        var symbol = _model.GetDeclaredSymbol(statement) as INamedTypeSymbol;
        var name = EscapeIdentifier(symbol?.Name ?? statement.Identifier.ValueText);
        var underlyingType = statement.UnderlyingType is null ? "" : " : " + Type(statement.UnderlyingType.Type());
        Line(output, $"{Access(statement.Modifiers)}enum {name}{underlyingType}".TrimStart());
        Line(output, "{");
        _writer.Indent++;
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
        _writer.Indent--;
        Line(output, "}");
    }

    /// <summary>VB属性リストをC#属性として出力します。</summary>
    /// <param name="lists">出力する属性リスト。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    private void WriteAttributes(SyntaxList<AttributeListSyntax> lists, StringBuilder output)
    {
        foreach (var list in lists)
            Line(output, "[" + string.Join(", ", list.Attributes.Select(Attribute)) + "]");
    }

    /// <summary>VB属性の名前、位置引数および名前付き引数をC#表現へ変換します。</summary>
    /// <param name="attribute">変換対象のVB属性。</param>
    /// <returns>生成または変換した文字列。</returns>
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
    /// <param name="method">処理対象のメソッド。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    private void WriteMethod(MethodBlockSyntax method, StringBuilder output)
    {
        var symbol = _model.GetDeclaredSymbol(method.SubOrFunctionStatement) as IMethodSymbol;
        // VBではFunction名が暗黙の戻り値変数としても解決される。文字列で名前を比較すると
        // 同名のメンバーや再帰呼び出しを誤判定するため、RoslynのIsFunctionValueで識別する。
        _functionValue = method.DescendantNodes().OfType<AssignmentStatementSyntax>()
            .Select(a => _model.GetSymbolInfo(a.Left).Symbol).OfType<ILocalSymbol>()
            .FirstOrDefault(local => local.IsFunctionValue && SymbolEqualityComparer.Default.Equals(local.ContainingSymbol, symbol));
        _functionValueName = _functionValue is null ? null : CreateUniqueTemporaryName("__returnValue");
        // ReturnやExit FunctionはFinally実行後の戻り値更新を反映できるよう、共通の終了地点へ集約する。
        _functionExitLabel = _functionValue is not null && method.DescendantNodes().Any(node =>
            (node is ReturnStatementSyntax || node is ExitStatementSyntax exit && exit.BlockKeyword.IsKind(VBSyntaxKind.FunctionKeyword)) &&
            SymbolEqualityComparer.Default.Equals(_model.GetEnclosingSymbol(node.SpanStart), symbol))
            ? CreateUniqueTemporaryName("__functionEnd") : null;
        Line(output, MethodSignature(method.SubOrFunctionStatement) + TrailingComment(method.SubOrFunctionStatement));
        WithLabelScope(method.Statements, () =>
            Block(output, () =>
            {
                if (_functionValueName is not null)
                    Line(output, $"{CSharpTypeName(symbol!.ReturnType)} {_functionValueName} = default({CSharpTypeName(symbol.ReturnType)});");
                foreach (var s in method.Statements) WriteStatement(s, output);
                WriteLeadingComments(method.EndSubOrFunctionStatement, output);
                if (_functionExitLabel is not null) Line(output, _functionExitLabel + ":;");
                if (_functionValueName is not null) Line(output, $"return {_functionValueName};");
            }, TrailingComment(method.EndSubOrFunctionStatement)));
        _functionValue = null;
        _functionValueName = null;
        _functionExitLabel = null;
    }

    /// <summary>VBのSub NewをinstanceまたはSharedのC#コンストラクターへ変換します。</summary>
    /// <param name="constructor">変換対象のVBコンストラクター。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    private void WriteConstructor(ConstructorBlockSyntax constructor, StringBuilder output)
    {
        var statement = constructor.SubNewStatement;
        WriteAttributes(statement.AttributeLists, output);
        var symbol = _model.GetDeclaredSymbol(statement) as IMethodSymbol;
        var typeName = EscapeIdentifier(symbol?.ContainingType.Name ??
            statement.Ancestors().OfType<TypeStatementSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "Constructor");
        var isShared = symbol?.IsStatic == true || statement.Modifiers.Any(VBSyntaxKind.SharedKeyword);
        var signature = isShared ? $"static {typeName}()" :
            $"{Access(statement.Modifiers)}{typeName}({string.Join(", ", statement.ParameterList?.Parameters.Select(Parameter) ?? [])})".TrimStart();
        var skip = 0;
        if (!isShared && constructor.Statements.Count > 0 && TryConstructorInitializer(constructor.Statements[0], out var initializer))
        {
            signature += " : " + initializer;
            skip = 1;
        }
        var statements = constructor.Statements.Skip(skip).ToArray();
        Line(output, signature + TrailingComment(statement));
        WithLabelScope(statements, () =>
            Block(output, () =>
            {
                foreach (var child in statements) WriteStatement(child, output);
                WriteLeadingComments(constructor.EndSubStatement, output);
            }, TrailingComment(constructor.EndSubStatement)));
    }

    /// <summary>コンストラクター先頭のMyBase.NewまたはMe.NewをC# initializerへ変換します。</summary>
    /// <param name="statement">変換または判定の対象となるVBステートメント。</param>
    /// <param name="initializer">変換対象の初期化子。</param>
    /// <returns>初期化子を安全に変換できた場合はtrue、それ以外はfalse。</returns>
    private bool TryConstructorInitializer(StatementSyntax statement, out string initializer)
    {
        initializer = "";
        var invocation = statement switch
        {
            ExpressionStatementSyntax { Expression: InvocationExpressionSyntax value } => value,
            CallStatementSyntax { Invocation: InvocationExpressionSyntax value } => value,
            _ => null
        };
        if (invocation?.Expression is not MemberAccessExpressionSyntax member ||
            !member.Name.Identifier.ValueText.Equals("New", StringComparison.OrdinalIgnoreCase)) return false;
        var target = member.Expression switch
        {
            MyBaseExpressionSyntax => "base",
            MeExpressionSyntax => "this",
            _ => null
        };
        if (target is null) return false;
        var constructor = _model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        initializer = $"{target}({Arguments(invocation.ArgumentList, constructor?.Parameters ?? default)})";
        return true;
    }

    /// <summary>VBメソッド宣言からC#のメソッドシグネチャを生成します。</summary>
    /// <param name="method">処理対象のメソッド。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string MethodSignature(MethodStatementSyntax method)
    {
        var access = Access(method.Modifiers);
        var symbol = _model.GetDeclaredSymbol(method) as IMethodSymbol;
        var shared = symbol?.IsStatic == true || method.Modifiers.Any(VBSyntaxKind.SharedKeyword) ? "static " : "";
        var returnType = method.Kind() == VBSyntaxKind.SubStatement ? "void" : Type(method.AsClause?.Type());
        var parameters = string.Join(", ", method.ParameterList?.Parameters.Select(Parameter) ?? []);
        return $"{access}{shared}{returnType} {method.Identifier.ValueText}({parameters})".TrimStart();
    }

    /// <summary>VBプロパティとアクセサーブロックをC#として出力します。</summary>
    /// <param name="property">処理対象のプロパティ。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    private void WriteProperty(PropertyBlockSyntax property, StringBuilder output)
    {
        if (_model.GetDeclaredSymbol(property.PropertyStatement) is IPropertySymbol named && UsesPropertyMethods(named))
        {
            WritePropertyMethods(property.PropertyStatement, property.Accessors, named, output);
            return;
        }
        Line(output, PropertySignature(property.PropertyStatement));
        Block(output, () =>
        {
            foreach (var accessor in property.Accessors)
            {
                var symbol = _model.GetDeclaredSymbol(accessor.AccessorStatement) as IMethodSymbol;
                var propertySymbol = _model.GetDeclaredSymbol(property.PropertyStatement) as IPropertySymbol;
                var access = symbol is not null && propertySymbol is not null && symbol.DeclaredAccessibility != propertySymbol.DeclaredAccessibility
                    ? AccessibilityText(symbol.DeclaredAccessibility) : "";
                var isGet = accessor.Kind() == VBSyntaxKind.GetAccessorBlock;
                Line(output, access + (isGet ? "get" : "set"));
                var previousValue = _setterValue;
                // VBのSet引数名は任意だが、C#では暗黙のvalueとなる。文字列置換ではなくシンボルで対応させる。
                _setterValue = isGet ? null : symbol?.Parameters.LastOrDefault();
                try
                {
                    WithLabelScope(accessor.Statements, () =>
                        Block(output, () => { foreach (var s in accessor.Statements) WriteStatement(s, output); }));
                }
                finally { _setterValue = previousValue; }
            }
        });
    }

    /// <summary>通常プロパティまたはIndexerのC#シグネチャを生成します。</summary>
    /// <param name="property">処理対象のプロパティ。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string PropertySignature(PropertyStatementSyntax property)
    {
        var symbol = _model.GetDeclaredSymbol(property) as IPropertySymbol;
        // VBの省略時アクセスは宣言の種類で異なる。Propertyは構文上の修飾子だけでinternalにしない。
        var access = symbol is null ? Access(property.Modifiers) : AccessibilityText(symbol.DeclaredAccessibility);
        var shared = symbol?.IsStatic == true || property.Modifiers.Any(VBSyntaxKind.SharedKeyword) ? "static " : "";
        var type = Type(property.AsClause?.Type());
        var parameters = property.ParameterList?.Parameters ?? default;
        if (symbol?.IsIndexer == true)
            return $"{access}{shared}{type} this[{string.Join(", ", parameters.Select(Parameter))}]".TrimStart();
        return $"{access}{shared}{type} {EscapeIdentifier(property.Identifier.ValueText)}".TrimStart();
    }

    private static string AccessibilityText(Accessibility access) => access switch
    {
        Accessibility.Public => "public ",
        Accessibility.Private => "private ",
        Accessibility.Protected => "protected ",
        Accessibility.Internal => "internal ",
        Accessibility.ProtectedOrInternal => "protected internal ",
        Accessibility.ProtectedAndInternal => "private protected ",
        _ => ""
    };

    /// <summary>フィールドまたはローカル変数の宣言をC#として出力します。</summary>
    /// <param name="modifierText">VB宣言の修飾子文字列。</param>
    /// <param name="declarators">変換対象の変数宣言子一覧。</param>
    /// <param name="output">生成したC#コードの出力先。</param>
    /// <param name="field">フィールド宣言として処理する場合はtrue。</param>
    private void WriteDeclaration(string modifierText, SeparatedSyntaxList<VariableDeclaratorSyntax> declarators, StringBuilder output, bool field)
    {
        foreach (var d in declarators)
        {
            foreach (var name in d.Names)
            {
                var declared = _model.GetDeclaredSymbol(name);
                var targetType = declared switch
                {
                    ILocalSymbol local => local.Type,
                    IFieldSymbol declaredField => declaredField.Type,
                    _ => null
                };
                var type = targetType is IArrayTypeSymbol && CSharpTypeName(targetType) is { } arrayType
                    ? arrayType : Type(d.AsClause?.Type());
                if (!field && d.AsClause is null && d.Initializer is not null && targetType is not IArrayTypeSymbol) type = "var";
                var shared = declared is IFieldSymbol { IsStatic: true } ? "static " : "";
                var prefix = field ? AccessText(modifierText) + shared : "";
                string init;
                if (d.Initializer is not null)
                    init = " = " + ExprForTarget(d.Initializer.Value, targetType);
                // As Newの生成式はInitializerではなくAsClauseにある。
                // 複数変数の宣言でも、各変数に独立したインスタンスを生成する。
                else if (d.AsClause is AsNewClauseSyntax asNew)
                    init = " = " + ExprForTarget(asNew.NewExpression, targetType);
                else if (targetType is IArrayTypeSymbol array && TryArrayBounds(name, array, out var allocation))
                    init = " = " + allocation;
                else
                    init = "";
                Line(output, $"{prefix}{type} {EscapeIdentifier(name.Identifier.ValueText)}{init};".TrimStart());
            }
        }
    }

    /// <summary>変数名側に指定されたVB配列上限をC#の配列長へ変換します。</summary>
    /// <param name="name">処理対象の名前。</param>
    /// <param name="array">変換先または解析対象の配列型。</param>
    /// <param name="allocation">生成した配列生成式の出力先。</param>
    /// <returns>配列上限を安全に変換できた場合はtrue、それ以外はfalse。</returns>
    private bool TryArrayBounds(ModifiedIdentifierSyntax name, IArrayTypeSymbol array, out string allocation)
    {
        allocation = "";
        if (name.ArrayBounds is null || name.ArrayBounds.Arguments.Count == 0) return false;
        var bounds = name.ArrayBounds.Arguments.OfType<SimpleArgumentSyntax>().ToArray();
        var elementType = CSharpTypeName(array.ElementType);
        if (bounds.Length != name.ArrayBounds.Arguments.Count || bounds.Length != array.Rank || elementType is null)
        {
            Review(name, ReasonCode.UnsupportedSyntax, "Array bounds could not be converted safely");
            return false;
        }
        allocation = $"new {elementType}[{string.Join(", ", bounds.Select(x => $"({Expr(x.Expression)}) + 1"))}]";
        return true;
    }

    /// <summary>VB型構文をC#の組み込み型、Genericまたは配列型表現へ変換します。</summary>
    /// <param name="type">処理対象の型または型構文。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string Type(TypeSyntax? type)
    {
        if (type is not null)
        {
            var resolvedType = _model.GetTypeInfo(type).Type ?? _model.GetSymbolInfo(type).Symbol as ITypeSymbol;
            if (resolvedType is INamedTypeSymbol named && NeedsQualifiedType(named, type.SpanStart))
                return CSharpTypeName(named) ?? type.ToString();
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
    /// <param name="p">変換対象のVBパラメーター。</param>
    /// <returns>生成または変換した文字列。</returns>
    private string Parameter(ParameterSyntax p)
    {
        var modifier = p.Modifiers.Any(VBSyntaxKind.ByRefKeyword) ? "ref " : "";
        var symbol = _model.GetDeclaredSymbol(p) as IParameterSymbol;
        // Optionalの既定値は通常メソッド・コンストラクター・プロパティで共通に保持する。
        var defaultValue = p.Default is null ? "" : " = " +
            (symbol is { HasExplicitDefaultValue: true } ? ParameterDefaultValue(symbol) : Expr(p.Default.Value));
        return $"{modifier}{Type(p.AsClause?.Type())} {EscapeIdentifier(p.Identifier.Identifier.ValueText)}{defaultValue}";
    }

    private static string ParameterDefaultValue(IParameterSymbol parameter)
    {
        var value = parameter.ExplicitDefaultValue;
        if (value is null) return $"default({CSharpTypeName(parameter.Type)})";
        var literal = ConstantLiteral(value);
        return parameter.Type.TypeKind == TypeKind.Enum ? $"({CSharpTypeName(parameter.Type)}){literal}" : literal;
    }

    private static string ConstantLiteral(object value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatPrimitive(value, true, false) +
        (value switch { decimal => "m", float => "f", double => "d", uint => "U", long => "L", ulong => "UL", _ => "" });

    /// <summary>VBアクセス修飾子からC#アクセス修飾子を生成します。</summary>
    /// <param name="modifiers">VB宣言に指定された修飾子。</param>
    /// <returns>生成または変換した文字列。</returns>
    private static string Access(SyntaxTokenList modifiers) =>
        modifiers.Any(VBSyntaxKind.PublicKeyword) ? "public " :
        modifiers.Any(VBSyntaxKind.ProtectedKeyword) ? "protected " :
        modifiers.Any(VBSyntaxKind.PrivateKeyword) ? "private " : "internal ";

    /// <summary>文字列化されたVB修飾子からフィールド用C#アクセス修飾子を生成します。</summary>
    /// <param name="modifiers">VB宣言に指定された修飾子。</param>
    /// <returns>生成または変換した文字列。</returns>
    private static string AccessText(string modifiers) =>
        modifiers.Contains("Public", StringComparison.OrdinalIgnoreCase) ? "public " :
        modifiers.Contains("Private", StringComparison.OrdinalIgnoreCase) ? "private " : "";
}
