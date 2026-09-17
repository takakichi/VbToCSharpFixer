using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBSyntaxKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace VbToCSharpFixer;

using static VbToCSharpFixer.CSharpTypeNames;

internal sealed partial class ConversionSession
{
    /// <summary>VB式を種類別にC#式へ変換します。</summary>
    private string Expr(ExpressionSyntax node, bool suppressImplicitCall = false)
    {
        if (!suppressImplicitCall && _model.GetOperation(node) is Microsoft.CodeAnalysis.Operations.IPropertyReferenceOperation propertyReference && UsesPropertyMethods(propertyReference.Property))
            return PropertyRead(propertyReference);
        string result = node switch
        {
            InvocationExpressionSyntax invocation => Invocation(invocation),
            MemberAccessExpressionSyntax member => Member(member, suppressImplicitCall),
            IdentifierNameSyntax id => Identifier(id, suppressImplicitCall),
            MeExpressionSyntax => "this",
            MyBaseExpressionSyntax => "base",
            PredefinedTypeSyntax p => Type(p),
            LiteralExpressionSyntax literal => Literal(literal),
            ParenthesizedExpressionSyntax p => $"({Expr(p.Expression)})",
            BinaryExpressionSyntax b => Binary(b),
            UnaryExpressionSyntax u => Unary(u),
            ArrayCreationExpressionSyntax a => ArrayCreation(a),
            CollectionInitializerSyntax initializer => ArrayLiteral(initializer,
                _model.GetTypeInfo(initializer).ConvertedType as IArrayTypeSymbol ?? _model.GetTypeInfo(initializer).Type as IArrayTypeSymbol),
            ObjectCreationExpressionSyntax o => ObjectCreation(o),
            PredefinedCastExpressionSyntax c => PredefinedCast(c),
            CTypeExpressionSyntax c => CType(c),
            DirectCastExpressionSyntax c => ParenthesizedCast(c.Expression, c.Type),
            TryCastExpressionSyntax c => $"(({Expr(c.Expression)}) as {Type(c.Type)})",
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
            case ExpressionMeaning.Property:
                after = Expr(node.Expression, true);
                fixType = FixType.MethodCall;
                break;
            default:
                Review(node, classification.Meaning == ExpressionMeaning.Ambiguous ? ReasonCode.AmbiguousSymbol : ReasonCode.UnresolvedSymbol, classification.Reason);
                return $"/* ManualReviewRequired */ {node}";
        }
        Record(node, fixType, after, classification);
        return after;
    }

    /// <summary>名前ではなくシンボルで既定プロパティを識別し、C#Indexerの対象式を生成します。</summary>
    private string IndexerTarget(ExpressionSyntax expression, IPropertySymbol? property)
    {
        if (expression is MemberAccessExpressionSyntax member &&
            property is not null && SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(member).Symbol, property))
        {
            if (HasOmittedWithReceiver(member))
                return _withTargets.Count > 0 ? _withTargets.Peek() : UnsupportedExpression(member);
            return Expr(member.Expression, true);
        }
        if (expression is IdentifierNameSyntax && property is not null &&
            SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(expression).Symbol, property)) return "this";
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
        if (_setterValue is not null && SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(node).Symbol, _setterValue))
            return "value";
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
            var refKind = parameter is null ? RefKind.None : EffectiveRefKind(parameter);
            if (parameter is not null && refKind is RefKind.Ref or RefKind.Out)
            {
                if (!IsSafeByReferenceArgument(simple.Expression, parameter, refKind))
                {
                    Review(simple, ReasonCode.UnsupportedSyntax,
                        $"{refKind} argument requires VB copy-in/copy-back or could not be proven safe.");
                    return prefix + $"/* ManualReviewRequired: {refKind} argument */ " + Expr(simple.Expression);
                }
                var modifier = refKind == RefKind.Out ? "out " : "ref ";
                return prefix + modifier + Expr(simple.Expression);
            }
            return prefix + ExprForTarget(simple.Expression, targetType);
        }));
    }

    /// <summary>refまたはout引数が型変換やVB copy-backを伴わない書き換え可能な格納場所か判定します。</summary>
    private bool IsSafeByReferenceArgument(ExpressionSyntax expression, IParameterSymbol parameter, RefKind refKind)
    {
        // VBはPropertyや変換式にも一時変数を介したcopy-in/copy-backを許すが、C#のref/outは
        // 同じ型の書き換え可能な格納場所を要求する。その条件を確認できる引数だけを直接渡す。
        if (expression is ParenthesizedExpressionSyntax) return false;
        var argumentType = _model.GetTypeInfo(expression).Type;
        if (argumentType is null || !SymbolEqualityComparer.Default.Equals(argumentType, parameter.Type)) return false;

        if (expression is InvocationExpressionSyntax invocation)
            return _classifier.ClassifyInvocation(invocation, _model).Meaning == ExpressionMeaning.Array;

        if (expression is not IdentifierNameSyntax && expression is not MemberAccessExpressionSyntax) return false;
        return _model.GetSymbolInfo(expression).Symbol switch
        {
            IParameterSymbol => true,
            IFieldSymbol { IsConst: false, IsReadOnly: false } => true,
            ILocalSymbol when refKind == RefKind.Out => true,
            ILocalSymbol local => HasExplicitLocalInitialization(local),
            _ => false
        };
    }

    /// <summary>refへ渡すローカル変数がC#でも宣言時に明示初期化されることを確認します。</summary>
    private static bool HasExplicitLocalInitialization(ILocalSymbol local)
    {
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            var declarator = syntax.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
            if (declarator?.Initializer is not null || declarator?.AsClause is AsNewClauseSyntax) return true;
            if (syntax.AncestorsAndSelf().Any(x => x is ForStatementSyntax or ForEachStatementSyntax or CatchStatementSyntax or UsingStatementSyntax))
                return true;
        }
        return false;
    }

    /// <summary>Object生成式のコンストラクター引数にも必要なEnum変換を適用します。</summary>
    private string ObjectCreation(ObjectCreationExpressionSyntax expression)
    {
        var constructor = _model.GetSymbolInfo(expression).Symbol as IMethodSymbol;
        return $"new {Type(expression.Type)}({Arguments(expression.ArgumentList, constructor?.Parameters ?? default)})";
    }

    /// <summary>VB配列生成式の要素型、Rank、上限値および初期化子をC#配列生成式へ変換します。</summary>
    private string ArrayCreation(ArrayCreationExpressionSyntax expression)
    {
        if (_model.GetTypeInfo(expression).Type is not IArrayTypeSymbol array ||
            CSharpTypeName(array.ElementType) is not { } elementType)
            return UnsupportedExpression(expression);

        if (expression.Initializer is not null)
        {
            var rank = "[" + new string(',', array.Rank - 1) + "]";
            return $"new {elementType}{rank} {CollectionInitializer(expression.Initializer)}";
        }

        var bounds = expression.ArrayBounds?.Arguments.OfType<SimpleArgumentSyntax>().ToArray() ?? [];
        if (bounds.Length == 0 || bounds.Length != array.Rank)
        {
            Review(expression, ReasonCode.UnsupportedSyntax, "Array creation bounds could not be converted safely");
            return $"/* ManualReviewRequired */ {expression}";
        }
        return $"new {elementType}[{string.Join(", ", bounds.Select(x => $"({Expr(x.Expression)}) + 1"))}]";
    }

    /// <summary>VB配列・コレクション初期化子を入れ子構造と要素式を保ってC#初期化子へ変換します。</summary>
    private string CollectionInitializer(CollectionInitializerSyntax initializer) =>
        "{ " + string.Join(", ", initializer.Initializers.Select(x =>
            x is CollectionInitializerSyntax nested ? CollectionInitializer(nested) : Expr(x))) + " }";

    /// <summary>CTypeを意味解析し、VB組み込み型はConversions、参照型等は括弧付きC#キャストへ変換します。</summary>
    private string CType(CTypeExpressionSyntax expression)
    {
        var targetType = _model.GetTypeInfo(expression).Type ?? _model.GetTypeInfo(expression.Type).Type;
        var sourceType = _model.GetTypeInfo(expression.Expression).Type;
        if (targetType is null || sourceType is null)
        {
            Review(expression, ReasonCode.UnresolvedSymbol, "CType source or target type could not be resolved.");
            return $"/* ManualReviewRequired */ {expression}";
        }

        var conversion = _model.ClassifyConversion(expression.Expression, targetType);
        if (!conversion.Exists)
        {
            Review(expression, ReasonCode.UnsupportedSyntax, "CType conversion could not be classified safely.");
            return $"/* ManualReviewRequired */ {expression}";
        }

        if (conversion.IsIdentity) return $"({Expr(expression.Expression)})";
        if (conversion.IsUserDefined || targetType.TypeKind == TypeKind.Enum ||
            targetType.SpecialType == SpecialType.System_Object ||
            VisualBasicConversionMethod(targetType) is null)
            return ParenthesizedCast(expression.Expression, expression.Type);

        return VisualBasicConversion(expression, expression.Expression, targetType,
            VisualBasicConversionMethod(targetType)!);
    }

    /// <summary>DirectCast等のC#明示キャストを後続メンバーアクセスにも安全な括弧付き式で生成します。</summary>
    private string ParenthesizedCast(ExpressionSyntax expression, TypeSyntax targetType) =>
        $"(({Type(targetType)})({Expr(expression)}))";

    /// <summary>変換先組み込み型に対応するMicrosoft.VisualBasic Conversionsメソッド名を返します。</summary>
    private static string? VisualBasicConversionMethod(ITypeSymbol targetType) => targetType.SpecialType switch
    {
        SpecialType.System_Boolean => "ToBoolean", SpecialType.System_Byte => "ToByte",
        SpecialType.System_SByte => "ToSByte", SpecialType.System_Int16 => "ToShort",
        SpecialType.System_UInt16 => "ToUShort", SpecialType.System_Int32 => "ToInteger",
        SpecialType.System_UInt32 => "ToUInteger", SpecialType.System_Int64 => "ToLong",
        SpecialType.System_UInt64 => "ToULong", SpecialType.System_Single => "ToSingle",
        SpecialType.System_Double => "ToDouble", SpecialType.System_Decimal => "ToDecimal",
        SpecialType.System_Char => "ToChar", SpecialType.System_DateTime => "ToDate",
        SpecialType.System_String => "ToString",
        _ => null
    };

    /// <summary>CTypeのVB互換変換をMicrosoft.VisualBasic.CompilerServices.Conversions呼び出しとして生成します。</summary>
    private string VisualBasicConversion(CTypeExpressionSyntax origin, ExpressionSyntax expression,
        ITypeSymbol targetType, string method)
    {
        var conversions = _model.Compilation.GetTypeByMetadataName("Microsoft.VisualBasic.CompilerServices.Conversions");
        if (conversions is null)
        {
            Review(origin, ReasonCode.MissingReference, "Microsoft.VisualBasic.CompilerServices.Conversions could not be resolved.");
            return $"/* ManualReviewRequired */ {origin}";
        }
        var value = expression.IsKind(VBSyntaxKind.NothingLiteralExpression) ? "(object)null" : Expr(expression);
        var after = $"{VisualBasicRuntimeTypeAccess(conversions)}.{method}({value})";
        Record(origin, FixType.VbRuntimeCall, after,
            new SymbolClassification(ExpressionMeaning.Value, conversions, targetType,
                $"VB CType conversion mapped to Conversions.{method}"));
        return after;
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
        if (expression is CollectionInitializerSyntax initializer && targetType is IArrayTypeSymbol array)
            return ArrayLiteral(initializer, array);
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

    /// <summary>NewのないVB配列初期化子も、代入先の要素型と次元数を持つ生成式にします。</summary>
    private string ArrayLiteral(CollectionInitializerSyntax initializer, IArrayTypeSymbol? array)
    {
        if (array is null || CSharpTypeName(array) is not { } type) return UnsupportedExpression(initializer);
        return $"new {type} {ArrayLiteralElements(initializer, array, 1)}";
    }

    private string ArrayLiteralElements(CollectionInitializerSyntax initializer, IArrayTypeSymbol array, int dimension) =>
        "{ " + string.Join(", ", initializer.Initializers.Select(element =>
            dimension < array.Rank && element is CollectionInitializerSyntax nested
                ? ArrayLiteralElements(nested, array, dimension + 1)
                : ExprForTarget(element, array.ElementType))) + " }";

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

    /// <summary>VB代入ステートメント種別をC#代入演算子へ対応付けます。</summary>
    private static string AssignmentOperator(VBSyntaxKind kind) => kind switch
    {
        VBSyntaxKind.AddAssignmentStatement => "+=", VBSyntaxKind.SubtractAssignmentStatement => "-=",
        VBSyntaxKind.MultiplyAssignmentStatement => "*=", VBSyntaxKind.DivideAssignmentStatement => "/=",
        VBSyntaxKind.ConcatenateAssignmentStatement => "+=", VBSyntaxKind.LeftShiftAssignmentStatement => "<<=",
        VBSyntaxKind.RightShiftAssignmentStatement => ">>=", _ => "="
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
}
