# セッション承認と共有workspaceへの読み取り接続

基点: `fe6378eb06b2b9427e10e0f9a657a925792aa613`。
kind=all・coverage・review_context・contextHash v2・必須引数エラー処理は維持する。

## 期限付き、プロジェクト限定の承認

信頼するプロジェクトを開き、ユーザーが対象・操作・期間に同意した後に一度呼ぶ。

```json
{"snapshotId":"<current>","projectId":"<selected>","tools":["build","jit"],"minutes":10,"approved":true}
```

ツール名は `tenka_approve_session`。toolsにはgateway名で build / jit / trace / run_companion / run_experiment を指定する。
指定したプロジェクトと操作に限り、期間内の呼び出しはapprovedの省略が可能。
初期値10分、1～30分。1セッションに1件の承認で、新しい承認は追加ではなく置き換え。
読み取り・実行による自動延長はしない。tenka_revoke_approvalで取消、tenka_finishで破棄。
接続し直したセッション、サーバー再起動、他のプロジェクトへは引き継がない。

対象は現在のsnapshotから解決したcsprojの正規化パスで束縛する。
buildは対象プロジェクト、jit/traceは実際に起動するassemblyの所属プロジェクト、
run_companionは発見されたcompanionの所属プロジェクト、run_experimentはprepare時の所属プロジェクトで検査する。
ライブラリAの承認だけで呼出元アプリBを起動することはできない。
同じプロジェクトのビルド後は新しいsnapshot IDへ選び直す必要があるが、承認は期限内なら維持する。

**セッション承認は、その期間内に当該プロジェクトのコードが変更されうることも含む許可。**
不変なバイナリだけを承認するトークンではない。実行の受付時に検査し、途中で期限を迎えても実行を強制終了しない。
project scopeの解決と実行の間には、既存実行経路の同一snapshot再検査が残る。
承認付与自体はrestore・ビルド・実行を行わない。既存のallowRestoreは自動変更しない。
run_experimentには従来どおりキャッシュからのオフラインrestoreを含む。
サーバースイッチ変更と自動再試行は行わない。
TENKA_AGENT_ALLOW_BUILD / TENKA_AGENT_ALLOW_EXECUTIONに加え、ローカル実行や生成実験の既存下位ゲートも必要。
明示的なapproved=trueによる従来の単発実行は維持する。

open_project（MSBuild評価・workspace置換）はセッション承認の対象外であり、従来の単発承認とexpectedSnapshotIdが必要。
外部エージェントは、接続しただけでユーザーが継続実行を承認したと解釈してはいけない。

## 現在のworkspaceを開き直さず使う

```json
{"path":"C:\\work\\Example\\Example.sln"}
```

ツール名は `tenka_attach_workspace`。読み込み中のworkspaceのパスと厳密に一致した場合に現在のcontextを返す。
承認不要。再評価・再ロード・ビルド・UI選択変更はない。異なるworkspaceなら失敗し、勝手に置換しない。
ソリューションで開いた場合に、その子csprojを同じworkspaceだとは扱わない。

context / attach / list_types / review_queue / review_target / review_history / review_diff / review_context / badges の
読み取りは、既存の共有gateが空くまで最大10秒待つ。待機後にsnapshotを再検査するため、
待機中のビルドで古くなったIDは採用しない。mark、承認付与、prepare、ビルド・実行は待機へ変更していない。

これは **古いsnapshotのピン留めでもRoslynの並列読み取りでもない**。
短い読み取り同士の即時競合を緩和する段階で、1つの最新snapshotと直列化の保証は維持する。
長いビルド中はタイムアウト、実際のworkspace置換後はstaleエラーになりうる。

## 制約を先に読む

`tenka_session_status` および tenka_context.session に、残り呼び出し数、最大16セッション、
1セッション200呼び出し・同時処理1件、非実行時の15分アイドル期限、承認の対象・期限を表示する。
status自体も1呼び出しとして数える。未ロードでも利用可能。
実験・結果・evidenceCallIdsの同一セッション制約は解除しない。
セッション移行を装って別エージェントの実験を実行しない。

未ロード時の既知の例外はツール名にかかわらずworkspace_unloadedに分類し、
信頼できる.sln/.slnx/.csprojを開く操作を案内する。SDKの必須引数エラー処理は維持する。

## 検証

```powershell
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter "FullyQualifiedName~AgentSessionApprovalTests|FullyQualifiedName~AgentWorkspaceAccessTests|FullyQualifiedName~AgentWorkflowGatewayTests|FullyQualifiedName~ReviewContextChurnTests"
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release
```

既存テストプロジェクトからWebを参照し、実際のgateway/journalの未ロード応答・制約表示・
重複requestの扱いまで回帰テストを追加。承認部品の範囲・失効・取消は偽時計で検査する。
C#ビルド・xUnit・Windows実MCP/UIはこの開発環境では未実行。詳しくは
[報告と証拠の区別](validation-feedback-2026-10-17.md)を参照。CIは再開しない。

## 残るもの

古いsnapshotの保持、複数Roslyn readerの並列実行、永続investigationを用いたセッション跨ぎ実験、
レビューmarkへのbefore/after測定の任意添付は未実装。checked/optimizedは引き続き自己申告で、
この変更を速度改善の裏取りとして扱わない。
