# レビュー対象の発見・調査コンテキスト・エラー回復

## 変更範囲

2026-10-16 の main `63932f67c9f96d277aa04f17dd6d9e60281c8379` が基点。
`d4a464b` の AgentPermissionGate / AgentWorkspaceGate による先行修正は維持する。
CI は再開しない。Docker、実行許可、レビュー記録形式、contextHash は変更しない。

## review_queue は型を含む

MCP、HTTP gateway、ブラウザー HTTP の省略時 kind を共通定数 `all` に統一する。
明示的な `kind=method` / `kind=type` は維持する。

既存の total / items / summary は指定した範囲の意味を維持する。
新しい coverage はページング前かつ全フィルタ適用前の読み込み済み索引から計算する。

- loadedIndex / methods / types: 全体・メソッド・型の件数と状態別内訳。
- pendingOutsideScope: kind / projectId / query の外に残る未確認件数。
- pendingHiddenByFilters: state / trackableOnly も含め、フィルタで非表示の未確認件数。後続ページは数えない。
- hasPendingInLoadedIndex / requiresReloadInLoadedIndex: 絞り込みで隠さない未確認・再読み込みの信号。
- status: pending / reload_required / index_limited / no_pending_in_loaded_index。complete / verified は返さない。
- restart: 同じ snapshot の state=pending, kind=all, offset=0, cursor=null。projectId / query / trackableOnly もクリアする。

メソッドがすべて checked、型が7件 unreviewed なら、既定の呼び出しは型7件を返す。
kind=method で空のページになっても coverage.types.pending=7 と
coverage.pendingHiddenByFilters=7 を返す。hasMore=false を全体完了に使わない。

**移行注意:** 旧既定値 method で発行済みの cursor は method の範囲に結び付いている。
その続きを読む場合は kind=method を明示する。all への切り替え時は cursor を捨て、先頭から再取得する。
チェックポイントの移行・一括リセットは不要。

## tenka_review_context

```json
{"snapshotId":"<queueのsnapshotId>","targetId":"<item.target.id>","includeBinary":true}
```

一度の DashboardState.ExploreAsync の排他区間で取得する。

- selection: snapshotId / targetId / kind / projectId / typeId / methodId。
- target / state / markBasis: レビュー状態と記録用ハッシュ・revision。status / summary は勝手に埋めない。
- source: 型の構造・ソース、またはメソッドと既存 Roslyn の inspection。
- binary.assemblies[].candidates[]: 署名・HasBody・tenka_il にそのまま渡せる arguments。

所属プロジェクトの発見済み DLL だけを調べる。8アセンブリ、各50候補までを返し、
上限到達・カタログ打ち切り・個別読み取り失敗を明示する。
多ターゲット・Debug/Release・オーバーロードは自動選択しない。
追加候補には既存の tenka_context / tenka_binary_methods を使う。

tenka_il の snapshot / assemblySha256 再検査は維持する。
候補は型名・メソッド名の一致にすぎず、1候補でもソースとDLLの同一ビルドを証明しない。
再読み込みが必要な対象には IL 用候補を生成せず、型には架空の methodId を生成しない。
ビルド・再読み込み・ターゲット実行は行わない。includeBinary=false でDLL読み取りを省略できる。

大きい応答は callId と tenka_read_result の /selection, /markBasis, /source,
/binary/assemblies などから取得する。取得のためだけに高価な解析を再実行しない。

## nextAction

Gateway のツール失敗 data に code / nextAction を追加する。
stdio adapter は構造化された nextAction を使い、旧サーバーは同じ分類器で補う。
既存の MCP エラーコード tool_failed / tool_cancelled と requestId / callId は維持する。

承認不足、build / execution の個別設定、workspace 未ロード / 変更 / 使用中、
cursor / filter、レビュー記録、バイナリ、結果ページング、実験のセッション違いを区別する。
HTTP 409 のセッション上限・呼び出し予算・期限切れにも専用案内を返す。
分類は現行の例外種別名と既知メッセージに依存する。未知のメッセージは保守的な案内へ戻し、
任意のエラー文字列を nextAction にコピーしない。実行を自動再送・自動承認しない。

## テストと責務分離

ReviewQueueViews / ReviewContextPlan / AgentRecoveryAdvice は Mcp 側の独立した部品で、
Web / DashboardState を参照せずテストできる。
ReviewQueueViewTests / ReviewContextPlanTests / AgentRecoveryAdviceTests に46ケースを追加。
型7件、非表示の未確認、cursor、索引上限、再読み込み、引数対応、異なるプロジェクト/DLL、
候補上限、現行の権限ゲート、旧サーバーとの互換性を扱う。

```powershell
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter "FullyQualifiedName~ReviewQueueViewTests|FullyQualifiedName~ReviewContextPlanTests|FullyQualifiedName~AgentRecoveryAdviceTests"
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release
```

**検証状態:** 作成環境には .NET SDK がなく、C# ビルド・xUnit・Windows 実MCP/UI統合は未実行。
差分の基点4ファイルは Git blob SHA を照合した。静的な配線確認はコンパイル合格の代わりではない。
CI は起動していない。

## 残る設計課題

単一workspace、セッション内の実験/evidence所有権、MaxSessions=16、checked/optimized の自己申告性は残る。
contextHash も触らず、実測で再現していない churn を修正済みとは扱わない。

次段階では transport session と永続 investigation ID を分離し、workspace 変更の所有権・世代を明確にする。
セッション制約だけを外して別エージェントの実験を無条件に実行可能にしない。
性能確認はチェック記録と分け、比較可能な実測・ソース/ビルド同一性・環境・判定規則を伴う別状態として設計する。
contextHash 正規化では表示用パスと意味を持つコンパイル条件を区別し、単純にコンパイル条件を消さない。
