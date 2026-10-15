# RaidLab — 複数プロジェクトで性能調査を練習する

既存のGameLabより一段複雑な、6プロジェクト・.NET 8の自作教材です。外部OSSをコピーしたものではありません。
「戦闘ログを読み、重複や不正イベントを除き、プレイヤーとギルドのランキングを返す」処理を題材にしています。

## 最初にすること

甜花ちゃん本体ルートの **run-raid-lab.bat** をダブルクリック → **1（初回準備）** → 内容を確認して **Y**。
6プロジェクトのパッケージを復元し、実行アプリをReleaseビルドして、仕様テストを実行します。処理が失敗したら失敗として表示し、画面とログを残します。初回復元にはネット接続が必要な場合があります。

通常はWindows用の.NET 8ランタイムと.NET 8以上のSDKが必要です。本体setup.batでSDK10とSDK8を導入した環境でも使えます。サンプルのglobal.jsonは8以上へroll-forwardする設定です。SDKを確認するだけで勝手にインストールはしません。

甜花ちゃん側は **プロジェクト調査 → 準備 → 付属サンプル「RaidLab（実践・6プロジェクト）」→ 選んだサンプルを使う → 読み込む**。
このボタンは固定のソリューションパスを設定するだけで、ビルドや実行を自動開始しません。
起動用・ビルド用の対象は **Tenka.Sample.RaidLab.Host**。LibraryであるApplicationを選んだまま直接起動しないでください。

`Tenka.Sample.RaidLab.sln` と `.slnx` は同じ6プロジェクトを含みます。BATはSDK8でも扱える.slnを使います。

## 構成と呼び出しの流れ

| プロジェクト | 役割 |
|---|---|
| Domain | DTO、入力契約、集計結果、IRaidAnalyzer／IRaidRepository |
| Infrastructure | 再現可能な戦闘データ生成、メモリ上のJSONリポジトリ |
| Application | 基準実装／改善候補、非同期レポートサービス、並列数を制限したバッチ |
| Host | 実行入口、負荷サイズ・計測条件、パッケージ不要の仕様テスト |
| Tests | xUnitで56ケースの仕様テスト。テストロジックはHostと共用 |
| Benchmarks | 集計だけ／JSON～レポート全体を分けたBenchmarkDotNet比較 |

Host → Application → Domain、Host → Infrastructure → Domain。
TestsとBenchmarksもApplication／Infrastructureを参照します。ソリューション全体の参照検索でこの関係を追えます。
JSON読込 → 入力検査 → 重複除外 → スキル照合 → ダメージ加算 → 全体ギルド集計 → 上位プレイヤー → レポート生成が一本の処理としてつながっています。

## 何を調べるか

| 入口 | ベースラインの調査点 | 改善候補／確認ポイント |
|---|---|---|
| BaselineRaidAnalyzer.Analyze | イベントごとのToList/ToArray、List.Containsによる重複検査、プレイヤーごとの再走査 | IndexedRaidAnalyzer.AnalyzeのDictionary/HashSet、一回の走査。構築コストも含む |
| ReportFormatter.BuildBaseline | ループ内のstring += | BuildOptimizedのStringBuilder。同一文字列か仕様テスト |
| RaidReportService.GenerateAsync | JSONデシリアライズを含めるとボトルネックがどう変わるか | 単体集計だけ速くしても全体速度が同率で改善するとは限らない |
| RaidBatchService.GenerateAsync | 並列数1と4で結果や割り当て・スケジューリングの違いを見る | 並列化で必ず速くなるとは主張しない |
| LegacyEndpointExamples.BlockingAsync | Result／Thread.Sleep | 静的解析専用。Host・テスト・ベンチからは呼ばない |

まず **BaselineRaidAnalyzer.Analyze → 参照を探す → IL → JIT** が入口です。通常起動は両方のAnalyzerを実行するので、それぞれのJIT対象を選べます。asyncメソッド本体は状態機械に変換されるため、最初は同期のAnalyzeを選んでください。

## 計測と正しさを区別する

BATの **2** は仕様テスト、**3** はStopwatchによる参考比較、**6** はxUnit、**7/8** はBenchmarkDotNet。
まず2または6で正しさを確認し、その後7で集計単体、8で全体を比較します。
56ケースの定義には、手計算済みの正解、先勝ち重複、未知参照、装備不一致、同点順序、上位切出し後も保持すべきギルド合計、整数上限、キャンセル、JSON異常、差分テスト27条件を含めています。

小さい負荷は `powershell -File .\run-lab.ps1 -Mode Compare -Size Small`、大きい負荷は `-Size Large`。負荷はそれぞれ20人/500件、通常200人/6000件、大800人/20000件。
BATの4/5は対象アプリを動かすだけです。トレースファイルを取得する操作ではありません。

甜花ちゃんで実測する場合は **実行・検証 → 詳細設定 → RaidLabの起動プリセット → プロファイル取得**。
ベースライン／改善候補／4並列バッチの4秒プリセットを追加しています。保存先の追加入力は不要です。
固定時間方式は処理回数が異なるので、総割り当てをそのまま「1回の削減率」としないでください。1回あたりはBenchmarkDotNetで比較します。

数値はハードコードしていません。プロセス全体の参考割り当てにはランタイム・タスク等の処理が混じります。短いBenchmarkDotNetジョブも予備測定であり、有意差の証明ではありません。

## 外部エージェントへの依頼例

`tenka_sample(sample="raid-lab")` でソリューションと起動プロジェクトを取得できます。省略時は従来のGameLabです。

「RaidLabを読み込み、BaselineRaidAnalyzerの反復処理と割り当てを調べてください。CONTRACT.mdを期待値の根拠にし、IndexedRaidAnalyzerとの正しさ・性能を確認してください。実測なしで高速化を断定せず、確認した版にチェック済みと確認範囲を記録してください。」

## 制限とログ

これは本番サーバーや外部OSSではなく、意図したボトルネックを持つ教材です。サンプル本体はファイル／ネットワーク／DBを読み書きしません。全JSONはメモリ上です。ただしビルド・パッケージ復元・テスト／ベンチマークツールはファイルやネットワークを使います。
BATはこのフォルダーのartifactsにログを保存します。ソース削除、git reset、プロセス強制停止、CIの作成・有効化はしません。Docker不要。中止はCtrl+C、長い対象処理には90秒の協調的制限があります。

作業環境では.NET SDK/PowerShellを起動できていないため、C#コンパイル、56ケースの実行、WindowsのBAT、JIT・実トレース・BenchmarkDotNet実測は未確認です。手元で実行された結果を成功・失敗の判断に使ってください。UI/構成検査の詳細は`docs/raid-lab.md`を参照してください。
