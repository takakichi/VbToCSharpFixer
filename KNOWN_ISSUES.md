# KNOWN ISSUES

## 1. 対応方針

本ツールは誤変換を避けることを優先します。未対応または意味を確定できないコードは、推測変換せず`ManualReviewRequired`へ記録します。

## 2. 未対応または限定対応のVB構文

- Event、Delegate、`Handles`、`WithEvents`
- Object／Late Bindingまたは値型を書き換える`With`ブロック
- Object／Late Binding／ユーザー定義変換を使用するFor／For Each、Boolean以外の条件を持つWhile、Do Loop
- ユーザー定義変換や安全性を確定できない比較を含む`Select Case`
- `Exit Try`、`On Error`、`Resume`
- LINQ query syntax
- Iterator、AsyncのVB固有構文
- XMLリテラル
- VBの匿名型や特殊なObject Initializer
- 複雑なOptional／Named Argument
- 複雑なProperty copy-back
- `Mid(text, ...) = value`形式のMid代入文

未対応ステートメントは生成C#内のコメントと`manual-review.csv`へ記録されます。

`Using ... End Using`は、変数宣言、`As New`、型推論、既存式および複数リソースに対応します。複数リソースは生成順序と逆順のDisposeを維持するため、従来形式のC# usingブロックを入れ子にして出力します。リソースの型または初期化式を解決できない場合は`ManualReviewRequired`になります。

通常のメソッドコメントに加え、`'''`形式のXML文書コメント、メソッド宣言行末、`End Sub`／`End Function`直前および行末のコメントを保持します。プリプロセッサディレクティブやコメント内のVB固有XML参照をC#向けに意味変換する処理は行いません。

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

`CType`も変換先が組み込み型の場合は同じ`Conversions`を使用します。参照型、配列、Enum、Objectおよびユーザー定義変換は意味解析後にC#キャストへ変換し、キャスト式全体を括ることでDesignerコードの`BeginInit`／`EndInit`など後続メソッド呼び出しの結合先を維持します。型または変換を解決できない場合は推測せず`ManualReviewRequired`になります。

式の受信側に現れる`String`、`Integer`、`Boolean`、`Date`などのVB組み込み型もC#型名へ変換します。これにより`String.Format`、`String.Empty`、`Integer.Parse`などが`default.Format`／`default.Empty`になる問題を防ぎます。

## 6. ref／out

VBソースで宣言された`ByRef`は`out`へ推測せず`ref`を維持します。呼び出し側では、解決済みパラメーターの参照種別、引数型の完全一致、およびローカル変数・引数・書き換え可能フィールド・配列要素のいずれかであることを確認できた場合だけ`ref`／`out`を付加します。外部APIのoutは同じ参照を使ったC# Compilationでメタデータを照合して復元します。

Property、Indexer、キャスト結果、`CInt(value)`などの式、括弧でByValを強制した引数、型変換やVB copy-in/copy-backを伴う引数は自動変換せず`ManualReviewRequired`になります。`ref`へ渡すローカル変数はC#上でも宣言時に明示初期化されている場合だけ自動変換します。

## 7. Windows Forms Designer生成コード

ModuleはVBで暗黙にSharedとなるため、生成するstatic class内のフィールド、プロパティ、メソッド、コンストラクターもSymbolの`IsStatic`に基づいてstatic化します。これにより`Resources.Designer.cs`などでstatic classにinstanceメンバーが生成される問題を防ぎます。

Designerの`New T() { ... }`配列生成は`new T[] { ... }`へ変換し、初期化子内の`Me`も`this`へ変換します。多次元配列初期化子はRankを維持します。Object Initializerの特殊形式は引き続き限定対応です。

## 8. Enum

Enumの基底型、明示値、属性、Flags演算およびメンバー参照を変換します。VBとC#の大文字・小文字の違いとC#予約語を補正し、Enumと整数型の間でC#に必要な明示キャストを追加します。

Enum値を使う`Select Case`は対応しますが、ユーザー定義変換など比較動作を確定できないCase句は`ManualReviewRequired`になります。

## 9. VB Application Framework

次は完全自動変換の対象外です。

- `Application.myapp`
- `My.Application`
- `My.Forms`
- SingleInstance
- SplashScreen
- ShutdownMode
- VB Application Frameworkが生成するエントリポイント

該当Projectは`UnsupportedApplicationFramework`または`StartupObjectUnresolved`として記録されます。

## 10. COM参照

COMReferenceとCOMFileReferenceのProject情報は可能な範囲で維持しますが、以下は自動化しません。

- COMコンポーネントのインストール
- COM登録
- ActiveX Wrapperの再生成
- 32bit／64bit互換性判断

COM参照はManualReviewRequiredになります。

## 11. Wildcard Project Item

次のようなWildcard指定は、ファイル集合を安全に確定できないためレビュー対象です。

```xml
<Content Include="Data\**\*.*" />
```

Project XMLの指定自体は維持しますが、Wildcard展開による全ファイルコピーは行いません。

## 12. Project外リンクと外部DLL

Project外のリンクファイルやHintPath DLLは出力ルート内の安全な場所へコピーし、Include／HintPathを更新します。VB CompileのLink項目は、Projectごとの論理パスへC#として個別出力し、同じフォルダーへ配置したresxのDependentUponを検証します。外部ファイルをコピーした事実は`ExternalLinkedFile`としてレビュー記録される場合があります。

参照ファイルが存在しない場合は`MissingReference`または`MissingContentFile`になります。

## 13. NuGet

`packages.config`と既存HintPathは維持しますが、ツール自身はNuGetパッケージの復元保証やパッケージ形式の変換を行いません。生成SolutionのMSBuild時に必要なパッケージが存在しない場合、ビルド検証が失敗します。

## 14. Frameworkとビルド環境

対象FrameworkのDeveloper Pack、Reference Assemblies、Visual Studio Build Tools、カスタムtargetsが存在しない環境では、MSBuildWorkspaceのロードまたは最終ビルドが失敗することがあります。

ビルドを別環境で行う場合は`--skip-build`を指定できます。

## 15. 既存出力

同じ出力ディレクトリを再利用すると、対象ファイルは再生成または上書きされます。以前の実行で生成され、今回の入力には存在しない古いファイルを自動削除する処理はありません。クリーンな出力ディレクトリの利用を推奨します。

## 16. 非VBプロジェクト

Solution内のVBプロジェクトが主な変換対象です。C++、セットアップ、データベースなどの別Project形式をC#へ変換する機能はありません。Solution全体に特殊Projectが含まれる場合は、生成Solutionの参照パスとビルド結果を確認してください。
