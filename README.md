# 甜花ちゃん — Tenka-chan

C#/.NETの性能調査を、外部LLMエージェントから進めるためのツールです。
Roslyn・IL/PDB・JIT・テスト・ベンチマークと、調査の記録を扱います。
モデルの推論はopencodeなどの外部クライアントが担当し、Webは進捗・結果の確認に使います。

> 開発中です。実装済みと動作確認済みは別です。
> 今回の追加にはC#ビルド・Windows・実MCP接続で未検証の部分があります。
> 最新の範囲と制限は [調査の引き継ぎと変更候補](docs/investigation-proposals.md) を参照してください。
> 静的候補、エージェントの報告、実測された結果を区別します。

## 起動と接続

Windowsでは最初に `setup.bat`、次回から `start.bat` を実行します。
本体は.NET 10 SDK、調査対象は.NET 8以降と、一部の.NET Standardライブラリです。
Dockerは通常利用に不要です。Node.js／Pythonは開発検証用で、通常のWeb/MCP利用には必要ありません。

セットアップが生成する `mcp-config.json` を外部クライアントへ登録します。
stdioアダプターを起動する構成です。`agent-mcp.bat` をコマンド起動型MCPへ登録する方法もあります。
**内部 `/agent/v1/*` をMCP HTTPエンドポイントとして登録しないでください。**
複数アダプターは起動中の同じWebワークスペースを参照します。

接続ファイルにはローカル資格情報が入ります。共有・Git登録しないでください。
本体ごとの設定場所は `%LOCALAPPDATA%\TenkaChan\launcher\<識別子>` です。
サーバー再起動後は `tenka_connect` で明示的に再接続します。

詳細: [外部エージェント連携](docs/external-agent.md) / [Windowsセットアップ](SETUP-GUIDE.md)

## 普段の調査

低水準のツールを毎回すべて使う必要はありません。
まずソース・呼び出し元と測定目的を確認し、必要なときだけIL/JITへ進みます。

1. `tenka_context` と `tenka_review_queue` / `tenka_priorities` で対象を決める。
2. `tenka_review_workflow {"targetId":"…"}` で、変更するファイル全文・ファイルハッシュ・既存テスト／ベンチ候補を得る。
3. `tenka_propose_change` に、対象ID・ファイルハッシュ・変更後のファイル全文を渡す。
4. `tenka_diff_proposal` を確認し、承認して `tenka_review_workflow` の `mode:"validate"` を実行する。
5. 比較結果を読み、必要な場合だけ別途承認して `tenka_apply_change`。変更後は再読み込みして確認記録を残す。

変更候補では、同じ既存テストとベンチを独立した変更前／変更後コピーで実行します。
テストの期待値を自動発明したり、未検証の対象をチェック済みにしたりはしません。
時間のかかる検証は段階ごとに保存し、`awaiting_next_stage` なら同じproposalIdで明示的に続行します。
バックグラウンド実行ではありません。完了した段階は再実行せず、開始後の結果不明も勝手に再試行しません。

**今回の候補機能は既存C#ファイル1つ・6万文字までです。**
既存のテストとベンチプロジェクトが必要です。候補が複数なら選択します。
範囲外の参照、動的なビルドパス、出力先の独自指定などは対応外として止めます。
すべての大規模ソリューションをコピー・検証できる段階ではありません。

`tenka_revert_change` は適用後のファイルがそのままのときだけ元のバイト列へ戻します。
後から編集したファイルへの強制上書き、Git reset、複数ファイルの原子的変更はしません。
別エディターとの同時変更は完全には排除できないため、退避ファイルを残し競合・中断時は手動確認に止めます。

## 再接続・再起動後に続ける

通信セッションと、根拠・実験・変更候補をまとめるinvestigationIdは別です。
`tenka_context.investigation` または `tenka_investigation {"action":"status"}` で現在のIDを確認します。

```text
tenka_investigation {"action":"list"}
tenka_investigation {"action":"resume","investigationId":"保存済みID","approved":true}
```

引き継ぎは、ローカル資格情報を持つクライアントによる明示的な許可操作です。
自己申告のclientNameを本人確認には使いません。複数利用者を隔離するアカウント機構ではありません。
完了呼び出しの根拠メタデータ、小さい応答、実験と候補の所有情報を保存します。
元のサーバーinstanceId、成功・失敗・キャンセルの区別は保ちます。
大きなライブ専用結果、変更前バージョンのセッション所有権は復元できません。
保存に失敗した実行結果には `persistenceWarning` を返し、実行の成否を書き換えたり再実行したりはしません。

接続上限に達した場合は、調査セッションを作らない `tenka_sessions_list` / `tenka_session_end` を使います。
既定は16セッション・idle 15分。詳細は [ID省略とセッション管理](docs/mcp-workflow-defaults.md) を参照してください。

## ツールとID

現在の公開ツールは44個です。基本操作は上記の高水準の流れを使い、必要な領域へ進んでください。

| 領域 | 主なツール |
|---|---|
| 接続・読み込み | `tenka_connect` / `tenka_context` / `tenka_open_project` / `tenka_finish` |
| 対象・根拠 | `tenka_list_types` / `tenka_inspect_method` / `tenka_method_references` / `tenka_il` / `tenka_jit` |
| 調査・記録 | `tenka_review_queue` / `tenka_priorities` / `tenka_review_target` / `tenka_mark_review` / `tenka_review_diff` |
| 生成実験 | `tenka_prepare_experiment` / `tenka_run_experiment` / `tenka_experiment_result` / `tenka_compare_benchmarks` |
| 引き継ぎ・候補 | `tenka_investigation` / `tenka_propose_change` / `tenka_diff_proposal` / `tenka_review_workflow` / `tenka_apply_change` / `tenka_revert_change` |

完全な一覧と入力スキーマは接続先の `tools/list` を参照してください。
ソースツールのsnapshotId省略時は現在値へ固定し、明示した古いIDを勝手に置き換えません。
対象の読取では `tenka_review_target` に現在のmethodId/typeIdも渡せます。
確認記録は正規targetIdと正確なsourceHash/contextHash/expectedRevisionを使います。
確認キューの続きはnextCursorをcursorへ渡し、減るpending件数へoffsetを足し続けないでください。

## 承認と安全性

**作業ディレクトリのコピーはセキュリティサンドボックスではありません。**
MSBuild、テスト、ベンチ、生成コードは現在のOSユーザー権限で動き、元ディレクトリやネットワークへアクセスするコードも実行可能です。
パッケージキャッシュからの復元や `--no-restore` はネットワーク遮断ではありません。
信頼できる対象だけを選び、外部クライアントでも個別操作の承認を行ってください。

ホスト側の許可と `approved:true` は別です。後者はクライアントの申告で、人間のクリックの証明ではありません。
候補検証はホストのbuild/execution、trusted MSBuild、local execution、generated host experimentsの許可を必要とします。
適用・取り消しはホストbuildポリシーと別途の個別承認を必要とします。
`tenka_context.permissions` は条件付きの承認、生成実験の有効化、sourceEditsを分けて表示します。

最適化済みは作業の報告です。テスト合格や指摘消滅、命令数減少だけで性能改善を証明しません。
ベンチ比較も要約統計の観測で、統計的有意差や本番性能の保証ではありません。

## サンプルとランタイム

GameLabは小さな操作練習、RaidLabは複数プロジェクト、ILSpyは固定コミットの未改変OSSとして利用します。
GameLab/RaidLabに改善答案は同梱しません。サンプルでの成功を一般の検出能力とは区別します。
`run-raid-lab.bat` / `run-large-oss.bat` も利用できます。

本体の.NET 10と、対象・実験ランタイムは別です。
portable net8.0以降の生成実験は対象の版を維持します。
netstandard2.0/2.1のライブラリは別のportable .NET 8以降で検証し、省略時はnet8.0基準環境です。
.NET Standardのcsprojを勝手に書き換えたり、そのDLLを直接実行したりはしません。
完全な推移依存・ネイティブ依存や本番環境一致は残作業です。

## 更新と検証

本体とMCPアダプターを終了してから更新します。起動中のDLLを自分自身で上書きビルドしないでください。

```powershell
git pull --ff-only
.\start.bat
```

`check-tenka.bat` で本体ビルド・限定回帰テスト・接続検査を選べます。
新しいinvestigation/proposal検査もビルド検査に含めます。復元が必要ならsetupまたは検証側で明示的に許可してください。
テストが未実行・失敗・スキップした状態を全件成功とは扱いません。

**GitHub CIは停止したままです。新設・再開しないでください。** ローカル検査はCIやDockerを起動しません。

## 詳細資料

[最新の変更と限界](docs/investigation-proposals.md) / [外部連携](docs/external-agent.md) /
[引数検査とエラー](docs/mcp-tool-contracts.md) / [ID省略](docs/mcp-workflow-defaults.md) /
[プロジェクト調査](docs/project-investigation.md) / [確認記録](docs/review-progress.md) /
[大規模キュー](docs/large-review-workflow.md) / [実測](docs/performance-evidence.md) /
[IL/PDB](docs/binary-source-evidence.md) / [ランタイム](docs/experiment-runtime-and-checks.md) /
[セキュリティ](docs/security.md)

古い資料の「同一セッションだけ」「まだ引き継げない」という説明より、最新の変更資料を優先してください。
