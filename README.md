# 甜花ちゃん — Tenka-chan

C#/.NETのコードを、外部LLMエージェントから調査するための解析ツールです。
Roslyn、IL/PDB、JIT機械語、プロファイル、テスト・ベンチマークを組み合わせ、Webには調査の経過と結果を表示します。

**外部LLMエージェント → stdio MCPアダプター → 起動中の甜花ちゃん → Webで確認**

モデルの選択と推論、次に使うツールの判断は外部クライアントが担当します。
甜花ちゃんのブラウザーにモデルURLやAPIキーを入力する必要はありません。
単体の推論サーバーではなく、MCPツールを扱えるエージェント／クライアントが必要です。

> 開発中です。静的な候補、エージェントの作業記録、実測結果を区別します。
> 機能の実装と動作検証は別です。最新のC#ビルド・Windows実機・実MCP接続の完了を保証するものではありません。
> [今回の変更と検証範囲](docs/experiment-runtime-and-checks.md)を確認してください。

## 最初の起動

Windowsでは本体フォルダーの **`setup.bat`** を実行してください。
.NET 10 SDK、本体の依存関係、計測ツール、解析を許可するフォルダーを案内します。
次回からは **`start.bat`** です。通常利用にDocker・Node.js・Pythonは不要です。

セットアップが表示する **`mcp-config.json` を外部クライアント側に登録**します。
実際のDLL・接続ファイルのパスは自動入力されます。
設定は本体ごとに `%LOCALAPPDATA%\TenkaChan\launcher\<識別子>` に保存されます。
設定の登録方式は利用する外部クライアントに合わせてください。

**内部の `/agent/v1/*` はMCP HTTPの接続先ではありません。**
stdio MCPアダプターをコマンドとして起動します。
代わりに本体の `agent-mcp.bat` をコマンド起動型MCPに登録する方法もあります。

外部エージェントにビルド・対象アプリの実行も任せる場合は、セットアップの該当項目を許可してください。
MSBuild評価、生成テスト、計測対象のアプリは**現在のユーザー権限で動き、隔離はありません**。
信頼するコードだけを対象にし、外部クライアントの実行承認機能も使用してください。

詳しい設定は [外部エージェント連携](docs/external-agent.md)、
[Windowsセットアップ](SETUP-GUIDE.md)、[セキュリティ](docs/security.md)を参照してください。
古い資料中の内蔵LLM起動手順より、このREADMEと外部エージェント連携の説明を優先してください。

## 普段使う画面

| 入口 | 使う目的 |
|---|---|
| ダッシュボード | 読み込み・解析状況の概要 |
| プロジェクト調査 | 準備、コード調査、実行・検証、結果のタブから手動で調べる |
| 確認状況・差分 | 未確認、チェック済み、最適化済み、要再確認とソース差分 |
| エージェントの活動 | 外部エージェントのツール操作・経過・返却結果を見る |

個別のIL、プロファイルなどは折りたたんだ「詳細ツール」にあります。
手動の調査画面は残しますが、**AI連携の活動画面は表示専用**です。
ページを開いたり対象を選んだりしても、LLMや対象アプリを自動で起動しません。

`.sln` / `.slnx` / `.csproj` を一度選びます。
ソリューション内の複数C#プロジェクトを扱い、クラス・メソッド・DLL・PDB・結果を選択IDで引き継ぎます。
追加パスは通常不要です。複数候補は一覧から選び、曖昧なDLLを勝手に実行しません。
静的解析は読み込み済みソリューション、ビルドは選択したプロジェクトとその依存先が対象です。

## サンプルで試す

| 対象 | 用途 | 準備 |
|---|---|---|
| GameLab | 小さな題材でソース・IL・テストの操作を覚える | 「プロジェクト調査」の付属サンプルから選択 |
| RaidLab | 複数プロジェクトの集計処理を調べる | `run-raid-lab.bat` |
| ILSpy | 未改変の大規模OSSで調べる | `run-large-oss.bat`で取得後、復元・ビルド |

GameLab/RaidLabには現在の実装1つ、仕様、正しさのテスト、計測を残しています。
**用意済みの改善実装・改善答案は同梱しません。** 自作教材であり、自然な実コードでの検出能力の評価とは区別します。
ILSpyは固定コミットから取得し、ソースを甜花ちゃん向けに加工しません。
取得だけでは対象を実行せず、既存の編集をreset/cleanで消しません。

外部エージェントには、例えば次のように依頼します。

> 甜花ちゃんで読み込み済みのプロジェクトを確認してください。
> 未確認・要再確認のメソッドを選び、仕様と呼び出し元を調べ、必要な静的解析・IL・実測で根拠を確認してください。
> 実行前に承認を求め、確認した範囲と未確認事項を残してください。
> 変更後は差分と正しさを確認し、未計測の高速化を確認済みと報告しないでください。

## 外部エージェントのツール

現在のMCP公開ツールは46個です。旧`workspace_open`や内蔵レビュー起動ツールではなく、次の`tenka_*`を使います。

| 領域 | 公開ツール |
|---|---|
| 接続・対象 | `tenka_connect` / `tenka_context` / `tenka_sample` / `tenka_open_project` / `tenka_finish` |
| コード | `tenka_list_types` / `tenka_inspect_type` / `tenka_type_references` / `tenka_inspect_method` / `tenka_method_references` |
| 静的解析 | `tenka_scan` / `tenka_rules` / `tenka_priorities` / `tenka_test_inputs` |
| IL・JIT | `tenka_binary_methods` / `tenka_il` / `tenka_jit` / `tenka_artifact` / `tenka_native` / `tenka_compare_native` |
| ビルド・計測 | `tenka_build` / `tenka_trace` / `tenka_import_thread` / `tenka_runtime_costs` / `tenka_run_companion` / `tenka_dynamic_diagnose` / `tenka_allocation_stacks` |
| 生成実験 | `tenka_prepare_experiment` / `tenka_run_experiment` / `tenka_experiment_result` / `tenka_compare_benchmarks` |
| 確認・差分 | `tenka_review_queue` / `tenka_review_target` / `tenka_mark_review` / `tenka_review_diff` / `tenka_review_history` |
| 報告・結果 | `tenka_note` / `tenka_read_result` |

複数のMCPアダプターも、起動中の同じWebプロセスの`DashboardState`を利用します。
接続ごとに別のRoslynワークスペースを作る構成ではありません。
ビルドや再読み込み後は`tenka_context`で新しいsnapshotId・選択IDを取得してください。
接続ファイルの資格情報はサーバー再起動で変わるため、再起動後は`tenka_connect`で明示的に再接続します。

確認キューを処理しながら続ける場合は、返された`nextCursor`を次の`cursor`に渡します。
チェック済みを除いて件数が減るため、単純にoffsetを加算しないでください。
`hasMore=false`は索引内の現在の続きがないという意味で、全コードの確認完了ではありません。

## 生成実験のフレームワーク

**本体は.NET 10、調べるライブラリと実験を動かすランタイムは別です。**

| 調査対象 | 実験ランタイム |
|---|---|
| portable `net8.0`以降 | 選んだ対象フレームワークと同じ。勝手に本体の.NET 10へ変更しません |
| `netstandard2.0` / `netstandard2.1` | ホストのテスト・ベンチマーク向けにportable .NET 8以降を別途選択。省略時は.NET 8の基準環境 |

`tenka_prepare_experiment`の`targetFramework`はライブラリ側、`runtimeFramework`は.NET Standardライブラリを検証する実験側です。
結果には両方を残します。例えばライブラリを`netstandard2.0`のまま参照し、実験を`net8.0`で構築します。
既存.NET Standardライブラリのcsprojを.NET 8へ書き換えたり、そのDLLを直接実行したりはしません。

.NET 8の既定値は**本番環境を自動推定した結果ではありません**。本番条件を調べるときは外部エージェントが実際のランタイムを指定してください。
対応するSDK/ランタイム、ビルド済みDLL、キャッシュ済みパッケージが必要です。
複雑な推移依存・ネイティブDLL・外部サービスまで自動解決する機能は未完です。
.NET Standardの生成JITハーネス・旧コンテナー経路は対象外で、JITは呼び出し元の実行アプリを指定します。

## 動的解析（EventPipeとRoslynの連携）

`tenka_trace` で承認済みの.NETアプリを実行・収集した後、
保存済みartifactを `tenka_dynamic_diagnose` に渡すと、
観測されたメソッドの重いスタックとGC/割り当て/ロック競合・first-chance例外のプロセス概要を
**別々の根拠として**まとめて確認できます。
追加の`tenka_allocation_stacks`では、取得済みの小さなEventPipeトレースを一時的にETLX変換し、
サンプリングされた割り当てイベントのコールスタックをソース候補へ関連付けます。
呼び出し元メソッドごとの**サンプル数**の調査であり、メソッド別の正確な総割り当てバイト数ではありません。後者を個々のメソッドのCPUや
割り当て量として推測することはありません。
操作例と制限: [動的トレース診断](docs/dynamic-method-trace.md)。

## チェック済みと最適化済み

チェックは確認時点のソース・周辺コードのハッシュとともにSQLiteへ保存します。
確認後にコードが変わった対象は要再確認になります。
型とメソッドは独立した記録で、型のチェックを全メソッドに自動展開しません。
改名や署名変更を、別対象へ勝手に引き継ぐこともしません。

**最適化済みはエージェントの作業記録で、正しさや性能改善の証明ではありません。**
テストの期待値、比較条件、実測結果を別途確認してください。
差分は保存した確認時点と読み込み済みソースの比較で、Git全体の差分ではありません。
チェック履歴は再起動後も残りますが、活動タイムライン・生成実験のセッション所有権など、調査全体の再開には未対応部分があります。

## 更新とローカル検証

**甜花ちゃん本体と外部MCPアダプターを終了してから**更新します。
DLLを使用したまま自分自身を再ビルドすると、MSB3027/MSB3021で失敗します。

```powershell
git pull --ff-only
.\start.bat
```

依存関係の復元が必要な場合は`setup.bat`、画面はCtrl+F5、外部クライアントはツール定義の再取得を行います。
**`check-tenka.bat`** では、SDK確認、本体ビルドと対象の回帰テスト、起動中のMCP接続検査を選択できます。
結果は本体ごとのローカル設定フォルダーへ保存します。接続検査だけはPython 3.10以降を使用します。
Node.jsとPythonは開発・検証用であり、通常のWeb/MCP利用には不要です。

ビルド検証はサーバー停止中、接続検証は`start.bat`による起動後に行ってください。
未実行・失敗・スキップを全件成功と数えません。
この検査は本体の回帰テスト／接続検査であり、ILSpyの全テスト・任意アプリの安全性・速度改善を保証するものではありません。

GitHub CIは[停止中](.github/CI-PAUSED.md)です。ワークフローを勝手に再開・新設しないでください。
`check-tenka.bat`はローカルだけで動作し、CIやDockerを開始しません。

## 制限と安全性

ローカルループバック専用です。クラウド上のエージェントがそのまま手元のlocalhostへ接続できるわけではありません。
接続ファイルには秘密が入ります。共有やGit登録はしないでください。
サーバー側のビルド／実行権限と外部クライアントの実行承認は別です。
`approved=true`はクライアントからの申告で、人間のクリックの証明ではありません。

静的候補の多さは検出精度でもCPU負荷でもありません。
IL・機械語の命令数だけで高速化とは判断しません。
JIT取得には起動したアプリが対象メソッドを実際に呼ぶ必要があります。
.NETの版、インライン化、Tiered JITなどによりコード生成は変わります。
現在の対応付けだけでネイティブ命令とC#行の厳密な対応を保証しません。

大規模索引・履歴・結果の保存には上限があります。表示されない範囲を完了扱いにしません。
完全な推移依存解決、実測とソース変更の厳密な結合、統計的な性能回帰判断、再起動後の調査全体の再開は残作業です。

## 詳細資料

- [外部エージェントの接続と許可](docs/external-agent.md)
- [プロジェクト調査](docs/project-investigation.md) / [複数プロジェクト](docs/solution-workspace.md)
- [確認記録](docs/review-progress.md) / [大規模確認キュー](docs/large-review-workflow.md)
- [大規模OSSの取得](docs/large-codebase.md)
- [IL・PDBの根拠](docs/binary-source-evidence.md) / [実測の根拠](docs/performance-evidence.md)
- [新しい実験ランタイム選択と検証](docs/experiment-runtime-and-checks.md)

実装は [Roslyn](https://github.com/dotnet/roslyn)、[公式MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)、
[BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet)などを利用しています。
