# ILSpy 検証フィードバック 第4回（大規模OSS・初回）

対象: 大規模OSSサンプル **ILSpy v10.0**（固定コミット `5deb6fb4258a09ba0ff5f63fa6180b1f8d6fb6ad`、`samples/External/ILSpy-10.0`）。
前稿: `validation-feedback-2026-10-18-raidlab.md` / `...-raidlab-fix.md` / `...-raidlab-fix2.md` / `...-raidlab-fix3.md`。

## 役割分担（継続）
- ビルド健全性は本ローカル側が担当して修正・検証する。
- ツールの使いやすさは双方で分担。今回、ローカル側で1件のブロッカーを修正した（下記）。
- CI は停止中（`[skip ci]`）。自動のビルド検出は無い前提。

## ビルド健全性（ローカル検証）
- `dotnet test`（Debug）: **759 / 759 成功**（main 757 から +2、いずれも green）。
- ILSpy 本体: `run-large-oss.bat`（`prepare-large-oss.ps1 -Mode Build`）で CLI＋依存を Release ビルド、**0 エラー**（`net10.0`、約 23 秒、警告 38）。
- 稼働中の Release サーバが `bin\Release` をロックするため、テスト検証は Debug 構成で実施。修正確認後に Release を再ビルドし直して再起動した。

## ローカル修正: NuGet がソースを注入するパッケージの除外
- **症状**: `tenka_open_project` で `ILSpy.Analysis.slnx` を開くと `UnauthorizedAccessException: Path is not in TENKA_ALLOWED_ROOTS`。
- **原因**: `ICSharpCode.Decompiler.csproj` が `tunnelvisionlabs.referenceassemblyannotator/1.0.0-alpha.160/build/NullableAttributes.cs` を `Compile` 項目として取り込む。当該ファイルは `TENKA_ALLOWED_ROOTS` 外（`%USERPROFILE%\.nuget\packages`）にあり、既存の `NuGetSdkGeneratedDocumentPolicy` は `Microsoft.NET.Test.Sdk` の合成 Program.cs 完全一致のみを除外していたため非該当 → `RoslynReviewService.OpenAsync` が例外で開けない。
- **対応**: 既存の「レビュー済み・完全一致カタログ」方式を維持したまま、`tunnelvisionlabs.referenceassemblyannotator` を追加。
  - レイアウトは `<pkg>/<ver>/build[/<tfm>]/<file>` の完全一致のみ。未知パッケージは従来どおり fail-closed。
  - 許可ルートは変更しない（キャッシュを広く許可しない方針を維持）。除外は解析スナップショットからのみで、警告として表面化。
  - 変更: `src/TenkaChan.Mcp/Analysis/NuGetSdkGeneratedDocumentPolicy.cs`、`src/TenkaChan.Mcp/Analysis/RoslynReviewService.cs`（警告文言・コメント一般化）。
- **回帰テスト**: `tests/TenkaChan.Tests/NuGetSdkGeneratedDocumentTests.cs` に +2（正例＝除外される / 負例＝別パッケージ・別ファイル・想定外の入れ子・build 以外のフォルダは除外しない）。

## ILSpy での実行結果（snapshot `9c4c1b8f413a4d92b99638cc0eea011d`）
- `tenka_open_project`（`ILSpy.Analysis.slnx`）: **成功**。解析対象は **3 プロジェクト / 14,149 メソッド**
  （Decompiler 579 docs / ILSpyX 87 / ILSpyCmd 8）。除外警告「Excluded 1 known package-injected source document(s)…」を確認。
- `tenka_review_queue{kind=all}`: **14,667 トラッカブル対象**。ページング動作。
- `tenka_priorities`: 大規模結果のため `data=null`＋`resultAvailable=true`＋`nextAction`（`read_result /items`）を返す。
- `tenka_scan`: **2,085 findings**、674 docs、`compilerErrors 430` / warnings 1 / infos 181、method 指標 14,149（inline は 2,000 で打ち切り、findings は打ち切り無し）。
- `tenka_scan` の hotspots: **300 methods / 1,500 sites**（`/hotspots/methods` 等の具体ポインタ指定が必要）。
- `tenka_review_context`: selection / target / state / source / markBasis / binary を取得 OK。

## 実測（生成ベンチマーク, measure_target）
- companion が無い（解析は 3 プロジェクトのみ）ため生成実験で計測。`tenka_prepare_experiment`（kind=benchmark, subject=netstandard2.0, runtimeFramework=net8.0）
  → `tenka_measure_target` → `tenka_mark_review` の順で実施。
- 対象 `CSharpDecompiler.DecompileTypeAsString(FullTypeName)`（targetId `D2DC0104…68C0`）:
  **mean ≈ 109.3 ms / median ≈ 109.3 ms / 53.5 MB per op**、n=40（Windows 11 / Ryzen 9 9900X / .NET 8.0.32）。
- `tenka_mark_review` → **revision 4 / status `checked`**。最適化は未実施のため `optimized` にはしていない（baseline のみ＝速度改善の主張なし）。
- experiment `3a8d5ded…` / investigation `45f9c88a…`。

## 良くなった点
- 大きな結果のページング手順（`nextAction`）が大規模OSSでもそのまま機能し、`data:null` の空振りが無い。
- 除外・診断が警告として表面化し、無言の欠落が無い。
- 部分解析ソリューションでも 14,149 メソッドの索引と決定的スキャンが実用的な時間で完了。

## 新たなフィードバック（外部側で検討してほしい点）
1. **未ビルド時の Compile 欠落で誤誘導メッセージ** — ILSpy はビルド時に `ILSpyUpdateAssemblyInfo` ターゲットが `ICSharpCode.Decompiler/Properties/DecompilerVersionInfo.cs` を生成する。未ビルドで開くと、MSBuildWorkspace がこの未生成ファイルを Compile 項目に載せ、`RoslynReviewService` の実在チェックが `FileNotFoundException: Project file not found` を投げる。実際は「未生成の .cs ドキュメント欠落」であり、パスも出ないため原因究明が難しい。実パスを出す / 欠落ドキュメントは空扱いで継続する、のいずれかを検討してほしい（少なくとも本メッセージは避けたい）。
2. **大規模結果は同一セッション限定** — `nextAction` の `read_result` レシピを別セッションで実行すると `evidence_unavailable: No completed receipt in this investigation` になる。「同一セッション内で読む必要がある／大きい結果は restart を跨ぐと失われる」ことを文面で明示してほしい（`read_result` の説明文には弱く書かれているが、`nextAction` からは読み取れない）。
3. **部分解析ソリューションの compiler error 件数** — 元は 16 csproj のリポジトリを 3 プロジェクトだけ解析するため、`compilerErrors 430` は参照不足によるアーティファクトで「対象コードの欠陥」ではない。summary の注記は妥当だが、エラー件数（430）とその内訳・限界の対応関係をもう少し明示すると誤読を避けやすい。
4. **選択ID（typeId/methodId）はセッション限定** — 別セッションで `tenka_inspect_type` を呼ぶと `Type selection is no longer available`。durable な targetId（`review_queue` が返す 64 hex）とは別物で混同しやすい。`list_types`/`inspect_type` は同一セッションで連結する必要がある旨と、durable ID への導線を明示してほしい。
5. **`tenka_measure_target` は未実行の実験専用** — `tenka_run_experiment` 済みの実験を渡すと `Experiment has already been executed. Prepare a new one.`。自然な「prepare → run → measure」順が失敗するため、`prepare_experiment` の説明かエラー文で「measure_target は未実行の実験に対して呼ぶ（実行は measure_target が行う）」ことを明示してほしい。
6. **大規模OSSでは既定で companion 不在** — 解析対象が 3 プロジェクトのみのため `run_companion`/`measure_companion` は使えず、生成実験（`measure_target`）が唯一の計測手段だった。設計どおりだが、`tenka_sample`/`context` から `measure_target` へ誘導する案内があると導線が良い。

## 限界
- 単一マシンでの検証。ILSpy は部分解析構成（Decompiler / ILSpyX / CLI の 3 プロジェクトのみ）。
- 実測は **baseline のみ**（生成ベンチマーク、n=40）。候補（最適化後）計測は未実施で、速度改善の証跡は無い。上流ソースは改変していない。
- CI 停止中・Docker 未使用。

## スコア（ツール体験, 100点満点）
- 総合 **85 / 100**。
  - 機能・ワークフロー 27/30（大規模OSSで追加の障壁が2つ、うち1つはローカル修正で解消）。
  - 多プロジェクト対応 20/20。
  - 実測・証跡 14/15（生成ベンチマークで baseline を実測。候補は未実施）。
  - エラー診断・回復 12/15（誤誘導メッセージ・セッション制約・実行順の摩擦）。
  - 納品品質（ビルド/テスト）12/20（同梱のままでは実在の大規模OSSサンプルを開けず、ローカル修正を要した）。

## 追記: 再起動を跨ぐ実地検証（ラウンド 4.1）

対象を開き直して（snapshot `b180abfa…` → 空白変更後 `670c2317…` → 復元後 `f1be736d…`）、レビュー記録の永続性と無効化を実地確認した。

- **マークは再起動・再オープンを跨いで永続**: サーバー再起動＋新スナップショットでも、durable な `targetId` で `tenka_review_history` が rev4（`DecompileTypeAsString`, checked）と rev5（`CollectNamespaces`, checked）を返した。
- **状態フィルタ**: `review_queue` は all 14,667 / pending 14,665 / checked 2。マーク済みが pending から正しく除外される。
- **楽観的同時実行**: 古い `expectedRevision` での `tenka_mark_review` は `ReviewConflictException`（「別のエージェントが記録を更新しました…上書きはしていません」）で拒否され、再取得の案内が返る。
- **無効化**: サンプルの空白のみの変更 → 再オープンで `needs_review` / `reason=source_changed`（revision は 5 のまま）。元に戻して再オープンすると `checked` / `current` に復帰。変更は復元済み（作業ツリー clean）。
- **実レビュー**: hotspot 2 位の `RequiredNamespaceCollector.CollectNamespaces(IEntity, MetadataModule, CodeMappingInfo)` を精査（callees 19 / callers 3 / blocks 101）し、rev5 で `checked` 記録。
  - 候補として「partial method で `HasBody` 判定と parts の本文取得が不一致 → 名前空間欠落」を疑ったが、`CodeMappingInfo.GetMethodParts` はラムダ/状態機械の parts 用で、partial は別 MethodDef として個別に処理される。partial method を含むアセンブリを ILSpyCmd で逆コンパイルし、実装本体の `using System.Text;` が出ることを確認 → **偽陽性**（欠落なし）。
  - 残る軽微点: `HasBody` は親 `IMethod` で判定し本文は各 `part` の `MethodDefinition` から取得する非対称。現状は RVA 0 を `BadImageFormatException` で握っており実害は無いが、意図をコメントで補うと読みやすい。
- **2件目**: hotspot 1 位の `TranslatedExpression.ConvertTo(IType, ExpressionBuilder, bool, bool)`（388 行）を精査し rev6 で `checked`。末尾の `NormalizeTypeVisitor.IgnoreNullabilityAndTuples.EquivalentTypes` 再チェックは、冒頭で同条件なら必ず `this` を返すため**到達不能**（無害な重複）。ほかに IntPtr/UIntPtr 変換ブロックの重複、char↔pointer 条件の `&&`/`||` 混在を指摘（いずれも挙動変更なし）。

### 追加のフィードバック
7. **引数名の紛らわしさ**: キューの状態は `state=pending` で、`kind=pending` は `ArgumentException: Invalid review filter.`。エラー文の案内は的確だが「kind に pending を渡しがち」なので、`kind` の許容値（all|type|method）をエラー文の先頭に出すとより親切。
8. **MCP に reload 導線が無い**: ソース変更後の再読込は `tenka_open_project` に現 `expectedSnapshotId` と `approved=true` を渡す必要がある。`tenka_reload_workspace`（承認必須）があると明確。
9. **空白のみの変更で `contextChanged=true`**: `sourceChanged` と同時に context も変わった扱いになる。仕様なら「context はソース本文を含む」旨の注記を、そうでなければ fingerprint の正規化を検討。
10. **結果エンベロープの形がツール毎に異なる**: `inspect_method` のソースは `data.inspection.source`、`review_context` は `data.source`。`mcpctl` の capture パスを書く際に試行錯誤が必要だった（自動判定できると良い）。
11. **大結果時の `read_result` 連結が深い**: 対象メソッドが大きいと `review_context` / `inspect_method` / `read_result /inspection` がすべて `data=null` になり、同一セッション内で `selection` → `methodId` → `inspection` → `data/source` と 4〜5 段連結する必要がある。入れ子が `data.data` になる点も含め、`/source` や `/inspection/source` を直接指定できると短くなる。

### スコア更新
- 永続性・無効化・競合検出の実地確認を加点し、総合 **86 / 100**（エラー診断・回復 12→13。他は据え置き: 機能・ワークフロー 27、多プロジェクト 20、実測・証跡 14、納品品質 12）。
