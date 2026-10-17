# RaidLab 実使用フィードバック（多プロジェクト題材）

基点: main `149edda`（PR #26 マージ後）。この開発環境の実機で甜花ちゃんを実際に使用した観測。
GameLab（単一アセンブリ）ではなく、6プロジェクト構成の `Tenka.Sample.RaidLab` を対象にした。

## この環境で実際に実行したこと

- `start.bat` で起動し、接続スモーク（`tests/Fixtures/agent_protocol_smoke.py`）を実行。
  `mcpTools=43`、`duplicateRequestExecutedOnce=true`、`modelInference=false`、`targetExecution=false`。
- `tenka_open_project`（`Tenka.Sample.RaidLab.slnx`）→ 6プロジェクト / 141メソッド。snapshot は都度更新された。
- `tenka_review_queue {kind:all}` → 未確認 93件（`hasMore=true`、`nextCursor` あり）。
- `tenka_review_context`（`RaidAnalyzer.Analyze`、targetId `17AF8E06…`）→
  selection / state / source / markBasis / binary と `contextFingerprint v2`（7成分, `complete=true`）を1回で取得。
- `tenka_run_companion`（Benchmarks コンパニオン）→ `benchmark_completed`。
- `tenka_run_companion`（Tests コンパニオン）→ `tests_passed`（46/46）。

## 実測ベースライン（`tenka_run_companion`、net8.0、Ryzen 9 9900X）

| case | mean | 割り当て | sampleCount |
|---|---|---|---|
| `AnalyzerBenchmarks.Analyze(Players=40)` | 746,233 ns | 1,335,744 B | 3 |
| `AnalyzerBenchmarks.Analyze(Players=200)` | 1,386,336 ns | 1,405,216 B | 3 |
| `PipelineBenchmarks.Generate()` | 2,259,729 ns | 2,192,744 B | 3 |

`environmentFingerprint` 付き。`sampleCount=3` は統計的に弱く、有意差の根拠にはしない。

## 観測した問題（改善候補）

1. **[重大] 生成実験が被参照アセンブリの型を使えない。**
   `tenka_prepare_experiment`（kind=benchmark）→ `tenka_measure_target` の経路で、
   生成 benchmark が `Tenka.Sample.RaidLab.Domain` の型（`RaidDataset` 等）を参照すると
   `CS0234`/`CS0246` で `build_failed` になった。
   原因は [TrustedHostExperimentExecutor.cs](../src/TenkaChan.Mcp/Experiments/TrustedHostExperimentExecutor.cs) の 56–62行で、
   `ProjectReference` を削除して**対象DLL1本の `Reference` だけ**に差し替えるため、
   対象アセンブリが公開APIで被参照アセンブリの型を使う場合にコンパイルできない。
   結果として、**多プロジェクト題材では `measure_target`／`mark_review` の実測添付が機能しない**。
   単一アセンブリの GameLab では従来どおり動作した実績があり、構成依存の制約とみられる。

2. **[中] 調査（investigation）所有が分離実行で噛み合わない。**
   `prepare_experiment` と `measure_target` を別々のアダプター起動で行うと
   `experiment_investigation_mismatch`。エラーと `nextAction` は具体的で分かりやすいが、
   初見では踏みやすい。同一呼び出しで束ねれば通る。

3. **[中] 実測と記録の途切れ。**
   `run_companion` の実測結果IDは `%LOCALAPPDATA%\tenka-chan\experiments` に保存されず
   （`run_companion` は生成実験を作らない）、`mark_review` の
   `baselineExperimentId`/`candidateExperimentId` に渡せない。
   「実測はできる（companion）／記録に添付できない（生成実験前提）」の乖離が起きる。

4. **[軽] 表示。** ビルド/テスト出力の日本語が PowerShell 経由で文字化けする（表示のみ）。

## 検証の限界（未確認）

- 問題1は、1回の `build_failed` ログとコード読解に基づく。単一アセンブリ題材での成功実績との対比はあるが、
  被参照型を使う生成実験が成功する条件を網羅的に試してはいない。
- 測定はコンパニオン経路で行った。`measure_target` 経由の添付（baseline/candidate を持つ `mark_review`）は未完了。
- 今回の実行で記録した mark は無い（`review-progress.sqlite` は消去済みのまま）。
- CI は停止中、Docker は未使用。速度値はこのマシンの単一環境の参考値。

## 改善提案

- 被参照プロジェクトの出力DLLも `Reference` に追加するか、推移的 `ProjectReference` を保持する。
- `prepare_experiment` と `measure_target` の同一呼び出しグルーピング、またはエラー時の導線。
- `run_companion` の実測を測定添付（`mark_review` の baseline/candidate）に使えるようにする。
