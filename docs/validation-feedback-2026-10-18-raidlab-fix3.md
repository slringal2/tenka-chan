# RaidLab 検証フィードバック 第3回（PR #30 の実地検証）

対象: PR #30「RaidLab DX改善」（head `81a67d9`）。main へマージ済み（merge commit `13afd1a`）。
前稿: `validation-feedback-2026-10-18-raidlab.md` / `...-raidlab-fix.md` / `...-raidlab-fix2.md`。

## 役割分担（継続）
- ビルド健全性は本ローカル側が担当して修正・検証する。
- ツールの使いやすさは双方で分担し、外部側にしか直せないものはフィードバックで返す。
- CI は停止中（`[skip ci]`）。自動のビルド検出は無い前提。

## ビルド健全性（ローカル検証）
- `dotnet build`（Debug, Web/Mcp/Chan.Core/Tests）: **0 エラー**。
- `dotnet test`: **757 / 757 成功**（main 753 から +4、すべて green）。
- 注記: 検証時、稼働中の Release サーバが `bin\Release` をロックしていたため Debug 構成で検証。旧プロセスは停止済み。
  実行時のランチャー（`.ps1`）は未実行（下記「限界」参照）。

## PR #30 の4項目（ライブ検証）
1. **[OK] Windows ランチャーの文字化け/BOM** — `scripts/start-tenka-local.ps1` と `scripts/test-windows-setup.ps1` は
   UTF-8 BOM 付きで保存され、PowerShell 5.1 の AST パーサで **構文エラー 0**。起動設定も正式ランチャーへ一本化。
   （静的な解析のみ。実起動は未実施。）
2. **[OK] 状態ID の持ち回り / companion 自動解決** — `tenka_measure_companion` を `companionId` 省略で実行し、
   一意な benchmark コンパニオン（`2188CE6B5F8E466A2FB2A6A5`）を自動選択して **benchmark_completed**。
   複数候補時は従来どおり明示要求（`measurement_companion_ambiguous`）で推測しないことを単体テストで確認。
3. **[OK] 大きな結果のページング** — `tenka_review_queue{limit:200}` は `data=null` / `resultAvailable=true` を返し、
   `nextAction` に具体的な取得手順（`tenka_read_result` callId, `pointer=/items`, `offset=0`, `limit=10`）を自動案内。
   同セッションで `tenka_read_result` を実行し **10件 / total 90** を取得。既定 20 件の指針も確認。
4. **[OK] `binariesVerified`** — `tenka_mark_review` の応答（`checkpoint.measurement` / `state.performance`）に
   `binariesVerified:false` と `benchmarkJob:"medium"` を確認。未検証を明示。

## 実測（Tenka.Sample.RaidLab, benchmarkJob=medium, n=30, resume なし）
`RaidAnalyzer.Analyze` を `List.Contains`/LINQ 走査（O(n²)）から `Dictionary`/`HashSet` 集計へ変更。契約テスト **46/46**。

| case | baseline | candidate | 改善 |
|---|---|---|---|
| `Analyze(Players=40)` | 777,897 ns / 1,335,744 B | 103,702 ns / 175,072 B | 約 7.5× |
| `Analyze(Players=200)` | 1,462,259 ns / 1,405,216 B | 119,616 ns / 234,320 B | 約 12.2× |
| `PipelineBenchmarks.Generate()` | — | 1,500,630 ns / 1,022,728 B | — |

フロー: baseline `measure_companion`（companion 自動解決）→ 最適化 → 候補 `measure_companion`（`investigationId` で resume なし）→
`mark_review`（`investigationId` + `approved=true`）→ **revision 2 / optimized / evidence 2**。
別ラウンド（`f7f60db` / `70df106` 検証）と合わせ、約 8〜11× を再現。

## 良くなった点
- companion 自動解決により、`snapshotId`/`companionId` を持ち回る手作業が減少。
- 大きな結果は自動でページング手順を案内し、`data:null` の空振りが解消。
- `binariesVerified` で「DLL とソースの同一性は未検証」が機械可読になった。
- Windows 入口が一本化され、BOM 問題が解消（静的に確認）。

## 残・限界
- ランチャーは実起動を検証していない（静的な構文解析のみ）。実機起動の確認を推奨。
- 単一マシン・`medium`（n=30）。有意差や本番性能の保証ではない。
- `snapshotId` と調査所有権は安全上、引き続き明示扱い（妥当な設計）。
- CI 停止中・Docker 未使用。

## スコア（ツール体験, 100点満点）
- 総合 **86 / 100**（前回 80 から +6）。
  - 機能・ワークフロー 30/30、多プロジェクト対応 20/20、実測・証跡 14/15、
    エラー診断・回復 15/15、納品品質（ビルド/テスト）7（ローカル修正前提を継続）。
- 減点要因は「納品コミットがそのままではビルドできない」体質と、ランチャー実行検証の担外。
