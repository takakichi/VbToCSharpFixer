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
| `WriteMethod` | メソッドブロックを出力する |
| `WriteTryBlock` | Try、Catch、Finallyを同じ順序のC#例外処理へ変換する |
| `WriteCatchBlock` | Catchの例外型、変数、Whenフィルターと本体を変換する |
| `CatchType` | Catch例外型をGlobal Importsに依存しない完全修飾名へ変換する |
| `WriteForBlock` | 境界値とStepを一度だけ評価するC#数値ループを出力する |
| `TryGetForControl` | For制御変数と組み込み数値型を安全に解決する |
| `CanAssignForValue` | Forの開始値、終了値、Step値が縮小変換なしで代入可能か判定する |
| `CSharpNumericType` | For対応数値型をC#キーワードへ対応付ける |
| `CreateUniqueTemporaryName` | For用一時変数の衝突しない名前を生成する |
| `MethodSignature` | C#メソッドシグネチャを生成する |
| `WriteProperty` | PropertyとAccessorを出力する |
| `PropertySignature` | PropertyまたはIndexerのシグネチャを生成する |
| `WriteDeclaration` | FieldまたはLocal変数宣言を出力する |
| `Expr` | VB式を種類別にC#へ変換する |
| `Invocation` | Method、Array、Indexer呼び出しを変換する |
| `IndexerTarget` | ItemプロパティをC# Indexer対象へ変換する |
| `Member` | MemberAccessと引数なしMethodを変換する |
| `Identifier` | Identifierと暗黙Method呼び出しを変換する |
| `Arguments` | 引数リストを変換する |
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
