# 生成したテスト・BenchmarkDotNetを変更候補の比較に使う

2026-10-16。PR #20の `5c63f5d` に続く変更。
既存の `tenka_prepare_experiment` を `tenka_review_workflow` に接続しました。
新しいMCPツールや依存パッケージは追加していません。公開ツールは44個のままです。

## 利用手順

対象のソースを確認し、`tenka_propose_change` で候補を作成します。
既存の検証プロジェクトがない場合は、同じ調査で次を用意します。

```text
tenka_prepare_experiment {
  "kind": "test",
  "projectId": "対象プロジェクトID",
  "targetFramework": "net8.0",
  "generatedCode": "仕様を確認したうえで外部エージェントが作成するxUnitコード"
}

tenka_prepare_experiment {
  "kind": "benchmark",
  "projectId": "同じ対象プロジェクトID",
  "targetFramework": "net8.0",
  "generatedCode": "対象の公開APIを呼ぶBenchmarkDotNetコード"
}
```

上は引数の説明であり、generatedCodeの文字列をそのまま実行できるサンプルではありません。
`prepare_experiment` がcsproj・必要なPackageReference・ベンチ起動用Program.csを作成します。
BenchmarkDotNetのバージョンは既存テンプレートの0.15.8を再利用し、新しい版へ勝手に変更しません。
テストの期待値・入力・測定対象は外部エージェントが仕様を確認して書くものです。
甜花ちゃんが正しい期待値や意味的同値性を自動で導く機能ではありません。

返った2つの実験IDを次に渡します。**先にtenka_run_experimentを呼ぶ必要はありません。**

```text
tenka_review_workflow {
  "proposalId": "候補ID",
  "mode": "validate",
  "testExperimentId": "testの作成結果のid",
  "benchmarkExperimentId": "benchmarkの作成結果のid",
  "benchmarkFilter": "*",
  "approved": true
}
```

これにより、元プロジェクトにテスト用／ベンチ用csprojを手で追加しなくても、候補コピーで比較できます。
`mode=inspect` の対象準備応答には、生成に渡すsnapshotId/projectIdと手順も追加しました。
読み込み前提・ホストの実行許可・外部クライアント側の個別承認は従来どおり必要です。

既存テスト＋生成ベンチ、生成テスト＋既存ベンチも使えます。
種類ごとに `testCompanionId` と `testExperimentId`、`benchmarkCompanionId` と `benchmarkExperimentId` は排他的です。
どちらも省略した種類については、従来どおり唯一の既存companionを選択します。
存在しない場合や複数候補の場合に、直近の生成実験を無断で選んだり空のテストを生成したりはしません。

## 実行対象の取り違えを防ぐ仕組み

生成実験は、候補と同じ対象プロジェクトのものに限定します。
別のプロジェクトへ生成コードだけを無言で付け替えることはしません。
実験の元コード・csproj・Program.csのハッシュを検査してから、候補側へ固定します。

候補の `generated/test/{baseline,candidate}` と `generated/benchmark/{baseline,candidate}` に独立ファイルを作ります。
元実験のDLLや古い計測結果を参照するのではなく、各生成プロジェクトのProjectReferenceを
それぞれの **baselineコピー／candidateコピー内の対象csproj** に向けます。
各コピーをビルドして計測するので、元の作業フォルダーを途中で書き換える必要はありません。

両側で共通なのはGenerated.cs、起動コード、PackageReference、選択した対象／実験フレームワークです。
参照先のコピーと出力先だけを分けます。追加された任意のC#ファイルを既定のglobで拾わないよう、Compile対象も明示します。
対象csprojの内容を.NET 10へ変更せず、保存されたSubjectTargetFrameworkとRuntimeTargetFrameworkを使います。
.NET Standardを使う場合もライブラリのターゲットと実験ランタイムを分離します。
対象近傍のglobal.jsonを生成側にも固定し、ネストしたSDK選択を落とさないようにしています。
フレームワーク選択やProjectReferenceの実SDKでの挙動は、後述のとおりこの作業環境では未検証です。

生成側のファイル一覧・内容ハッシュを検証前後と適用前に再確認します。
検証後に生成テスト、測定コード、起動コード、csproj、NuGet.Config、SDK設定が変わった候補は適用できません。
測定に使ったソースコピーも、適用前に再確認します。
これらのハッシュは事故防止の整合性検査であり、ローカルの同一OSユーザーによる署名偽造等を防ぐ認証機構ではありません。

## 再開と結果

検証段階は従来どおり変更前テスト→変更後テスト→変更前ベンチ→変更後ベンチです。
実験ID・フィルター・生成ファイルは最初の検証開始時に固定されます。
続行時は次だけで保存した選択を利用できます。

```text
tenka_review_workflow {"proposalId":"同じ候補ID","mode":"validate","approved":true}
tenka_review_workflow {"proposalId":"同じ候補ID","mode":"result"}
```

同じコードを持つ別の実験IDであっても、開始後に選択を入れ替えることはできません。
再開時には保存された実験IDの調査所有権も再確認します。
新しいセッションでは、先に既存のinvestigationを明示的にresumeしてください。

完了済み段階を再実行せず、開始記録だけ残った段階も自動再試行しません。
既に固定した検証は元の実験フォルダーから再生成せず、候補内の固定済みファイルを使います。
元実験の古いexecution-report.jsonはコピーも比較もせず、候補側の今回のレポートだけを扱います。

validate応答と保存したvalidationに、TestProject/BenchmarkProject、GeneratedTest/GeneratedBenchmark、フィルターを残します。
MCPのJSONではcamelCaseです。生成選択のプロジェクト表示は `generated:<kind>:<experimentId>` になります。
生成記述には対象／実験TFM、コード・テンプレート・起動コードのハッシュと固定ファイル一覧が入ります。
旧companion経路や旧レシートの追加フィールドはnullです。既存の引数名・既定値・位置引数の順序は変更していません。

## 保持した条件と制限

テストは非ゼロ・全件成功・スキップなし、ベンチはケース・環境・観測数を確認します。
対象メソッドが実際に十分にカバーされることは自動で証明できません。
終了コードやテスト成功だけで意味的同値性、統計的有意差、本番の速度改善を断定しません。
比較の完了後も適用・取り消し・レビュー記録は別の明示的操作です。

コピーや生成プロジェクトはOSサンドボックスではありません。
MSBuild・テスト・ベンチは現在のユーザー権限で動きます。
復元は既存のキャッシュを使うオフライン設定を適用しますが、任意の実行コードのネットワーク／ファイル操作を遮断しません。
必要なSDK／ランタイム／パッケージがない場合は準備が必要です。

単一ファイル候補、コピー範囲、動的／外部のビルド入力など従来の制限は残ります。
MSBuild特殊文字を含む参照パスは、誤解釈させるのではなく現在は未対応として停止します。
ハッシュの揃っていない旧世代の生成実験は、prepare_experimentから作り直してください。
生成用の固定ファイルが中途半端に残った場合、同じ内容は補完できますが、異なる内容は強制上書きしません。

## 検証記録

この作業環境で実行したもの：

- `python scripts/test-generated-proposal-contract.py`：ソース構造テスト5件成功。うち1件で保護を壊す10種類の変更を検出。
- Python構文チェック、C#ファイルの字句的な括弧対応、空白チェック。
- 編集した既存4ファイルは、変更前にGitHubのblob SHAと完全一致することを照合。

追加C#テストは `ProposalValidationWorkflowTestsForGeneratedExperiments` の33ケースです。
既存check-tenkaの `FullyQualifiedName~ProposalValidationWorkflowTests` フィルターに含まれるクラス名です。
同一生成コード・コピー先参照・生成/既存混在・所有権・誤った種類/プロジェクト・改変・再開・不明な開始記録・フレームワーク/SDKの固定を検査します。

**C#コンパイル、33ケースの実行、実SDK/MSBuild/BenchmarkDotNet、MCPの実接続、Windowsでの動作は未実施です。**
この作業環境に.NET SDK/PowerShellはなく、SDK配布先等の名前解決もできませんでした。
C#の偽プロセステストも未実行です。Pythonソース検査を実行確認の代わりには扱いません。

.NET 10 SDKと復元済み依存関係がある環境で、本体/MCPアダプターを終了して実行します。

```powershell
dotnet test tests\TenkaChan.Tests\TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ProposalValidationWorkflowTests" --logger "trx;LogFileName=generated-proposals.trx"
```

CIは再開していません。通常エージェントと比較した調査時間の改善も、この変更ではまだ計測していません。
`docs/investigation-proposals.md` にある「生成実験の候補比較への接続は残作業」という記載は本変更で更新されます。

参考にした一次資料：
- https://benchmarkdotnet.org/articles/guides/how-to-run.html
- https://github.com/dotnet/msbuild/blob/main/documentation/ProjectReference-Protocol.md
