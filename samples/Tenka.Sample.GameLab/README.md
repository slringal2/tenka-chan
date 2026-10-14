# GameLab — 甜花ちゃんの比較教材

まずWebの **使い方・サンプル** (`/guide.html`) を開いてください。サンプルを選択して準備画面に進むと、パスが入力されます。ページを開くだけでビルドや実行は行いません。

## 構成

本体 `Tenka.Sample.GameLab.csproj` は .NET 8 のコンソールアプリです。外部NuGetパッケージは使いません。比較するメソッドを同じ実行DLLに含め、既存のJITメソッド選択と組み合わせられる構成です。Tests（xUnit）とBenchmarks（BenchmarkDotNet 0.15.8）は別プロジェクトで、本体の既定のCompileから明示的に除外しています。

| 題材 | ベースライン | 改善候補 | 主な確認画面 |
|---|---|---|---|
| 所持アイテム検索 | 毎回ToList + 線形検索 | HashSetを1回構築（構築コストも測定に含む） | 検出候補・決定論的解析・性能ホットスポット |
| 戦利品ログ | ループ内string += | StringBuilder | 割り当て・ベンチマーク |
| ダメージのバケット集計 | 動的除数による剰余 | 2のべき乗という契約下でマスク | JIT・IL・ベンチマーク |
| 共有カウンター | lock内で独立した仕事と加算 | 独立作業を外に出して集計 | ランタイムのMonitor競合 |
| 非同期読み込み | Result / Thread.Sleep | await | 静的解析専用（エントリーポイントから呼ばない） |

静的指摘は性能劣化やバグの確定ではありません。改善候補にも割り当てやループがあり、警告がゼロになることは要求しません。想定する入口はPERF1001、PERF1002、ASYNC1001、ASYNC1003ですが、検知件数の固定値は約束しません。

## いちばん簡単な実行

`run-lab.bat` をダブルクリックします。1は参考計測、2は35個の比較・確認、3はxUnit、4はインベントリのBenchmarkDotNet比較です。選択した処理のみ実行し、失敗時は終了コードを返します。ウィンドウは結果確認まで残ります。DockerとCIは使用しません。SDKと.NET 8ランタイムが必要です。

```powershell
# このサンプルフォルダーで実行
# 正しさの簡易確認（外部パッケージ不要）
dotnet run -c Release -- --verify
# xUnit。初回はテスト依存パッケージを復元します
dotnet test Tests/Tenka.Sample.GameLab.Tests.csproj -c Release
# 1回あたりの時間と割り当て量。通常は数分かかります
dotnet run --project Benchmarks/Tenka.Sample.GameLab.Benchmarks.csproj -c Release -- --filter '*InventoryBenchmarks*' --job short --exporters FullJSON
```

`--verify` は実装を呼んで比較し、失敗なら0以外で終了します。これだけで全入力に対する等価性を証明するものではありません。xUnitでは空配列、重複、負数、上限付近の値、不正な引数、並列更新を追加で確認します。

## JIT

本体をReleaseビルドし、甜花ちゃんのJIT取得に `Tenka.Sample.GameLab.dll` を指定します。引数なしの起動はInventorySearch、LootReport、DamageBucketsの両版を実行します。例えば `DamageBuckets:SumModulo` と `DamageBuckets:SumMask` を別々に取得します。NoInliningを教材の入口メソッドに付けていますが、NoOptimizationは付けません。

マスク版は「非負の入力」「正の2のべき乗のバケット数」に限定し、両版とも同じ入力検査を行います。一般の符号付き剰余を置き換える例ではありません。命令の具体形・速度差は実際のランタイムとCPUで確認してください。

## トレース

Webのプロファイル取得でDLLと既存の `profiles` フォルダーを指定し、詳細ランタイムイベントを有効にして明示承認後に実行します。取得全体の制限時間は60秒を目安にします。

```json
["--scenario","inventory","--mode","baseline","--seconds","4"]
```

改善版は `baseline` を `optimized` に変えます。ロック競合はシナリオを `contention` に、サイズを300程度にします。4秒方式は同一処理数の比較ではありません。イベントの有無や種類を調べる用途で、総割り当て量から1回あたりの改善率を算出しません。GCが出ることや、すべての競合が捕捉されることは保証しません。

固定回数は `--iterations` を使用し、`--seconds` と併用できません。baseline/optimizedの固定回数モードは同じチェックサムだけを標準出力に出します。データは時刻や乱数に依存しません。ファイル・ネットワークへのアクセスはサンプル本体では行いません（ビルドや依存パッケージの復元は別です）。

## 甜花ちゃんの生成実験

`/guide.html` には貼り付け可能なxUnitとBenchmarkDotNetコードを掲載しています。付属のTests/Benchmarksプロジェクトを実行する方法と、甜花ちゃんが作る別の実験を実行する方法は区別してください。生成実験のホスト実行も隔離なしなので、コード確認と各回の承認が必要です。

## 測定を読むとき

引数なしの比較はウォームアップ1回後のStopwatchとプロセス全体の割り当て差を表示する参考値です。起動順の影響・他のランタイム処理を含みます。BenchmarkDotNetによる定常状態の検証や統計的な有意差の証明ではありません。速度改善の数値を埋め込んでいません。
