# ILSpy 第4回フィードバックへの修正 (2026-10-20)

根拠: [validation-feedback-2026-10-20-ilspy.md](validation-feedback-2026-10-20-ilspy.md)
基点: `main 03e1561`。ローカル側で修正済みのNuGet注入ソースカタログ
(`tunnelvisionlabs.referenceassemblyannotator`)と 759/759 テスト実機報告を
**そのまま維持する**。この文書の変更分には新しい実機合格の主張を含めない。

## 直した問題と使い方

### 1. Compile項目の生成ファイルがないとき
`RoslynReviewService.OpenAsync` で、実在する`.csproj`を検査した後、
各`Compile`ソースの存在チェックに失敗した場合は、
`Project file not found`ではなく
`C# Compile source document is missing in project '<project>': <full .cs path>`
として区別する。場所とMSBuild生成ターゲットが調査できるようにする。
`tenka_open_project`の回復指示も「信頼済みプロジェクトをユーザー承認でrestore/buildしてから再Open」にする。

**欠落ソースを空ファイルへ置換せず、無言で除外もしない。**
そうした処理は分析結果の誤帰属を生むため実施しない。
NuGet外部パスのallowlistも変更しない。ILSpyの
`DecompilerVersionInfo.cs`は対象のMSBuild生成物として個別に扱う。

### 2. 大きな結果のページングと接続寿命
`AgentJournal.LargeResultNextAction` と
`tenka_read_result`の説明文に、元`callId`をページングするには
**同じ接続中のMCP transport session**で続ける必要があることを追記。
既定で15分操作がないとセッションは失効する。
`data=null,resultAvailable=true`でも**60KB超の元データはメモリにのみ保持**され、
再接続・サーバー再起動・他アダプターではページングできない。
保存済み`investigationId`から再開できるのは小さいreceipt/明示保持した実験や証跡であり、
同じ大きな`callId`の元結果は含まれない。
`evidence_unavailable`の場合に「再実行して回収」を勧めない。

### 3. compilerErrors 430件などの解釈
`compilerErrors`は**現在開いたRoslyn project graphだけ**の診断件数であり
本来の全ソリューションをコンパイルした結果とは限らない。
`summary.compilerDiagnosticsScope`と`compilation.interpretation`で限定を明示。
`summary.topCompilerErrorCodes`、`summary.potentialReferenceResolutionErrors`、
`compilation.topErrorCodes`は件数と診断IDを返し、例えば
CS0246/CS0234/CS0012などを「参照不足**かもしれない**分類」として示す。
解決できない参照があると同じ診断IDは正しいソースでも出るので、
**コードの欠陥であることを証明するフラグにはしない**。
静的スキャン結果内のcompiler findingsとも区別する。
本当のビルド結果は承認済みの本来のMSBuild/テストで確認する。

### 4. typeId/methodId と targetId の違い
`tenka_list_types`の返却には`selectionScope=current_loaded_roslyn_snapshot_only`と
`nextAction`を追加。
`typeId`/`methodId`は現在ロードしたRoslynソース索引用の短期IDであり、
別の起動プロセス・ワークスペース読み直し・変更されたsnapshotでは使用できない。
`tenka_inspect_type`の選択IDが使えない場合は
`tenka_context -> tenka_list_types -> tenka_inspect_type`の順で再選択する。
**永続レビュー用の64 hex`targetId`は別ID**であり、
`tenka_review_queue(kind=all)` から得て
`tenka_review_context(snapshotId,targetId)`で最新選択に変換する。
同じWebプロセス内でsnapshotが変化していなければ別MCP transportでも
使える場合はあるが、その有効性を前提にせず最新のcontextを確認する。

### 5. prepare / run / measure の順番
レビュー対象と紐づくベンチマークは
`tenka_prepare_experiment(kind=benchmark)`
→ **`tenka_measure_target(approved=true)`** の順にする。
**`tenka_run_experiment`を先に呼ばない**。
`measure_target`そのものが実行と観測への紐付けを行う。
一度実行したexperimentIdを再度実行・計測できない（何度も走らせない）。
回復メッセージにも`experiment_already_executed`を追加。
実行・生成コードの信頼とユーザー承認は従来と同じ。

### 6. 3プロジェクトのILSpyでbenchmark companionがない場合
`tenka_sample(sample=ilspy)`の`measurementGuidance`および
`tenka_context.workspace.measurementWorkflow`に
同じ`prepare -> measure_target`導線を追加。
コンパニオンがあればその既存BenchmarkDotNetプロジェクトを使えるが、
**なければ勝手に探索範囲を16プロジェクト全体へ広げない**。
生成実験を選んでもホスト許可と承認は必要。

## 今回の検証範囲

このコミットに追加したテストは実機未実行であり、
フィードバックの **759/759** は当ブランチより前の `main` での実機結果。
追加した検査は下記:

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Debug --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~NuGetSdkGeneratedDocumentTests|FullyQualifiedName~CompilerDiagnosticContextTests|FullyQualifiedName~DeterministicAnalysisIntegrationTests|FullyQualifiedName~AgentJournalTests|FullyQualifiedName~AgentRecoveryAdviceTests|FullyQualifiedName~SolutionExplorerTests|FullyQualifiedName~LargeOssCatalogTests"
```

ILSpyソリューションについては、**ビルド前**の生成`.cs`が欠落していると
そのファイルパスを含むエラーが出ること、承認したビルド後に
`tenka_open_project`で3プロジェクトを開けることを確認する。
`tenka_scan`のsummaryと`compilation`で診断ID内訳を確認し、
`compilerErrors`をバグ件数として転記しない。

**.NET SDKがこの編集環境にはなく、C#ビルド/xUnit/Windows上の実ILSpy操作は未実施。**
CIの新設・再開、Dockerの追加、ホスト実行権限の緩和は行っていない。
別のDraft PR #32（動的解析）と`RoslynReviewService.cs`等の同一ファイルを変更しているので、
双方を取り込む際は統合差分とローカル検証をやり直す。


## ラウンド4.1：レビューの往復数と操作上の誤解を削減（2026-10-21）

元のラウンド4.1は上流`main a0015f7`の
`docs/validation-feedback-2026-10-20-ilspy.md` に追記済み。
ローカル側の実レビュー rev4/5/6、SourceHash/ContextHashの無効化、
楽観的更新競合の実地確認は上流の実測として保持する。

**今回追加したDX変更:**

- `tenka_review_focus`: 現在の`snapshotId`とdurableな64 hex`targetId`だけで
  ソース、現在のレビュー状態・reason、sourceHash/contextHash/expectedRevision、
  呼び出し関係・CFG・診断の**上限付き要約**を一度に返す。
  外部`methodId`や`typeId`を事前に引き回す必要なし。
  `data.source`はレビュー記録のハッシュ対象となる**追跡ソース**。
  既存の`InspectAsync`の最大2万文字で切れたプレビューではなく、
  `ReviewTarget.Source`の全体を`sourceOffset`・`sourceLimit`で分割する。
  既定12,000文字、1回最大16,000文字。件数・省略フラグを返し、分割ページを
  識別するためにsourceTotalChars/sourceHasMoreも返す。
- 旧経路も互換維持: 同じセッションの保存済み`review_context`／
  `inspect_method`に対する`tenka_read_result(pointer=/source)`は
  元のソースだけを`data.source`へ短く返す（`limit*200`文字、最大16,000）。
  `/inspection/source`など既存のJSON Pointerと旧レスポンスは変更しない。
- `tenka_reload_workspace`: 既にロード済みのパスを、その**最新snapshotId**と
  `approved=true`で明示的に再読み込み。呼び出し元はパスを変更できず、
  別エージェントが更新したsnapshotには衝突で失敗。MSBuildの許可と
  `TENKA_AGENT_ALLOW_BUILD`は必須で、自動ビルド/restoreはしない。
  読み込み後は新snapshotで`review_queue`から再取得。既存markは書き換えない。
- `review_queue(kind=pending)`は先頭から
  `kind=all|type|method`、`state=pending|...` と用途を分けて説明するエラーに変更。
- `contextChanged`は`sourceFiles`ハッシュも含むことを明示。
  空白のみの変更で失効する**保守的な安全設計を維持**し、バージョン2 fingerprintと
  source/checkpointハッシュは書き換えない。

### 使用例

```text
tenka_context -> snapshotId
tenka_review_queue(snapshotId, kind=all, state=pending) -> target.id (64-hex)
tenka_review_focus(snapshotId, targetId=target.id)
  -> data.source, data.analysis, data.sourceHash, data.contextHash,
     data.expectedRevision, data.sourceHasMore
必要なら tenka_review_focus(..., sourceOffset=12000) -> 続き
さらにILが必要なら tenka_review_context(..., includeBinary=true) -> 候補と署名を確認
ソースが変わった場合のみ、承認して tenka_reload_workspace(snapshotId, approved=true)
```

### 追加テスト（この編集環境では未実行）

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Debug --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ReviewFocusedSliceTests|FullyQualifiedName~ReviewFocusIntegrationTests|FullyQualifiedName~AgentJournalTests|FullyQualifiedName~AgentWorkflowGatewayTests|FullyQualifiedName~ReviewQueueViewTests|FullyQualifiedName~ReviewContextChurnTests|FullyQualifiedName~AgentRecoveryAdviceTests"
```

- C#ビルド・xUnit・実際のILSpy 14,149メソッドでの導線確認はローカル側未検証。
  元mainの759/759は今回新設したテストの合格数ではない。
- CI停止・Dockerなし・許可範囲の拡大なし。
- 既存の競合検出（古いexpectedRevision/sourceHash/contextHashの拒否）、
  sourceChanged/needs_reviewと復帰、永続的なレビュー履歴を維持する。
- PR #32（動的解析）は別Draftであり、同時統合時にはRoslyn/Gatewayの差分確認が必要。
