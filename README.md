# VbToCSharpFixer

.NET 8 / Roslyn の `SyntaxTree`、`SemanticModel`、シンボル情報を使う、保守的な VB.NET → C# 変換補正 CLI です。メソッド、配列、既定/Item プロパティを名前ではなく解決済みシンボルから区別します。判定不能または未対応構文は `ManualReviewRequired` に記録します。

```powershell
dotnet run --project src/VbToCSharpFixer -- --solution C:\src\App.sln --output C:\out
dotnet run --project src/VbToCSharpFixer -- --project C:\src\App.vbproj --output C:\out --dry-run
dotnet run --project src/VbToCSharpFixer -- --folder C:\src\vb --output C:\out
```

出力は `converted/<project>/`、`logs/conversion.log`、`logs/manual-review.csv`、`summary.txt` です。`--dry-run` は変換コードを書かず、ログだけを生成します。

## 設計上の境界

この実装は誤変換回避を優先します。主要な型・メソッド・プロパティ・式・宣言・条件分岐は変換しますが、イベント、LINQ query syntax、複雑な制御構文など未対応の VB 構文はレビュー対象です。`.sln/.vbproj` 入力では `MSBuildWorkspace` が ProjectReference、DLL/NuGet/Framework 参照、Imports、Define、RootNamespace 等をロードします。フォルダ/単一ファイル入力では .NET 8 の platform assemblies のみを参照します。
