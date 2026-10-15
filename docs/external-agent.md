# 外部LLMが調べ、甜花ちゃんが実行し、Webは経過を表示する

## 接続方向

外部のLLMエージェント → MCP（標準入出力）→ 甜花ちゃんのアダプター → 起動中のWebサーバーの解析エンジン。
Webの「エージェントの活動」は経過と結果をGETで取得して表示するだけです。ブラウザーにモデルURL/APIキーを設定せず、甜花ちゃんからモデルAPIに推論を依頼しません。モデル選択、推論、次のツールの判断、利用料金やモデル側のデータ送信は外部クライアントが担当します。生のLLM推論サーバーだけでなく、MCPツールを扱うエージェント／クライアントが必要です。

これは **stdio MCP** です。内部の `/agent/v1/*` は認証付きのローカル連携APIであって、MCP Streamable HTTP URLではありません。URL欄へそのまま登録しないでください。MCPクライアントは付属の.NETアダプターを起動します。Python、Node.js、Dockerは通常利用には不要です。

## 初回の更新と設定

1. 起動中の甜花ちゃんを黒いウィンドウのEnterで終了します。以前のMCPプロセスが接続中なら、外部クライアント側でも停止します。使用中DLLの上書きはしません。
2. 本体で `git pull --ff-only` を実行し、`setup.bat` を起動します。
3. 新しい質問で、外部エージェントの「プロジェクト読み込み・ビルド」と「JIT・計測・テストの実行」を個別に許可できます。既存設定からの移行時は両方とも未許可が初期値です。読み取りだけなら許可しなくても使えます。
4. セットアップが表示する **mcp-config.json** を外部クライアントのMCP設定に登録します。設定は本体ごとの `%LOCALAPPDATA%\TenkaChan\launcher\<識別子>\mcp-config.json` に保存されます。実際のdotnet/DLL/接続ファイルのパスは自動で入ります。クライアントの設定形式によってはmcpServersの内側を転記します。
5. 外部クライアントで「甜花ちゃんのMCPを使って、このプロジェクトの重い処理を調べて」と依頼します。Webに接続・レビュー開始ボタンを押す作業はありません。

コマンド起動型MCPに `agent-mcp.bat` を登録する方法もあります。BATはビルドせず、既にできているアダプターを起動します。MCP標準出力にメニューやログを混ぜません。設定例は次の形です（これは説明用。setupが作る実パス入りの設定を優先します）。

```json
{
  "mcpServers": {
    "tenka": {
      "command": "dotnet",
      "args": [
        "C:\\Projects\\tenka-chan\\src\\TenkaChan.Mcp\\bin\\Release\\net10.0\\TenkaChan.Mcp.dll",
        "--connection-file",
        "C:\\Users\\YOUR_NAME\\AppData\\Local\\TenkaChan\\launcher\\CHECKOUT_ID\\agent-connection.json"
      ]
    }
  }
}
```

通常の次回起動は `start.bat` です。最初に活動モニターを開きます。MCP接続ファイルは実際に起動したポートを使うので、5077が埋まって別ポートになっても設定済みのパスを再入力する必要はありません。手動起動では `--port 5077` または `--connection-file` を指定できます。接続情報はサーバー起動ごとに更新されます。再起動後は外部クライアントで `tenka_connect` を明示的にやり直してください。

## 外部LLMが使う36ツール

| 領域 | ツール |
|---|---|
| 接続・状態 | tenka_connect / tenka_context / tenka_sample / tenka_open_project / tenka_finish |
| クラス・ソース | tenka_list_types / tenka_inspect_type / tenka_type_references / tenka_inspect_method / tenka_method_references |
| 静的解析 | tenka_scan / tenka_rules / tenka_priorities / tenka_test_inputs |
| IL・機械語 | tenka_binary_methods / tenka_il / tenka_jit / tenka_artifact / tenka_native / tenka_compare_native |
| ビルド・計測 | tenka_build / tenka_trace / tenka_import_thread / tenka_runtime_costs / tenka_run_companion |
| 生成した実験 | tenka_prepare_experiment / tenka_run_experiment / tenka_experiment_result / tenka_compare_benchmarks |
| 確認状態・差分 | tenka_review_queue / tenka_review_target / tenka_mark_review / tenka_review_diff / tenka_review_history |
| 報告・結果の分割取得 | tenka_note / tenka_read_result |

一度プロジェクトを読み込めば、クラス、メソッド、DLL、PDB、保存先はIDで引き継ぎます。複数のDLLやオーバーロードは候補から選びます。古いsnapshotIdは拒否されるので、ビルド・再読み込み後はtenka_contextで最新状態を取得します。全MCP呼び出しが同じWebプロセスのDashboardStateを使います。別のMCPプロセスごとに別のRoslynワークスペースを作る設計ではありません。

例：GameLabのInventorySearchを探索 → CountOwnedの仕様・ソースと参照を読む → 調査候補を自分で選ぶ → 必要なILの確認や、仕様に基づいたxUnit/BenchmarkDotNetコードの準備を行う → 許可がある場合に実行 → 実際の結果を読む → 根拠を添えた報告を送る。順番や追加調査は外部エージェントが判断します。甜花ちゃん自身がLLMを回すループではありません。

生成実験は対象プロジェクトのソースを直接書き換えず、既存の実験領域に準備します。実行は別操作です。任意クラスの正しい初期状態、外部サービス、期待値は自動では決まりません。LLMは仕様や信頼できる参照実装を用いてテストを作る必要があります。コードの自動修正・コミット・任意シェルコマンドは新ツールに含めていません。

## Webに表示するもの

外部クライアントから受けたセッション、ツールの開始・結果・失敗・中止、LLMが送った計画・仮説・報告、選択可能な返却データを表示します。LLMからはユーザー向けの要約を受け取り、非公開の思考過程の送信は求めません。

**ツールの記録**と**エージェントの自己申告**は分けます。モデルの「直った」「速くなった」という文は検証済みに昇格しません。ツールの返却完了と、テスト成功・ビルド失敗など内部の状態も分けて残します。詳細結果の閲覧もGETだけです。履歴は起動中のメモリー内、イベント最大500件、セッション最大16個、1セッション最大200呼出し。全体の結果保存は32MB、1結果8MBを上限に古い結果を破棄します。6万バイトを超える結果はtenka_read_resultでJSON pointerとページを指定して取得します。

最後の通信から15分間活動がないセッションは期限切れになります。監視画面を開いていても期限は延びません。これはモデルの内部推論の生存確認ではなく最終通信時刻です。実行中ツールは実行中として残します。サーバー再起動後にこの活動履歴を復元する機能は未追加です。既存の実験成果物の保存とは別です。

手動のクラス調査・計測画面は残しています。AI連携の入口は監視専用に置き換えました。旧 `/api/reviews/start` は410を返し、古い画面から内蔵LLMを開始できません。旧推論クラスは互換性・単体テスト用ソースとして残りますが、Web/MCPの本番エントリーポイントでは登録しません。旧MCPの直接解析用ツール一覧は、新しい共有ワークスペース用36ツールに置き換わるため、既存のプロンプトも更新してください。

## 許可と保護

- ローカルのループバックのみ。別PCやクラウドのMCPクライアントが、直接このlocalhost URLへ届く機能はありません。
- アダプターは毎回のサーバー起動で発行されるBearer資格情報をローカルの接続ファイルから読みます。ブラウザーのCSRFトークンを流用せず、モデルにAPIキーを渡しません。接続ファイルには秘密が入るので共有・Git登録しないでください。MCP設定ファイルには秘密そのものは入りません。
- machine APIはOriginやSec-Fetch-Siteが付いたブラウザー要求を拒否します。既存Web APIのCSRFチェックは緩めません。リダイレクト・HTTPプロキシ・外部ホストへの資格情報転送もしません。
- 自動実行にはサーバー側の `TENKA_AGENT_ALLOW_BUILD=1` / `TENKA_AGENT_ALLOW_EXECUTION=1` が別々に必要です。従来の信頼済みMSBuild/ローカル実行/生成実験フラグも維持します。読み込みだけでもMSBuild評価があるためbuild許可が必要です。
- 各実行で `approved=true` も要求します。ただしこれは**外部クライアントが申告する承認**であり、サーバーが人間のクリックを証明するものではありません。外部クライアントの承認機能を使い、信頼するワークスペースにだけサーバーの実行権限を与えてください。
- 同じrequestIdを再送しても同じ処理を二重実行しません。通信エラー時にアダプターがツールを自動再試行することもありません。処理が始まっていた可能性は監視画面で確認します。
- 中止・期限は協調的であり、既存の実行エンジンの子プロセス終了を完全保証しません。ビルド・計測・生成コードは隔離なしです。ローカルの同じOSユーザーによる悪意ある操作を防ぐサンドボックスではありません。

## 初期実装時の検証記録（現在版の実機成功を意味しません）

この変更で実際に実行したのは、Node.jsの122テスト（新規22、既存100）と、Chromiumの59確認項目（1440/1024/768/390/320幅の模擬APIによる画面操作、XSS文字列の非実行、GETだけの取得、旧レビュー欄の非表示）です。JS構文・変更差分の空白も確認しました。Chromium検証では既存styles.css/旧app.jsを省略し、実際の新HTML・新JS・共通シェル/CSSを使用しています。

.NET SDKがこの作業環境にないため、**C#のビルド、追加26ケースのC#単体テスト、本物のMCPクライアントとASP.NETを通した接続、WindowsのBAT/PowerShell、対象アプリの実行は未検証**です。模擬UI検証を実接続成功とは扱っていません。GitHub CIは再開していません。

ローカル検証は、甜花ちゃんとアダプターを終了した後に `scripts/test-agent-integration.ps1 -Restore` を実行します。Node.jsはUI開発テストだけに必要です。実サーバーとMCPの確認用に `tests/Fixtures/agent_protocol_smoke.py` も用意しています（当環境では未実行）。

MCP標準入出力の仕様: https://modelcontextprotocol.io/specification/2025-11-25/basic/transports
MCP SDK: 既存のModelContextProtocol 2.2.0を使用。新たなNuGet依存関係は追加していません。

## 生成実験と現在版の検証

.NET Standardライブラリの生成テスト・ベンチマークでは、`targetFramework`で対象ライブラリのTFM、`runtimeFramework`で実験の.NETランタイムを指定できます。省略時の.NET 8は基準環境であり、本番環境の推定ではありません。ライブラリ本体を.NET 8へ書き換えず、ビルド済みDLLをホストで参照します。生成JIT・旧コンテナー経路はこの分離に未対応なので拒否します。

`check-tenka.bat`に、SDK確認・停止中のビルド/回帰テスト・起動中のMCP接続検証をまとめています。接続検証は自動でパスを解決し、ツール名だけでなく入力スキーマも確認します。接続検証のみPython 3.10以降が必要です。通常利用の依存関係は増やしていません。詳しくは[実験ランタイムと検証](experiment-runtime-and-checks.md)を参照してください。
