# SYSTEM OVERVIEW

## 1. システム概要

VbToCSharpFixerは、VB.NETからC#へ移行するコードをRoslynの構文・意味情報に基づいて変換する.NET 8コンソールツールです。主な対象は.NET Framework 4.xの旧形式Windows Formsソリューションです。

単純な正規表現ではなく、VBの`SyntaxTree`、`SemanticModel`、`ISymbol`、型情報を利用して、メソッド呼び出し、配列アクセス、既定プロパティ、Indexerを区別します。判定できない構文は推測せず`ManualReviewRequired`として記録します。

ソース変換に加えて、旧形式`.vbproj`から`.csproj`への変換、`.sln`更新、ProjectReference、リソース、設定、Content、ローカルDLLなどの出力も行います。

## 2. 技術構成

| 項目 | 内容 |
|---|---|
| 実装言語 | C# |
| ツール実行環境 | .NET 8 |
| 主な対象 | .NET Framework 4.x旧形式VB.NETプロジェクト |
| 構文・意味解析 | Microsoft.CodeAnalysis.VisualBasic / CSharp |
| Project/Solution読込 | MSBuildWorkspace |
| MSBuild検出 | Microsoft.Build.Locator |
| テスト | NUnit |
| 対応OS | Windows |

## 3. 主要コンポーネント

### Program

処理全体を統括します。入力のロード、構成出力、ソース変換、検証、ビルド、ログ出力を順番に実行します。

### WorkspaceLoader

`.sln`または`.vbproj`を`MSBuildWorkspace`で読み込みます。Project入力の場合はProjectReferenceを再帰的に収集します。Folder/File入力ではAdhocWorkspaceと実行環境の参照アセンブリを使います。

### SymbolClassifier

VBの式を次の意味に分類します。

- Method
- Property
- Array
- Indexer
- Value
- Unresolved
- Ambiguous

### VbToCSharpConverter

VB SyntaxTreeを走査してC#コードを生成します。呼び出し式はSymbolClassifierの結果を利用し、メソッドの丸括弧とIndexerの角括弧を区別します。

`Microsoft.VisualBasic`由来の関数は、通常次の形式にします。

```csharp
using Microsoft.VisualBasic;

var part = Strings.Mid(text, 1, 3);
var valid = Information.IsDate(value);
```

`Strings`などの型名が競合するときはusing aliasを生成します。

### LegacyProjectMaterializer

旧形式プロジェクトとSolution構成をC#用に出力します。

- `.sln`内のプロジェクトパスとProject Type GUIDを変更
- `.vbproj`から`.csproj`を生成
- VB MSBuild targetsからC# targetsへ変更
- ProjectReferenceを出力先へ付け替え
- 非VBプロジェクト項目をコピー
- `My Project`の標準項目を`Properties`へ割り当て
- LinkされたVBソースをProjectごとの論理パスへ個別出力
- Form、Designer、resxのDependentUpon整合性を検証

### OutputLayout

Solution、Project、Folder、Fileの入力形式に応じて、安全な出力パスを決定します。出力ルート外へのパストラバーサルを防止します。

### VisualBasicRuntimeReferenceService

変換後コードがVBランタイム関数を使う場合、旧形式C#プロジェクトへ`Microsoft.VisualBasic`参照を追加します。SDK形式ではターゲットフレームワーク側の参照を利用します。

### ValidationService

生成C#コードをSyntaxTreeおよびプロジェクト単位のCSharpCompilationで検証します。VB CompilationReferenceは一時的なPortable Metadataへ変換します。

### GeneratedBuildValidator

通常実行の最後に、生成されたSolutionまたはProjectを`dotnet msbuild`でビルドします。失敗は`GeneratedProjectBuildFailure`として記録します。

### ConversionLogger

変換、コピー、Project変換、ManualReviewRequired、集計結果をファイルへ出力します。

## 4. 設計原則

1. 名前ではなくSymbolで判定する
2. 意味が確定しないコードを推測変換しない
3. VBランタイム互換性をC#らしさより優先する
4. 元Solutionと元ファイルを変更しない
5. 出力先から外れる書込みを行わない
6. 生成コードをSyntax、Compilation、MSBuildの複数段階で検証する

## 5. ソース構成

```text
src/VbToCSharpFixer/
├─ Program.cs
├─ WorkspaceLoader.cs
├─ SymbolClassifier.cs
├─ VbToCSharpConverter.cs
├─ LegacyProjectMaterializer.cs
├─ OutputLayout.cs
├─ VisualBasicRuntimeReferenceService.cs
├─ ValidationService.cs
├─ GeneratedBuildValidator.cs
├─ ConversionLogger.cs
└─ Models.cs

tests/VbToCSharpFixer.Tests/
├─ SemanticConversionTests.cs
└─ LegacyProjectMaterializerTests.cs
```

## 6. 依存パッケージ

- Microsoft.Build.Locator
- Microsoft.CodeAnalysis.CSharp.Workspaces
- Microsoft.CodeAnalysis.VisualBasic.Workspaces
- Microsoft.CodeAnalysis.Workspaces.MSBuild
- Microsoft.NET.Test.Sdk
- NUnit
- NUnit3TestAdapter
