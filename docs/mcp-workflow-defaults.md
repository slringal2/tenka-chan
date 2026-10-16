# MCPのID省略・承認表示・セッション復旧（2026-10-16）

基準はmain `55cc31b4f0396f343deb3f0795ef87a9706a4efd`。第2版フィードバックの一部への対応です。
全P0/P1を完了した変更ではありません。CI・依存パッケージ・サンプルの実装・Docker要否は変更しません。
外部クライアントが推論と承認を担当し、起動中の同じWebプロセスを使う構成も維持します。

## 1. IDの省略

既存のソースツールの `snapshotId` をMCP上の省略可能引数にしました。例えば次の呼び出しが可能です。

```text
tenka_review_queue {}
tenka_scan {}
tenka_inspect_method {"methodId":"取得済みのメソッドID"}
```

省略またはnullは、読み込み済みの現在のスナップショットを意味します。空文字や不正な型は省略扱いにしません。
解決はワークスペースの既存ロック内で行い、明示的なIDに固定してから処理します。
処理がロックを取り直すまでに別のプロジェクトへ切り替わった場合は、既存のスナップショット検査が拒否します。
明示した古い `snapshotId` を勝手に現在値に置換したり、失敗した実行を自動再送したりはしません。
同じrequestIdの完了済み再取得は解決前にキャッシュを返し、別の対象で再実行しません。

結果の `AgentReply.snapshotId` に解決済みIDを残します。ビルド自身が新しいIDを返した場合は新しいIDを優先します。
メソッド・型・DLLのIDや確認時のソース／コンテキストハッシュは自動生成・自動代替しません。
別スナップショットのIDが使えるようになる変更ではなく、確認記録のハッシュ／revision検査も維持します。

`tenka_build` と `tenka_prepare_experiment` の `projectId` は、直接開いたcsproj、または索引内の唯一のプロジェクトなら省略できます。
直接開いたcsprojに依存プロジェクトがあっても、ルートのcsprojを選択します。
複数プロジェクトを含むソリューションでは、Webの一時的な選択を推測しません。`ambiguous_project` を返し明示指定を求めます。
一覧・確認キューの未指定projectIdは、既存互換性のため従来どおりソリューション全体です。
`tenka_open_project.expectedSnapshotId` の既存の競合防止契約は変更していません。

MCPの既存ツール名・引数名は維持し、snapshotIdと上記projectIdのrequiredを緩和しています。クライアントでtools/listを再取得してください。
C#の静的メソッドを直接呼び出す場合は、必須引数を先にするため引数順が変わっています。位置引数呼び出しは再確認が必要です。

## 2. ホスト設定と今回の承認を区別

`tenka_open_project` / build等でホストが不許可なら `host_build_disabled`、実行が不許可なら `host_execution_disabled` と説明します。
ホストが許可済みで `approved:true` がない場合は `approval_required` と説明し、サーバー設定変更を案内しません。
これらの理由コードは、従来の `tool_failed` エラー内のmessageに含まれます。エラー外形を破壊的に置き換える変更ではありません。

`tenka_context.permissions.build/execution` は互換性のため残しますが、ホストのスイッチであることを明示します。
追加の `hostPolicy`、`invocation.approvalRequired`、`invocation.toolsRequiringApproval`、`localPrerequisites` を参照してください。
ホストが許可すること、ローカルのSDK・信頼設定が整うこと、今回の操作をクライアントで承認することは別です。
`approved:true` を自動設定する処理はなく、同意を暗黙に継承しません。

**ホスト生成実験はセキュリティサンドボックスではありません。** 作業ディレクトリの分離と権限の隔離は異なります。
現在のOSユーザー権限で動きます。`executionIsSandboxed:false` を返します。
`approved:true` 自体もクライアントの申告で、人間によるクリックの証明ではありません。

## 3. セッションが満杯でも復旧できる入口

公開ツールは38個です。既存36個に次を追加しました。

```text
tenka_sessions_list {}
tenka_session_end {"sessionId":"一覧のitems[].id","approved":true}
```

一覧・指定終了は調査セッションを新設しないため、`tenka_connect` が上限で失敗しても呼べます。
接続ファイルのローカル資格情報は必要です。同一マシンAPIの管理操作であり、自己申告のclientNameを本人確認として扱いません。
一覧は件数・上限・idle TTL・再利用可能枠・各状態を返します。一覧を繰り返してもLastSeenは更新しません。
終了は指定IDと個別承認が必要です。idleだけを終了でき、既にfinished/expiredならその状態を返します。
実行中の処理を終了・追い出し・キャンセルせず、プロセスをkillしません。
永続的なレビュー履歴も削除しません。ただし上限再利用で古いセッションの一時的な呼び出し結果は従来どおり破棄されます。
終了されたアダプターで調査を続ける際は、`tenka_connect` から明示的に接続し直します。

従来から15分のidle失効処理が存在しました。今回新設したのはTTLそのものではなく、設定可能化と復旧・説明の入口です。
失効は接続・呼び出し・一覧・モニターのアクセス時に判定します。バックグラウンドタイマーではありません。
idleの期限が経過すれば次の接続時にも回収でき、実行中の処理は期限で追い出しません。

| サーバー起動前の環境変数 | 既定 | 範囲 |
|---|---:|---:|
| `TENKA_AGENT_MAX_SESSIONS` | 16 | 1〜128 |
| `TENKA_AGENT_IDLE_TTL_MINUTES` | 15 | 1〜1440 |

これは今回のサーバー環境変数であり、setupメニューや保存設定に新項目を追加した変更ではありません。
不正な値は起動時に明示的な設定エラーにします。接続が満杯なら上限・TTL・一覧/終了の使い方をエラーに示します。
管理通信もloopback限定・proxy禁止・redirect禁止・本文サイズ制限・全体期限・資格情報除去・再送なしを維持します。

## 4. 未対応と注意点

- SDKで必須引数の型変換・バインドが失敗するケース全体の処方的エラー化は未対応です。`review_queue {}` はsnapshot必須をなくすことで通せますが、他ツールの不足引数全般を修正したとは報告しません。session_endのID不足は専用の名前付きエラーになります。
- 同一セッション以外のevidence・実験所有権の共有は未対応です。clientName一致だけで許可すると、自己申告名によって別セッションの根拠を流用できるため、その緩和は入れていません。
- context_changedの原因一覧・永続コンテキスト差分は未対応です。現行ハッシュはメソッド本文だけでなく、プロジェクト内文書・csproj・構文/コンパイル設定・参照・依存先を含みます。メソッド差分0行だけでは、再確認が不要とは判断できません。再起動だけで誤失効したかは、変更前後の入力がないため特定できていません。
- 非破壊の候補コピー、propose/diff/apply/revert、一括レビュー、ワンストップ検証、targetId⇔methodIdの追加橋渡し、collectionキーの全面統一は未対応です。既存のベンチ比較機能とは別に、永続ベースライン管理も残ります。

GameLabの約122倍という値はユーザーの再検証報告で、今回この環境で再計測していません。
それは候補実装の速度差であり、甜花ちゃんが調査時間を削減したという証明ではありません。
通常のエージェントと同条件で、正しさ・実測の到達率、総時間、手作業の回数を比較する評価が別途必要です。

## 5. 検証記録

実施したのは、Pythonのソース構造テスト3件（その中で負の改変9種類を拒否）、C#13ファイルのコメント/文字列を除いた括弧対応検査、`git diff --check` です。
元の変更対象6ファイルは取得したGit blobと完全一致することを確認してから編集しました。
これらはC#コンパイルや実MCP通信ではありません。

C#回帰テストは66ケース追加しました。
`AgentWorkflowDefaultsTests` 27、`AgentSessionLifecycleTests` 15、`AgentSessionClientTests` 24です。
対象解決と承認分離、実際のAgentJournal、偽HTTPハンドラー経由の管理クライアントを対象にします。
**C#ビルド・66ケースの実行・ASP.NET経路・公式SDK stdio・Windows/PowerShell/opencode実接続は未実行**です。
この環境には.NET SDK/PowerShellがなく、SDK取得先のDNS解決にも失敗しました。

復元済み依存関係と.NET 10 SDKがある環境で、本体とMCPアダプターを終了してから実行します。

```powershell
dotnet test tests\TenkaChan.Tests\TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentWorkflowDefaultsTests|FullyQualifiedName~AgentSessionLifecycleTests|FullyQualifiedName~AgentSessionClientTests" --logger "trx;LogFileName=agent-workflow.trx"
```

`check-tenka.bat` の既存の限定フィルターは今回変更していません。新しい3クラスには上の明示的フィルターを使ってください。
既存の接続プローブは登録済みクラスからツール名を読み、38個を認識できる構造ですが、管理ツールの実呼び出しや任意引数の新契約まではテストしません。
