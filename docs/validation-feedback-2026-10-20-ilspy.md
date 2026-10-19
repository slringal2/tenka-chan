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

## 良くなった点
- 大きな結果のページング手順（`nextAction`）が大規模OSSでもそのまま機能し、`data:null` の空振りが無い。
- 除外・診断が警告として表面化し、無言の欠落が無い。
- 部分解析ソリューションでも 14,149 メソッドの索引と決定的スキャンが実用的な時間で完了。

## 新たなフィードバック（外部側で検討してほしい点）
1. **未ビルド時の Compile 欠落で誤誘導メッセージ** — ILSpy はビルド時に `ILSpyUpdateAssemblyInfo` ターゲットが `ICSharpCode.Decompiler/Properties/DecompilerVersionInfo.cs` を生成する。未ビルドで開くと、MSBuildWorkspace がこの未生成ファイルを Compile 項目に載せ、`RoslynReviewService` の実在チェックが `FileNotFoundException: Project file not found` を投げる。実際は「未生成の .cs ドキュメント欠落」であり、パスも出ないため原因究明が難しい。実パスを出す / 欠落ドキュメントは空扱いで継続する、のいずれかを検討してほしい（少なくとも本メッセージは避けたい）。
2. **大規模結果は同一セッション限定** — `nextAction` の `read_result` レシピを別セッションで実行すると `evidence_unavailable: No completed receipt in this investigation` になる。「同一セッション内で読む必要がある／大きい結果は restart を跨ぐと失われる」ことを文面で明示してほしい（`read_result` の説明文には弱く書かれているが、`nextAction` からは読み取れない）。
3. **部分解析ソリューションの compiler error 件数** — 元は 16 csproj のリポジトリを 3 プロジェクトだけ解析するため、`compilerErrors 430` は参照不足によるアーティファクトで「対象コードの欠陥」ではない。summary の注記は妥当だが、エラー件数（430）とその内訳・限界の対応関係をもう少し明示すると誤読を避けやすい。

## 限界
- 単一マシンでの検証。ILSpy は部分解析構成（Decompiler / ILSpyX / CLI の 3 プロジェクトのみ）。
- 今回は **実測（measure_companion / mark_review）まで未実施**。性能証跡は無い。
- CI 停止中・Docker 未使用。

## スコア（ツール体験, 100点満点）
- 総合 **84 / 100**。
  - 機能・ワークフロー 26/30（大規模OSSで追加の障壁が2つ発生）。
  - 多プロジェクト対応 20/20。
  - 実測・証跡 13/15（今回は未実測）。
  - エラー診断・回復 13/15（誤誘導メッセージとセッション制約）。
  - 納品品質（ビルド/テスト）12/20（同梱のままでは実在の大規模OSSサンプルを開けず、ローカル修正を要した）。
