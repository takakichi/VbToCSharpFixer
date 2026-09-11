# FUNCTION LIST

## 1. Program

| メソッド | 機能 |
|---|---|
| `Main` | CLI解析からログ出力まで処理全体を実行する |
| `IsGeneratedBuildDocument` | `obj`配下のMSBuild生成ドキュメントを除外する |

## 2. WorkspaceLoader

| メソッド | 機能 |
|---|---|
| `LoadAsync` | Solution、Project、FolderまたはFileからVB Compilationを構築する |
| `ReferencedProjectClosure` | ProjectReferenceを再帰的に収集する |
| `RegisterMsBuild` | Visual Studioまたは.NET SDKのMSBuildを登録する |
| `ParseSdkVersion` | SDKディレクトリ名をVersionへ変換する |
| `CompileVisualBasicProjects` | VBプロジェクトのCompilationを生成する |
| `PlatformReferences` | Adhoc解析用の参照アセンブリを列挙する |
| `Dispose` | MSBuildWorkspaceを破棄する |

## 3. SymbolClassifier

| メソッド | 機能 |
|---|---|
| `ClassifyInvocation` | 呼び出し式をMethod、Array、Indexerなどへ分類する |
| `ClassifyExpression` | 一般式をMethod、Property、Valueなどへ分類する |
| `ClassifyProperty` | IPropertySymbolを通常PropertyまたはIndexerへ分類する |

## 4. VbToCSharpConverter

| メソッド | 機能 |
|---|---|
| `Convert` | VB SyntaxTree全体をC#ソースへ変換する |
| `ConvertExpression` | 単一式をC#表現へ変換する |
| `WriteStatement` | VBステートメントを種類別に出力する |
| `WriteType` | Class、Structure、Interface、Moduleを出力する |
| `WriteEnum` | Enumの属性、基底型、メンバーおよび初期値を出力する |
| `WriteAttributes` | VB属性リストをC#属性として出力する |
| `Attribute` | 属性の位置引数と名前付き引数をC#表現へ変換する |
| `WriteMethod` | メソッドブロックを出力する |
| `WriteConstructor` | Sub Newをinstance／Sharedコンストラクターとして出力する |
| `TryConstructorInitializer` | MyBase.New／Me.Newをbase／this initializerへ変換する |
| `WriteSingleLineIf` | 単行IfのThen／ElseをC#ブロックへ変換する |
| `WriteSelectBlock` | Select Caseを選択値の一度評価とif／else ifへ変換する |
| `SelectCaseCondition` | Caseの単一値、範囲、比較句を条件式へ変換する |
| `CombineSelectRange` | Case範囲の上下限条件を結合する |
| `SelectComparison` | Select対象型に応じた比較式を生成する |
| `RelationalOperator` | Case Isの演算子をC#演算子へ変換する |
| `VisualBasicOperatorAccess` | VB Operators比較メソッドの参照を生成する |
| `WithLabelScope` | メソッド内Labelと生成名の対応表を管理する |
| `UsesTextComparison` | Option Compare Textの有効状態を判定する |
| `WriteLabel` | LabelStatementをC# Labelへ変換する |
| `WriteGoTo` | GoToStatementを対応するC# Label参照へ変換する |
| `WriteTryBlock` | Try、Catch、Finallyを同じ順序のC#例外処理へ変換する |
| `WriteCatchBlock` | Catchの例外型、変数、Whenフィルターと本体を変換する |
| `CatchType` | Catch例外型をGlobal Importsに依存しない完全修飾名へ変換する |
| `WriteForBlock` | 境界値とStepを一度だけ評価するC#数値ループを出力する |
| `WriteForEachBlock` | 列挙情報と制御変数の動作を維持してC# foreachを出力する |
| `TryGetForEachControl` | For Eachの宣言付き／宣言済み制御変数を意味解析する |
| `CanSafelyEnumerate` | 列挙型と要素変換をC#で安全に表現できるか判定する |
| `WriteWithBlock` | With対象を一度だけ評価して先頭ドットの参照を展開する |
| `IsReadOnlyValueTypeWith` | 値型Withの本体が読み取り専用か保守的に判定する |
| `IsWithBasedExpression` | 式がWith対象を起点とするか判定する |
| `HasOmittedWithReceiver` | 先頭ドットで受信側が省略されたメンバーか判定する |
| `TryGetForControl` | For制御変数と組み込み数値型を安全に解決する |
| `CanAssignForValue` | Forの開始値、終了値、Step値が縮小変換なしで代入可能か判定する |
| `CSharpNumericType` | For対応数値型をC#キーワードへ対応付ける |
| `CSharpTypeName` | 型シンボルをGlobal Importsに依存しないC#型名へ変換する |
| `CreateUniqueTemporaryName` | For用一時変数の衝突しない名前を生成する |
| `MethodSignature` | C#メソッドシグネチャを生成する |
| `WriteProperty` | PropertyとAccessorを出力する |
| `PropertySignature` | PropertyまたはIndexerのシグネチャを生成する |
| `WriteDeclaration` | FieldまたはLocal変数宣言を出力する |
| `TryArrayBounds` | 変数名側の配列上限をC#の配列長へ変換する |
| `Expr` | VB式を種類別にC#へ変換する |
| `Invocation` | Method、Array、Indexer呼び出しを変換する |
| `IndexerTarget` | ItemプロパティをC# Indexer対象へ変換する |
| `Member` | MemberAccessと引数なしMethodを変換する |
| `Identifier` | Identifierと暗黙Method呼び出しを変換する |
| `Arguments` | 引数リストを変換し、必要なEnum変換を適用する |
| `ObjectCreation` | Object生成式とコンストラクター引数を変換する |
| `ArrayCreation` | VB配列生成式の型、Rank、上限値、初期化子をC#配列生成式へ変換する |
| `CollectionInitializer` | 配列・コレクション初期化子を入れ子構造を保って変換する |
| `CType` | CTypeを意味解析し、VB互換ConversionsまたはC#明示キャストへ変換する |
| `ParenthesizedCast` | DirectCast等を後続メンバーアクセスに安全な括弧付きキャストへ変換する |
| `VisualBasicConversionMethod` | 組み込み変換先型をVB Conversionsメソッド名へ対応付ける |
| `VisualBasicConversion` | CTypeのVB互換変換呼び出しを生成して変換ログへ記録する |
| `PredefinedCast` | CInt、CStrなどをVB互換Conversions呼び出しへ変換する |
| `ExprForTarget` | Enumと整数型間で必要な明示キャストを追加する |
| `IsInsideDeclaringEnum` | Enum初期値内の同一Enumメンバー参照か判定する |
| `Literal` | VBリテラルをC#リテラルへ変換する |
| `StringLiteral` | 実タブを維持して安全なC#文字列リテラルを生成する |
| `Type` | VB型構文をC#型表現へ変換する |
| `Parameter` | ParameterとByRefを変換する |
| `Access` | Access modifierを変換する |
| `AccessText` | 文字列化されたAccess modifierを変換する |
| `AssignmentOperator` | 代入演算子を変換する |
| `Binary` | `Is`／`IsNot`を含む二項式を意味に応じて変換する |
| `BinaryOperator` | 二項演算子を変換する |
| `UnaryOperator` | 単項演算子を変換する |
| `Unary` | BooleanのNotとEnum／整数のビット反転を区別して変換する |
| `IsIntegral` | 型がC#整数型か判定する |
| `EnumTypeName` | Enum型の正式名をC#表現で生成する |
| `TypeName` | 型シンボルをC#型名へ変換する |
| `EscapeIdentifier` | C#予約語と一致する識別子をエスケープする |
| `UnsupportedExpression` | 未対応式をManualReviewRequiredにする |
| `Record` | FixResultを記録する |
| `Review` | ManualReviewItemを記録する |
| `WriteLeadingComments` | VBコメントをC#コメントへ変換する |
| `Block` | C#ブロックとインデントを出力する |
| `Line` | インデント付きの1行を出力する |
| `OneLine` | ログ用に文字列を1行化する |
| `IsVisualBasicRuntimeMethod` | Microsoft.VisualBasic由来のMethodか判定する |
| `IsVisualBasicRuntimeValueMember` | Microsoft.VisualBasic由来の静的Field／Propertyか判定する |
| `IsVisualBasicRuntimeSymbol` | AssemblyとNamespaceからVBランタイムシンボルか判定する |
| `VisualBasicRuntimeTypeAccess` | VBランタイム型名またはaliasを決定する |
| `IsGlobalNamespace` | RootNamespace対象外のGlobal Namespaceか判定する |

## 5. LegacyProjectMaterializer

| メソッド | 機能 |
|---|---|
| `MaterializeAsync` | Solution、Project、関連ファイルを出力する |
| `ConvertSolutionAsync` | `.sln`のVB Project情報をC#用へ変更する |
| `ConvertProjectAsync` | 旧形式`.vbproj`を`.csproj`へ変換する |
| `MapSourceDocument` | Compileの物理パス、Link論理パス、C#出力先を対応付ける |
| `ValidateResourceParents` | resxのDependentUponと生成Compile項目の整合性を検証する |
| `NormalizeProjectPath` | Project項目パスを比較可能な形式へ正規化する |
| `CopyImportIfLocal` | 相対指定されたMSBuild Importをコピーする |
| `CopyItemAsync` | Content、Resource、DLLなどを安全にコピーする |
| `MapProjectPath` | `My Project`の標準ファイルを`Properties`へ割り当てる |
| `ContainsWildcard` | MSBuild項目のWildcardを検出する |
| `Review` | Project変換用ManualReviewItemを生成する |
| `DetectEncoding` | Solutionファイルのエンコーディングを検出する |

## 6. OutputLayout

| メソッド | 機能 |
|---|---|
| `ProjectDirectory` | Projectの出力ディレクトリを返す |
| `SourceDestination` | VBソースに対応するC#出力先を返す |
| `PathInProject` | Project相対パスを出力側絶対パスへ変換する |
| `SafeCombine` | 出力領域外へのパス逸脱を防ぐ |
| `IsWithin` | パスがルート配下か判定する |
| `SafeName` | 無効なファイル名文字を置換する |

## 7. VisualBasicRuntimeReferenceService

| メソッド | 機能 |
|---|---|
| `EnsureReferenceAsync` | 必要な旧形式C# ProjectへMicrosoft.VisualBasic参照を追加する |

## 8. ValidationService

| メソッド | 機能 |
|---|---|
| `ValidateSyntax` | 生成C#ファイルの構文エラーを検出する |
| `ValidateCompilation` | Project単位でC# Compilationエラーを検出する |
| `ToPortableReference` | 異言語CompilationReferenceをMetadataReferenceへ変換する |

## 9. GeneratedBuildValidator

| メソッド | 機能 |
|---|---|
| `ValidateAsync` | 生成SolutionまたはProjectをMSBuildする |
| `OneLine` | MSBuild出力をProjectログ用に整形する |

## 10. ConversionLogger

| メソッド | 機能 |
|---|---|
| `WriteAsync` | 変換、コピー、Project、レビュー、集計ログを出力する |
| `Csv` | CSVフィールドをエスケープする |

## 11. Options / SyntaxExtensions

| メソッド | 機能 |
|---|---|
| `Options.Parse` | CLI引数をOptionsへ変換する |
| `Options.Next` | オプション値を絶対パスとして取得する |
| `SyntaxExtensions.Type` | VBのAs句からTypeSyntaxを取得する |
