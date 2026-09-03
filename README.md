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

## 設計上の境界

この実装は誤変換回避を優先します。主要な型・メソッド・プロパティ・式・宣言・条件分岐は変換しますが、イベント、LINQ query syntax、複雑な制御構文など未対応の VB 構文はレビュー対象です。`.sln/.vbproj` 入力では `MSBuildWorkspace` が ProjectReference、DLL/NuGet/Framework 参照、Imports、Define、RootNamespace 等をロードします。フォルダ/単一ファイル入力では .NET 8 の platform assemblies のみを参照します。
