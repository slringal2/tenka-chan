# RaidLab DX フィードバック対応 (2026-10-18)

入力: [validation-feedback-2026-10-18-raidlab-fix2.md](validation-feedback-2026-10-18-raidlab-fix2.md)。
基点: main `57b7853`（753/753通過はフィードバック側の既存コミットの実機報告）。
この修正はユーザー側ですでに実装された調査ID直接束縛と `benchmarkJob/preliminary` の表示を保持する。

## 1. Windowsランチャーの統一

`setup.bat` と `start.bat` は既存のBOM付き
`scripts/tenka-windows.ps1` に集約済み。
BOMなしの旧 `scripts/start-tenka-local.ps1` に残っていた、独立した
`TENKA_ALLOWED_ROOTS`・生成実験の無条件有効化・独立のNuGet操作を撤廃した。

旧スクリプトも**UTF-8 BOM付きの薄い互換ラッパー**とし、
既存設定ファイルを使う `-Mode Start` に委譲する。
旧スクリプトに `-WorkspaceRoot/-ModelEndpoint/-ModelName` を渡した場合は、
設定を黙って無視せず、`setup.bat` またはMCPクライアント側の設定へ誘導して停止する。
起動時の権限を増やさない。Windows PowerShell 5.1のソースパーサー/BOM検査を
既存 `scripts/test-windows-setup.ps1` に追加した。

## 2. 実測対象の companionId 省略

`tenka_measure_companion` は `companionId` 省略可。
現在ロードされているプロジェクトの**既知のBenchmarkDotNet companion**だけを候補とし、
対象 `targetId` のプロジェクトへの直接・推移的 `ProjectReference` が確かめられる
候補が**ちょうど1件**のときのみ自動選択する。

- 候補なし→エラー。`tenka_context` でプロジェクト構成を確認。
- 複数件→実行せず `measurement_companion_ambiguous` と候補IDを返す。
- `companionId` 明示時も同じProjectReference制約を検査。
- 既存 `snapshotId` チェックとユーザー承認・実行権限は**そのまま**。
  すでに別アダプターへ渡せる `investigationId` は引き続き明示する。
- 成功/失敗応答に選択した `companionId` と新 `currentSnapshotId` を返す。
- `targetId` が指す対象が、別ワークスペースに切り替わっていた場合には現在の
  ロード済みカタログに存在しないため拒否。

MCP例:
```json
{
  "snapshotId": "<tenka_contextで取得したID>",
  "targetId": "<tenka_review_contextで取得した対象ID>",
  "approved": true,
  "benchmarkJob": "medium",
  "investigationId": "<保存済み調査に続ける場合のみ>"
}
```
`companionId` が未指定で同一対象に候補が1件なら実行可能になる。
ただし、これによって任意のソース変更・ワークスペース置換・実行承認を自動化するわけではない。

## 3. 大きな結果の案内と既定ページング

MCP `tenka_review_queue` の既定 limit は **50→20**（Web UIの既定値は変更なし）。
結果が60KBを超えて `data=null` になる場合は、保存済み結果の
`callId` に対して `tenka_read_result` を呼び出す具体的な
`nextAction` を追加した。例: `pointer=/items, offset=0, limit=10`。
read-onlyの結果と実行済みの副作用ある処理を混同して勝手に再実行しない。

保持上限自体を超える結果はページング不可である旨を明示する。
コンパニオン結果や生成実験を**同じexperimentIdで再実行しない**。

## 4. binariesVerified の明示

`checkpoint.measurement.binariesVerified` と
`state.performance.binariesVerified` を明示的に `false` とする。
getter-onlyであり保存データを改変してもtrueにはできない。
`false` は不一致の検出ではなく、**ソースと実行DLLの同一性を証明していない**意味。
`independentlyVerified` も従来どおりfalse。
同一ビルドの証跡連鎖/PDBとの照合は別機能として未実装。

## 手元で必要な検証

**今回のC#コードとWindowsランチャーはこの編集環境ではビルド・実行できていない。**
.NET SDKとPowerShell 5.1がなく、CIは停止中のまま。
今回の回帰テストは追加したが、753/753という以前の合格を
今回変更の実行結果として扱わない。

Windowsで以下の順番を推奨:

```powershell
# ソフト本体を終了し、git pull 後に
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CompanionBenchmarkWorkloadTests|FullyQualifiedName~AgentJournalTests|FullyQualifiedName~ReviewBenchmarkAttachmentTests"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-windows-setup.ps1
```
注: 最後の既存テストスクリプトは元からNodeによるUIテストも含む。
Windows PowerShellのパーサー検査/BOMチェックのみを行うなら
`scripts/test-windows-setup.ps1` の冒頭の検査部分を利用すること。
実際の `start.bat` と旧スクリプト（引数なし）の起動経路も確認。

## 未完の境界
- snapshotIdは現在の共有ソース状態を明示的に選ぶため必須。
- investigationIdは調査の所有権に関わるため、別セッションの自動取り込みはしない。
- 実際のbinary-to-sourceの同一性判定は未実装。
- CIは新設/再開しない。Docker不要。
