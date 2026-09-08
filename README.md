# VbToCSharpFixer

.NET 8 / Roslyn の `SyntaxTree`、`SemanticModel`、シンボル情報を使う、保守的な VB.NET → C# 変換補正 CLI です。メソッド、配列、既定/Item プロパティを名前ではなく解決済みシンボルから区別します。判定不能または未対応構文は `ManualReviewRequired` に記録します。

```powershell
dotnet run --project src/VbToCSharpFixer -- --solution C:\src\App.sln --output C:\out
dotnet run --project src/VbToCSharpFixer -- --project C:\src\App.vbproj --output C:\out --dry-run
dotnet run --project src/VbToCSharpFixer -- --folder C:\src\vb --output C:\out
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

通常実行の最後に生成されたソリューション／プロジェクトを`dotnet msbuild`で検証します。ビルド環境や外部依存の都合で省略する場合は`--skip-build`を指定します。dry-runではビルドしません。

## Microsoft.VisualBasic互換関数

`Mid`、`Format`、`IsDate`、`IsNothing`などの関数と、`vbCrLf`、`vbCr`、`vbTab`などの定数は、SemanticModelで`Microsoft.VisualBasic`由来と確認できた場合だけ、可読性を保った互換参照に変換します。

```csharp
using Microsoft.VisualBasic;

var part = Strings.Mid(text, 1, 3);
var display = Strings.Format(value, "@@@");
var valid = Information.IsDate(value);
var lines = "first" + Constants.vbCrLf + "second";
```

`Strings`、`Information`、`Constants`という名前がソース内で衝突する場合は、`VBStrings`、`VBInformation`、`VBConstants`などのusing aliasを自動生成します。自作の同名関数や定数はシンボルのAssemblyとContainingTypeが異なるため変換しません。旧形式C#プロジェクトには、使用時だけ`Microsoft.VisualBasic`アセンブリ参照を追加します。動作の完全な同値性を保証できない関数を`Substring`や`string.Format`などへ置き換えることはしません。

VBの`Is`／`IsNot`による参照同一性比較は、演算子オーバーロードとC#言語バージョンの影響を避けるため、`object.ReferenceEquals(...)`へ変換します。VB文字列内の実タブは、生成C#でも実タブのまま維持します。

通常の`Try`／`Catch`／`Finally`、複数Catch、`Catch When`をC#の例外処理へ変換します。組み込み数値型の`For`は開始値、終了値、Step値の評価回数を維持するため一時変数を生成し、正負どちらのStepにも対応します。`Exit For`と`Continue For`もそれぞれ`break`と`continue`へ変換します。Object型やユーザー定義変換など安全性を確定できないForは`ManualReviewRequired`に残します。

`For Each`は列挙情報と要素変換をSemanticModelで確認し、内部用の反復変数を介して変換します。これにより、VB側の制御変数への再代入と、宣言済み変数に最後の要素が残る動作を維持します。参照型を対象とする`With`は対象式を一度だけ評価する一時変数へ展開し、入れ子、メソッド、プロパティ、Indexerに対応します。値型Withは読み取り専用の場合だけ変換します。

## 設計上の境界

この実装は誤変換回避を優先します。主要な型・メソッド・プロパティ・式・宣言・条件分岐は変換しますが、イベント、LINQ query syntax、複雑な制御構文など未対応の VB 構文はレビュー対象です。`.sln/.vbproj` 入力では `MSBuildWorkspace` が ProjectReference、DLL/NuGet/Framework 参照、Imports、Define、RootNamespace 等をロードします。フォルダ/単一ファイル入力では .NET 8 の platform assemblies のみを参照します。
