# RaidLab 修正検証フィードバック（`f7f60db` 検証・多プロジェクト実使用）

基点: main `f7f60db`（外部エージェントによる RaidLab 修正。`docs/raidlab-measurement-fixes-2026-10-18.md` の A/B/C）。
前稿 `docs/validation-feedback-2026-10-18-raidlab.md` の問題1〜3に対する修正を、実機で通しで検証した。

## 検証したこと

- `f7f60db` をプル。**ビルドが 1 箇所で失敗**したため修正（後述）後、`dotnet build`/`dotnet test` を実行。
- テスト: `tests/TenkaChan.Tests` **742/742 合格**（前回 730、+12）。
- ツール数: **44**（`tenka_measure_companion` 追加）。
- 接続スモーク（`tests/Fixtures/agent_protocol_smoke.py`）: `mcpTools=44`, `duplicateRequestExecutedOnce=true`, `modelInference=false`, `targetExecution=false`。
- `Tenka.Sample.RaidLab` で最適化を1件通しで実施:
  `RaidAnalyzer.Analyze` を `List.Contains`/LINQ 走査（O(n²)）から `Dictionary`/`HashSet` 集計へ変更。
  契約テスト（Tests コンパニオン相当を CLI で）**46/46 合格**。

## 修正 A/B/C の判定

- **A（生成実験の多プロジェクト参照, `TrustedExperimentReferenceClosure`）**: 今回の経路では A を直接は通していない（後述の allowlist 制約でソリューション open ができず）。ただし `f7f60db` はビルド不能だったため、修正のコンパイルを直さないと検証できない状態だった（下記）。
- **B（`prepare_experiment` の `investigationId` と resume 導線）**: `resume`→`tenka_context`→再承認の導線は動作。エラー時の `nextAction` も具体。
- **C（`tenka_measure_companion` + `mark_review` 添付）**: **動作確認済み**。既存 BenchmarkDotNet コンパニオンをビルド/実行し、実測レポートを before/after として `mark_review` に添付できた（下記の実測・マーク）。前稿の問題3（実測はできるが記録に添付できない乖離）は解消。

## 実測（`tenka_measure_companion`, `benchmarkJob=short`, net8.0, Ryzen 9 9900X）

investigation `682524b0…` / baseline `bbd98e4f…` / candidate `faef0f61…`。

| case | baseline mean | candidate mean | 改善 | 割り当て |
|---|---|---|---|---|
| `AnalyzerBenchmarks.Analyze(Players=40)` | 767,585 ns | 98,153 ns | 約 7.8× | 1,335,744 → 187,248 B |
| `AnalyzerBenchmarks.Analyze(Players=200)` | 1,433,401 ns | 124,591 ns | 約 11.5× | 1,405,216 → 294,112 B |
| `PipelineBenchmarks.Generate()` | (baseline 2,259,729 ns) | 1,371,979 ns | — | 2,192,744 → 1,052,424 B |

`tenka_mark_review`（status=optimized, revision 3）に measure_companion の evidence 2件を添付。
`performance.hasMeasurements=true, comparedCases=3, incomparableCases=0`。

## 新たに観測した問題（改善候補）

1. **[重大] `f7f60db` がコンパイル不能。**
   `src/TenkaChan.Web/AgentGateway.Investigations.cs:144` の `_ = await ReloadAsync();` が **CS0029**。
   `ExploreAsync` のラムダ `(index, _)` の第2引数（`RoslynReviewService`）にタプルを代入してしまう。
   `await ReloadAsync();` に修正して解消。CI 停止中のため気づかれていない可能性が高い。`f7f60db` は
   「Windows .NET build/xUnit/live RaidLab verification is pending」と明記しており、その未検証がそのまま出た形。

2. **[重大・既存] ソリューション open が NuGet キャッシュ内ドキュメントで拒否される。**
   `Tenka.Sample.RaidLab.slnx`（Tests プロジェクトが `Microsoft.NET.Test.Sdk` を使用）を
   `tenka_open_project` すると、MSBuildWorkspace が
   `C:\Users\...\.nuget\packages\microsoft.net.test.sdk\17.14.1\build\net8.0\Microsoft.NET.Test.Sdk.Program.cs`
   をドキュメントとして列挙し、`RoslynReviewService` が `RequireFile` で
   許可ルート外として全体を失敗させる。Tests プロジェクトを一度 restore/build した後に表面化する。
   回避策は **`.csproj` を起点に open** すること（今回は Benchmarks.csproj で成功）。
   テストプロジェクトを含むソリューション題材では実質 open 不可なので、実害が大きい。
   提案: 許可ルートに NuGet キャッシュを加える、またはパッケージ由来ドキュメント（`File` が許可ルート外でも
   `IsGenerated`/パッケージ由来）を除外する。

3. **[軽] `benchmarkJob=short` の添付は `status=insufficient_samples`。**
   `sampleCount=3` のため比較ステータスが `measured_comparison` にならない。仕様どおりだが、
   既定 `medium` との差が分かるよう、`mark_review` 応答や `performance` にジョブ種別があると親切。

4. **[軽・DX] MCP ツールで引数型エラー時に表層メッセージが汎用。**
   `expectedRevision` を誤って `null` で送ると、クライアントには
   `An error occurred invoking 'tenka_mark_review'.` としか出ず、実体
   （`JsonException: ... could not be converted to System.Int64`）はサーバ stderr にしか出ない。
   引数バインディング失敗も構造化エラー（`tool_failed` 相当）で返してほしい。

## 検証の限界（未確認）

- 問題1は自明なコンパイルエラーの修正のみ。修正の意図（B/C）の網羅的な回帰は未実施。
- 修正 A（生成実験の多プロジェクト参照）は、問題2のためソリューション経路で未検証。`.csproj` 経由の
  `measure_companion` のみ確認。
- 実測は単一マシン・`short`（`sampleCount=3`）。有意差の根拠にはしない。
- マーク履歴には調査中のダミー revision（1–2）が残っている（cleanup は未実施）。
- CI 停止中、Docker 未使用。

## 改善提案

- `f7f60db` のコンパイル修正を main に取り込む（`AgentGateway.Investigations.cs:144`）。
- 問題2の allowlist 対応（NuGet キャッシュ/パッケージ由来ドキュメントの扱い）。テスト付き題材の前提を満たすために必須。
- `mark_review` 応答および `performance` に benchmarkJob を露出。
- MCP の引数バインディング不整合を構造化エラーで返す。
