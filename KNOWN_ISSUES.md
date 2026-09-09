# KNOWN ISSUES

## 1. 対応方針

本ツールは誤変換を避けることを優先します。未対応または意味を確定できないコードは、推測変換せず`ManualReviewRequired`へ記録します。

## 2. 未対応または限定対応のVB構文

- Event、Delegate、`Handles`、`WithEvents`
- Object／Late Bindingまたは値型を書き換える`With`ブロック
- Object／Late Binding／ユーザー定義変換を使用するFor／For Each、While、Do Loop
- `Select Case`
- `Exit Try`、`On Error`、`Resume`
- LINQ query syntax
- Iterator、AsyncのVB固有構文
- XMLリテラル
- VBの匿名型や特殊なObject Initializer
- 複雑なOptional／Named Argument
- 複雑なProperty copy-back
- `Mid(text, ...) = value`形式のMid代入文

未対応ステートメントは生成C#内のコメントと`manual-review.csv`へ記録されます。

## 3. Late Binding

`Option Strict Off`によるlate bindingは、コンパイル時に一意な`IMethodSymbol`または`IPropertySymbol`を取得できない場合があります。この場合はMethod、Property、Indexerを推測しません。

## 4. IsNull

.NET Framework 4.8の標準`Microsoft.VisualBasic.Information`には一般的な`IsNull`メソッドがありません。

`IsNull`が存在する場合は次の可能性があります。

- ユーザー定義関数
- 外部ライブラリ関数
- VB6／VBA移行時の残存コード
- `IsDBNull`またはNothing判定を意図したコード

Symbolを解決できなければ、`IsDBNull`や`is null`へ推測変換しません。

## 5. VBランタイム関数・定数

`Mid`、`Format`、`IsDate`、`IsNothing`などの関数と、`vbCrLf`、`vbCr`、`vbTab`などの定数は互換性を優先し、Microsoft.VisualBasicの型を通して参照します。

```csharp
using Microsoft.VisualBasic;

Strings.Mid(...);
Strings.Format(...);
Information.IsDate(...);
Constants.vbCrLf;
```

現在、`Substring`、`string.Format`、`DateTime.TryParse`などへのC#ネイティブ最適化は実施しません。境界値、Nothing、カルチャ、例外、評価順序の完全な同値性が確認された規則だけを将来追加する方針です。

VBの`Is`／`IsNot`は参照同一性を維持するため`object.ReferenceEquals`へ変換します。C#の`==`／`!=`には変換しません。

`CInt`、`CStr`、`CDate`などの定義済み型変換は、単純なC#キャストや`ToString`へ置換せず、丸め、Nothing、カルチャなどのVB動作を優先して`Microsoft.VisualBasic.CompilerServices.Conversions`へ変換します。`CObj`はC#のObjectキャストへ変換します。

## 6. Enum

Enumの基底型、明示値、属性、Flags演算およびメンバー参照を変換します。VBとC#の大文字・小文字の違いとC#予約語を補正し、Enumと整数型の間でC#に必要な明示キャストを追加します。

Enum値を使用していても、外側が未対応の`Select Case`などである場合は、そのステートメント全体が引き続き`ManualReviewRequired`になります。

## 7. VB Application Framework

次は完全自動変換の対象外です。

- `Application.myapp`
- `My.Application`
- `My.Forms`
- SingleInstance
- SplashScreen
- ShutdownMode
- VB Application Frameworkが生成するエントリポイント

該当Projectは`UnsupportedApplicationFramework`または`StartupObjectUnresolved`として記録されます。

## 8. COM参照

COMReferenceとCOMFileReferenceのProject情報は可能な範囲で維持しますが、以下は自動化しません。

- COMコンポーネントのインストール
- COM登録
- ActiveX Wrapperの再生成
- 32bit／64bit互換性判断

COM参照はManualReviewRequiredになります。

## 9. Wildcard Project Item

次のようなWildcard指定は、ファイル集合を安全に確定できないためレビュー対象です。

```xml
<Content Include="Data\**\*.*" />
```

Project XMLの指定自体は維持しますが、Wildcard展開による全ファイルコピーは行いません。

## 10. Project外リンクと外部DLL

Project外のリンクファイルやHintPath DLLは出力ルート内の安全な場所へコピーし、Include／HintPathを更新します。外部ファイルをコピーした事実は`ExternalLinkedFile`としてレビュー記録される場合があります。

参照ファイルが存在しない場合は`MissingReference`または`MissingContentFile`になります。

## 11. NuGet

`packages.config`と既存HintPathは維持しますが、ツール自身はNuGetパッケージの復元保証やパッケージ形式の変換を行いません。生成SolutionのMSBuild時に必要なパッケージが存在しない場合、ビルド検証が失敗します。

## 12. Frameworkとビルド環境

対象FrameworkのDeveloper Pack、Reference Assemblies、Visual Studio Build Tools、カスタムtargetsが存在しない環境では、MSBuildWorkspaceのロードまたは最終ビルドが失敗することがあります。

ビルドを別環境で行う場合は`--skip-build`を指定できます。

## 13. 既存出力

同じ出力ディレクトリを再利用すると、対象ファイルは再生成または上書きされます。以前の実行で生成され、今回の入力には存在しない古いファイルを自動削除する処理はありません。クリーンな出力ディレクトリの利用を推奨します。

## 14. 非VBプロジェクト

Solution内のVBプロジェクトが主な変換対象です。C++、セットアップ、データベースなどの別Project形式をC#へ変換する機能はありません。Solution全体に特殊Projectが含まれる場合は、生成Solutionの参照パスとビルド結果を確認してください。
