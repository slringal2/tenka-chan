# ReviewTargetの型名衝突によるビルド失敗の修正

ユーザーのWindows実ビルドで、SourceExplorerIndex.Review.csからCS1729、CS1061、CS0117、CS1503の計7エラーが報告されました。SDKやNuGetの復元不足ではなく、こちらのソースにある名前解決の問題です。

Analysis.ReviewTarget（静的な調査優先度）とTracking.ReviewTarget（確認記録）は別の型です。元のファイルは名前空間の外側で同名のusing ReviewTargetエイリアスを宣言していましたが、内側のAnalysis名前空間にあるReviewTarget型が優先されました。このため、14引数の生成、SourceHash等の読み書き、ReviewSourceCatalogへ渡す一覧がすべて誤った型になっていました。

エイリアスをTrackedReviewTargetという衝突しない名前に変え、global::TenkaChan.Mcp.Tracking.ReviewTargetを指定しました。一覧の要素型と生成するレコードの両方にこのエイリアスを使います。既存の型定義・API・保存データ形式は変えていません。チェック履歴や設定を削除する必要はありません。

起動スクリプトの一般的なビルド失敗メッセージも修正し、CS系はソースと参照の整合性確認、NETSDK1004等は復元、MSB3027/MSB3021は使用中DLLという対処を区別しました。コンパイルエラーに対して一律にsetup.batを再実行する案内は外しています。プロセスの自動終了やCIの再開はしていません。

## 更新

甜花ちゃん本体と外部MCPアダプターを終了し、本体フォルダーで次を実行します。

```powershell
git pull --ff-only
.\start.bat
```

今回の7エラーに対してNuGet復元、SDK再インストール、bin/obj削除は必要な修正ではありません。別途依存関係不足が報告された場合はそのエラーに合わせて対応してください。

## 検証した範囲

修正前ファイルが現在のGitHubのblobと一致することを確認し、必要箇所だけ変更しました。Node.jsのソース形状検査5件は実行し、成功しました。元の衝突するエイリアスに戻す場合、一覧だけ・生成箇所だけを戻す場合の取りこぼしも検査しています。Git差分の空白検査も成功しています。

C#には、同名エイリアスと異なるエイリアスの実際のRoslyn名前解決2ケース、実際のSourceExplorerIndexから型とメソッドの確認記録を作る1ケースを追加しました。これら3ケースは未実行です。この環境には.NET SDKとPowerShellがなく、SDK取得先のDNS解決も失敗したため、本体のC#ビルドやWindows起動の成功は確認できていません。ソース形状検査をコンパイル成功とは扱いません。

```powershell
# 依存関係を準備し、甜花ちゃんとMCPアダプターを終了した環境で実行
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter FullyQualifiedName~ReviewCatalogTypeBindingTests
```

参考: C#名前空間仕様のエイリアスと入れ子の名前解決
https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/namespaces
