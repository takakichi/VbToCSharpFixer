# 処理フロー

この文書は、`VbToCSharpFixer`が入力を読み込み、VBソースとプロジェクト構成をC#へ変換し、結果を検証するまでの大まかな流れを示します。

## CLI全体の流れ

```mermaid
flowchart TD
    Start([CLI起動]) --> Parse[コマンドライン引数を解析]
    Parse -->|引数が不正| ArgumentError[エラーを表示]
    ArgumentError --> Exit1([終了コード 1])

    Parse --> Load[WorkspaceLoader.LoadAsync]
    Load --> InputType{入力の種類}
    InputType -->|Solution / Project| MSBuild[MSBuildWorkspaceで読み込み]
    InputType -->|Folder / File| Adhoc[AdhocWorkspaceを構築]
    MSBuild --> Compilation[VB ProjectとCompilationを取得]
    Adhoc --> Compilation

    Compilation --> Materialize[出力構成を生成]
    Materialize --> ProjectFiles[Solution / Project / リソースを変換・コピー]
    ProjectFiles --> ProjectLoop{各VBプロジェクト}

    ProjectLoop --> SourceConvert[プロジェクト内のVB文書を変換]
    SourceConvert --> ProjectValidation[生成C#をプロジェクト単位でコンパイル検証]
    ProjectValidation --> RuntimeReference[必要なVBランタイム参照を追加]
    RuntimeReference --> MoreProjects{次のプロジェクトがあるか}
    MoreProjects -->|ある| ProjectLoop
    MoreProjects -->|ない| BuildDecision{生成物をビルドするか}

    BuildDecision -->|通常実行| MSBuildValidation[dotnet msbuildで検証]
    BuildDecision -->|dry-run / skip-build / 対象なし| Logs[ログと集計を出力]
    MSBuildValidation --> Logs

    Logs --> Reviews{ManualReviewRequiredがあるか}
    Reviews -->|ない| Exit0([終了コード 0])
    Reviews -->|ある| Exit2([終了コード 2])

    Load -. 予期しない例外 .-> FailureLog[error.logへ記録]
    Materialize -. 予期しない例外 .-> FailureLog
    SourceConvert -. 予期しない例外 .-> FailureLog
    FailureLog --> Exit1
```

中心となる呼び出し順は、`Program.Main` → `ConversionRunner.RunAsync` → `WorkspaceLoader.LoadAsync` → `LegacyProjectMaterializer.MaterializeAsync` → `ProjectSourceConverter.ConvertAsync`です。

## 1つのVB文書を変換する流れ

```mermaid
flowchart TD
    Document[VB Document] --> Tree[SyntaxTreeを取得]
    Tree --> Semantic[SemanticModelを取得]
    Semantic --> Session[ConversionSessionを生成]

    Session --> RootNamespace{RootNamespaceがあるか}
    RootNamespace -->|ある| SplitGlobal[通常メンバーとGlobalメンバーを分離]
    RootNamespace -->|ない| Statements[各ステートメントを変換]
    SplitGlobal --> Statements

    Statements --> Dispatch{構文の種類}
    Dispatch -->|型・メソッド・変数| Declarations[宣言を変換]
    Dispatch -->|呼び出し・演算・リテラル| Expressions[式を変換]
    Dispatch -->|If・Try・For・Select・With| ControlFlow[制御構文を変換]
    Dispatch -->|未対応・判定不能| ManualReview[ManualReviewRequiredへ記録]

    Declarations --> SemanticFixes[SemanticModelに基づく補正]
    Expressions --> SemanticFixes
    ControlFlow --> SemanticFixes

    SemanticFixes --> Imports[Imports・VBランタイムaliasを確定]
    ManualReview --> Imports
    Imports --> Result[ConversionResultを生成]

    Result --> CSharp[C#ソース]
    Result --> FixLog[変換記録]
    Result --> ReviewLog[手動確認項目]
    Result --> RuntimeTypes[使用したVBランタイム型]
```

`ConversionSession`は構文の文字列だけではなく、Roslynの`SemanticModel`で解決したシンボルと型を使います。これにより、同じ丸括弧を使うメソッド呼び出し・配列アクセス・Indexerや、同名の型・メンバーを区別します。安全に判断できない場合は推測で変換せず、`ManualReviewRequired`として記録します。

## 主なクラスの役割

| クラス | 主な役割 |
|---|---|
| `Program` | 引数解析、終了コード、最上位の例外処理 |
| `ConversionRunner` | 読み込みからログ出力までの実行順序を管理 |
| `WorkspaceLoader` | Solution、Project、Folder、FileをRoslynのCompilationへ読み込み |
| `LegacyProjectMaterializer` | 変換後のSolution／Project構成と出力先を生成 |
| `ProjectConverter` | `.vbproj`の項目、参照、リソース、関連ファイルを変換 |
| `ProjectSourceConverter` | プロジェクト内の各VB文書を変換し、生成C#をまとめて検証 |
| `VbToCSharpConverter` | 1ファイル単位の変換窓口 |
| `ConversionSession` | 宣言、式、制御構文を意味情報に基づいてC#へ変換 |
| `ValidationService` | 生成C#の構文およびCompilationエラーを検出 |
| `GeneratedBuildValidator` | 生成されたSolution／Projectを`dotnet msbuild`で検証 |
| `ConversionLogger` | 変換記録、手動確認項目、ファイル操作、集計を出力 |

