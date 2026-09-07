# PROCESS FLOW

## 1. 全体フロー

```text
CLI引数解析
  ↓
MSBuild検出・登録
  ↓
Solution / Project / Folder / File読込
  ↓
VB ProjectごとのCompilation構築
  ↓
Solution・Project構成と関連ファイルを出力
  ↓
各VB DocumentのSyntaxTree / SemanticModel取得
  ↓
VB構文をC#へ変換
  ↓
Microsoft.VisualBasic参照を必要時に追加
  ↓
C# Syntax検証
  ↓
Project単位C# Compilation検証
  ↓
生成Solution / ProjectをMSBuild
  ↓
ログとsummaryを出力
```

## 2. 入力ロード

### Solution入力

1. `MSBuildWorkspace.OpenSolutionAsync`でSolutionを開く
2. VBプロジェクトを抽出する
3. 各プロジェクトについてCompilationを構築する
4. Solution内のProjectReferenceをRoslynに解決させる

### Project入力

1. `MSBuildWorkspace.OpenProjectAsync`でルートProjectを開く
2. ProjectReferenceを再帰的に辿る
3. 到達したVBプロジェクトをすべて変換対象にする

### Folder / File入力

1. `.vb`ファイルを列挙する
2. AdhocWorkspaceを作成する
3. Trusted Platform Assembliesを参照に追加する
4. 単一VB Compilationへまとめる

## 3. Project構成出力

1. 出力ルートを計算する
2. `.sln`の`.vbproj`パスを`.csproj`へ変更する
3. VB Project Type GUIDをC#用へ変更する
4. `.vbproj` XMLを読み込む
5. Compile項目、DependentUpon、LastGenOutputを`.cs`へ変更する
6. `Microsoft.VisualBasic.targets`を`Microsoft.CSharp.targets`へ変更する
7. ProjectReferenceを変換後Projectの相対パスへ変更する
8. Resource、Content、None、Settings、DLLなどをコピーする
9. 外部パスは安全な出力領域へ移し、IncludeまたはHintPathを同期する

## 4. 式変換フロー

VBの`xxx(...)`に対して次の順序で判定します。

```text
GetSymbolInfo
  ├─ IMethodSymbol
  │    ├─ Microsoft.VisualBasic由来 → VBランタイム互換呼び出し
  │    └─ その他                    → C# Method呼び出し
  ├─ IPropertySymbol + Parameters  → C# Indexer
  ├─ IArrayTypeSymbol              → C#配列アクセス
  ├─ Candidateが複数               → AmbiguousSymbol
  └─ 解決不能                      → UnresolvedSymbol
```

例:

```vb
service.GetValue(i)
employees(i)
array(i)
Mid(text, 1, 3)
```

```csharp
service.GetValue(i);
employees[i];
array[i];
Strings.Mid(text, 1, 3);
```

## 5. VBランタイム関数・定数

1. Assemblyが`Microsoft.VisualBasic`または`Microsoft.VisualBasic.Core`か確認する
2. Namespaceが`Microsoft.VisualBasic`配下か確認する
3. 利用されたContainingTypeを記録する
4. 通常は`using Microsoft.VisualBasic;`を追加する
5. `Strings.Mid`、`Information.IsDate`、`Constants.vbCrLf`の形式で出力する
6. 型名が競合する場合は`VBStrings`などのaliasを生成する
7. 自作の同名関数は通常Methodとして維持する
8. 旧形式C# Projectに必要なAssembly Referenceを追加する

## 6. 参照比較と文字列

- VBの`Is`／`IsNot`は`object.ReferenceEquals`またはその否定へ変換する
- `=`／`<>`などの値比較とは別の専用処理とし、意味を混同しない
- VB文字列に含まれる実タブはC#文字列でも実タブとして維持する
- 引用符、バックスラッシュ、その他の制御文字はC#リテラルとして安全にエスケープする

## 7. 検証フロー

### Syntax検証

各生成ファイルをCSharpSyntaxTreeとして解析します。

### Compilation検証

Project内の生成ファイルをまとめてCSharpCompilationへ渡します。ProjectReference相当のVB Compilationはメモリ上でemitし、MetadataReferenceとして利用します。

### MSBuild検証

通常実行では次を実行します。

```text
dotnet msbuild <生成SolutionまたはProject>
  /t:Build
  /p:Configuration=Debug
```

`--skip-build`または`--dry-run`では実行しません。

## 8. エラー処理

- 致命的なCLI／Workspace例外: 終了コード1
- ManualReviewRequiredなし: 終了コード0
- ManualReviewRequiredあり: 終了コード2
- 変換不能な個別構文: 処理を継続してレビュー記録
- Project／参照ファイル欠落: コピーを中止してレビュー記録
