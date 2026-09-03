# VbToCSharpFixer

.NET 8 / Roslyn の `SyntaxTree`、`SemanticModel`、シンボル情報を使う、保守的な VB.NET → C# 変換補正 CLI です。
メソッド、配列、既定/Item プロパティを名前ではなく解決済みシンボルから区別します。
判定不能または未対応構文は `ManualReviewRequired` に記録します。

このコードは、Codexによって自動生成されたコードです。

```powershell
dotnet run --project src/VbToCSharpFixer --solution C:\src\App.sln --output C:\out
dotnet run --project src/VbToCSharpFixer --project C:\src\App.vbproj --output C:\out --dry-run
dotnet run --project src/VbToCSharpFixer --folder C:\src\vb --output C:\out
dotnet run --project src/VbToCSharpFixer --folder C:\src\vb --output C:\out --skip-build

```

`.sln` / `.vbproj` 入力では、元の相対構成を保った変換済みソリューションを `converted/<solution>/` または `converted/<project>/` に生成します。旧形式 `.vbproj` は `.csproj` へ変換し、`.resx`、`.settings`、`app.config`、Content、None、EmbeddedResource、ローカル HintPath DLLなど、プロジェクトに登録された非VBファイルをコピーします。

ログは `logs/conversion.log`、`logs/file-copy.log`、`logs/project-conversion.log`、`logs/manual-review.csv` と `summary.txt` です。`--dry-run` は変換ソース、プロジェクト、リソースを作成せず、予定内容をログだけに出力します。

旧形式プロジェクトでは次も補正します。

- VB Project Type GUIDからC# Project Type GUID
- `Microsoft.VisualBasic.targets`から`Microsoft.CSharp.targets`
- `.vb` / `.Designer.vb` / `DependentUpon` / `LastGenOutput`
- 変換対象ProjectReferenceの`.vbproj`から`.csproj`
- `My Project`のResources、Settings、manifestから`Properties`への配置
- VB Resources generatorからC# Resources generator

COM参照、VB Application Framework、StartupObject、ワイルドカード項目、欠落参照およびプロジェクト外リンクは保持可能な情報を残し、`ManualReviewRequired`にも記録します。

通常実行の最後に生成されたソリューション／プロジェクトを`dotnet msbuild`で検証します。
ビルド環境や外部依存の都合で省略する場合は`--skip-build`を指定します。dry-runではビルドしません。

## Microsoft.VisualBasic互換関数

`Mid`、`Format`、`IsDate`、`IsNothing`など、SemanticModelで`Microsoft.VisualBasic`由来と確認できた関数は、可読性を保った互換呼び出しに変換します。

```csharp
using Microsoft.VisualBasic;

var part = Strings.Mid(text, 1, 3);
var display = Strings.Format(value, "@@@");
var valid = Information.IsDate(value);
```

`Strings`や`Information`という名前がソース内で衝突する場合は、`VBStrings`、`VBInformation`などのusing aliasを自動生成します。自作の同名関数はシンボルのAssemblyとContainingTypeが異なるため変換しません。旧形式C#プロジェクトには、使用時だけ`Microsoft.VisualBasic`アセンブリ参照を追加します。動作の完全な同値性を保証できない関数を`Substring`や`string.Format`などへ置き換えることはしません。

## 設計上の境界

この実装は誤変換回避を優先します。主要な型・メソッド・プロパティ・式・宣言・条件分岐は変換しますが、イベント、LINQ query syntax、複雑な制御構文など未対応の VB 構文はレビュー対象です。`.sln/.vbproj` 入力では `MSBuildWorkspace` が ProjectReference、DLL/NuGet/Framework 参照、Imports、Define、RootNamespace 等をロードします。フォルダ/単一ファイル入力では .NET 8 の platform assemblies のみを参照します。
