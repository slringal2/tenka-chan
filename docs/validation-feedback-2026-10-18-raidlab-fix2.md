# RaidLab 修正検証フィードバック 第2回（`70df106` の検証と分担の明確化）

基点: main `70df106`（前回フィードバックへの対応コミット）をプルして検証。検証後の本リポジトリ状態は `e74d25d`。
前稿: `docs/validation-feedback-2026-10-18-raidlab.md`, `docs/validation-feedback-2026-10-18-raidlab-fix.md`。

## 役割分担（本ラウンドで確定）
- **ビルド健全性は本ローカル側（この検証・修正を行っている側）が担当して直す。**
  外部エージェントのコミットが警告なしにビルド不能でも、こちらで修正して通す。
- **使いやすさ（ツールの DX）は双方で分担する。**
  ローカル側で完結できる改善は本リポジトリに実装してプッシュし、外部側にしか直せないものはフィードバックとして返す。
- CI は引き続き停止（`[skip ci]`）のため、ビルド健全性の自動検出は無い前提で運用する。

## 検証したこと
- `git pull` + `git reset --hard origin/main` → `70df106`。`dotnet build` / `dotnet test` を実行。
- **`70df106` はそのままではコンパイル不能**。`src/TenkaChan.Mcp/Program.cs:46` で `failure` パターン変数が
  31行とスコープ衝突（CS0136）。ローカル側で `inputFailure` に改名して解消（`656da51`）。
- テストは **752/753**。新修正自身の回帰テスト
  `NuGetSdkGeneratedDocumentTests.OpensMultiProjectSolutionWithoutIndexingExternalSdkSyntheticSource` が失敗。
  原因は `Documents == 1` 固定で、当環境では SDK 生成ドキュメントまで数えて `App:3 | Tests:3` になる（SDK 依存）。
  ローカル側で「除外の意味論で検証」する形に頑健化（`70c7e87`）→ **753/753**。

## 上流で解消済み（前回フィードバックの反映）
- **[解消] NuGet synthetic ドキュメントによるソリューション open 失敗。**
  `NuGetSdkGeneratedDocumentPolicy` で既知の `Microsoft.NET.Test.Sdk.Program.cs` のみを ANALYSIS から除外し、
  allowlist を広げない形で解決。実地で **`Tenka.Sample.RaidLab.slnx` の直 open が成功**（6プロジェクト、
  Tests は synthetic 1件除外の警告つき）。
- **[解消] MCP 引数エラーの表層化。** `AgentToolErrorAdvice` + `Program.cs` の request filter で、
  誤用時に `{code, message, nextAction}` の構造化エラーが返る（検証中に実確認）。

## 実地検証（`Tenka.Sample.RaidLab`, `benchmarkJob=short`, net8.0, Ryzen 9 9900X）
`RaidAnalyzer.Analyze` を `List.Contains`/LINQ 走査（O(n²)）から `Dictionary`/`HashSet` 集計へ変更。契約テスト 46/46。

| case | baseline | candidate | 改善 |
|---|---|---|---|
| `Analyze(Players=40)` | 786,445 ns | 99,683 ns | 約 7.9× |
| `Analyze(Players=200)` | 1,434,342 ns | 129,062 ns | 約 11.1× |
| `PipelineBenchmarks.Generate()` | 2,428,734 ns | 1,318,532 ns | 約 1.8× |

割り当て: `Analyze(40)` 1,335,744 → 187,248 B、`Analyze(200)` 1,405,216 → 294,112 B。
別 investigation での再実行でも `Analyze(40)` 826,841 → 101,618 ns / `Analyze(200)` 1,478,352 → 132,270 ns
と概ね再現（±1〜5%、short・n=3）。`tenka_mark_review` は `optimized`（evidence 2件, `comparedCases=3`）で記録。

## ローカル側で実装した使いやすさ改善（本リポジトリ）
- **[実装] 測定/mark の investigation 直接束縛（resume 往復の全廃）。**
  `tenka_measure_companion` / `tenka_measure_target` / `tenka_mark_review` に `investigationId` を受け取らせ、
  `tenka_investigation` の resume を挟まず保存済み investigation へ束縛（`approved=true` を要求）。
  実地で **resume を一切使わず** baseline → candidate → mark の一気通貫を確認。
- **[実装] 実測信頼度の明示。** `tenka_measure_companion` 応答に `benchmarkJob` / `preliminary` /
  `recommendedBenchmarkJob` を追加（`short` は `preliminary=true`, `recommended=medium`）。
- **[実装] 回帰テストの頑健化**（前述）と **コンパイル修正**（前述）。テスト 753/753。

## 外部側にお願いしたい残りの使いやすさ
- **[中] Windows ランチャーの文字化け修正と一本化。** `scripts/start-tenka-local.ps1` が PowerShell 5.1 で
  構文エラー（UTF-8 BOM 無しの多バイト文字化け）。`TENKA_ALLOWED_ROOTS` 設定が分散している。
- **[中] 状態の持ち回りの自動化。** 各アダプタ起動が別セッションのため `snapshotId` / `companionId` /
  `investigationId` を人手で持ち回る。既知の「前回 snapshot を渡す」導線に加え、候補（companion 一覧）も
  自動解決できると往復が減る。
- **[低] 大きすぎる結果の既定ページング。** `review_queue{limit:200}` は `data:null` + `availableFields`。
  既定 limit を下げるか `nextAction` に `tenka_read_result` を明示。
- **[低] `binariesVerified` の明示。** `performance` 等に「既存 DLL とソースの同一性は未検証」を
  専用フィールドで示す（現在は limitations 文言のみ）。

## 検証の限界
- 実測は単一マシン・`short`（`sampleCount=3`）で、有意差の根拠ではない。
- コンパイル修正は本ローカル側のもので、上流 `70df106` の意図（A/B/C）全体の回帰は未実施。
- CI 停止中、Docker 未使用。

## スコア（ツール体験, 100点満点）
- 総合 **80 / 100**。
  - 機能・ワークフロー 30/30、多プロジェクト対応 20/20、実測・証跡 13/15、
    エラー診断・回復 14/15、納品品質（ビルド/テスト）3（ローカル修正前提）。
- 前回 78 から微増。最大の減点要因は「納品コミットがそのままではビルドできない」点で、
  これは役割分担により**ローカル側で吸収**する。ツール自体の機能・導線は高水準。
