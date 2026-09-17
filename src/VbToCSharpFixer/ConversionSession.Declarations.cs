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
        Line(output, MethodSignature(method.SubOrFunctionStatement) + TrailingComment(method.SubOrFunctionStatement));
        WithLabelScope(method.Statements, () =>
            Block(output, () =>
            {
                foreach (var s in method.Statements) WriteStatement(s, output);
                WriteLeadingComments(method.EndSubOrFunctionStatement, output);
            }, TrailingComment(method.EndSubOrFunctionStatement)));
    }

    /// <summary>VBのSub NewをinstanceまたはSharedのC#コンストラクターへ変換します。</summary>
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
                else if (targetType is IArrayTypeSymbol array && TryArrayBounds(name, array, out var allocation))
                    init = " = " + allocation;
                else
                    init = "";
                Line(output, $"{prefix}{type} {EscapeIdentifier(name.Identifier.ValueText)}{init};".TrimStart());
            }
        }
    }

    /// <summary>変数名側に指定されたVB配列上限をC#の配列長へ変換します。</summary>
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
}
