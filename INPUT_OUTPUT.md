# INPUT OUTPUT

## 1. CLI

```text
VbToCSharpFixer
  (--solution file.sln | --project file.vbproj | --folder directory | --file file.vb)
  --output directory
  [--dry-run]
  [--verbose]
  [--skip-build]
```

入力指定は必ず一つだけ指定します。

## 2. 入力例

### Solution

```powershell
VbToCSharpFixer.exe `
  --solution "C:\Source\LegacySystem.sln" `
  --output "C:\Converted"
```

### Project

```powershell
VbToCSharpFixer.exe `
  --project "C:\Source\LegacyApp\LegacyApp.vbproj" `
  --output "C:\Converted"
```

ProjectReference先のVBプロジェクトも再帰的に出力されます。

複数ProjectからLinkされているVBソースとresxは、出力側の共有先へ配置します。各Projectの`Link`を保持し、`Include`を共有先への相対パスへ更新します。VBソースの拡張子は`.cs`へ変更します。

### dry-run

```powershell
VbToCSharpFixer.exe `
  --solution "C:\Source\LegacySystem.sln" `
  --output "C:\Preview" `
  --dry-run
```

dry-runでは変換コード、Project、Solution、Resourceを作成せず、ログだけを生成します。

## 3. 入力対象

### 解析・変換対象

- `.sln`
- `.vbproj`
- `.vb`
- `.Designer.vb`

### コピー対象

- `.resx`
- `.settings`
- `app.config`
- `packages.config`
- Content
- None
- EmbeddedResource
- Resource
- AdditionalFiles
- Page
- SplashScreen
- TypeScriptCompile
- ローカルHintPath DLL
- 相対指定されたカスタムMSBuild Import

### 標準除外

- `bin`
- `obj`
- MSBuildが`obj`配下へ生成したVBソース

## 4. 出力例

Solution入力:

```text
C:\Converted\
├─ converted\
│  └─ LegacySystem\
│     ├─ LegacySystem.sln
│     ├─ LegacyApp\
│     │  ├─ LegacyApp.csproj
│     │  ├─ Form1.cs
│     │  ├─ Form1.Designer.cs
│     │  ├─ Form1.resx
│     │  ├─ app.config
│     │  └─ Properties\
│     └─ CommonLibrary\
│        ├─ CommonLibrary.csproj
│        └─ CommonService.cs
├─ logs\
│  ├─ conversion.log
│  ├─ file-copy.log
│  ├─ project-conversion.log
│  └─ manual-review.csv
└─ summary.txt
```

リンクされたFormは、出力側の共有先に一式を配置します。各Projectでは`Link`と`DependentUpon`により表示上の親子関係を維持します。

```text
Shared\SharedForm.cs
Shared\SharedForm.Designer.cs
Shared\SharedForm.resx
```

Project入力では主Projectを`converted/<Project名>`へ出力し、参照Projectは`converted`直下の兄弟ディレクトリへ出力します。

リンク元が変換対象Project内にある場合は、そのProjectの出力先を共有します。ソリューション内の共有フォルダーは相対配置を維持し、範囲外のリンク元は`converted/_linked/<元フォルダーのハッシュ>`へ配置します。元の入力ファイルは変更しません。

共有ソースは各Projectの条件で変換して比較します。結果が異なる場合は`LinkedSourceConflict`を記録し、`logs/linked-conflicts/*.txt`へProject別の結果を保存します。共有先には`#error`を出力し、片方の結果を黙って採用・上書きしません。dry-runでは差分検出のみ行い、比較レポートと共有ファイルを書き込みません。

## 5. ログ

### conversion.log

- Project
- File
- Line / Column
- FixType
- Symbol
- DeclaringType
- Assembly / Project
- Before / After
- Reason

### file-copy.log

- SourcePath
- DestinationPath
- ItemType
- Action
- Result
- FileSize

### project-conversion.log

- Project / Solution変換
- Microsoft.VisualBasic参照追加
- MSBuild検証結果

### manual-review.csv

- Project
- File
- Line / Column
- Code
- ReasonCode
- Details

### summary.txt

- 処理ファイル数
- Fix数
- コピー／生成予定数
- Project／Solution処理数
- ManualReviewRequired数
- Workspace診断数
- dry-run状態

## 6. 終了コード

| コード | 意味 |
|---|---|
| `0` | 正常終了、ManualReviewRequiredなし |
| `1` | CLI、Workspaceまたは予期しない致命的エラー |
| `2` | 変換は完了したがManualReviewRequiredあり |

## 7. 代表的な変換

```vb
DataGridView1.Rows(i).Cells(j).Value.ToString
employees(i)
service.Close
Mid(text, 1, 3)
IsDate(value)
```

```csharp
DataGridView1.Rows[i].Cells[j].Value.ToString();
employees[i];
service.Close();
Strings.Mid(text, 1, 3);
Information.IsDate(value);
```
