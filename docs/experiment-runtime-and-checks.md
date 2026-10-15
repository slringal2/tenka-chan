# ライブラリの生成実験とローカル検証

## 今回の変更

`.NET Standard`はAPIの契約で、実行するランタイムとは別です。従来は両方を一つのTargetFrameworkとして扱っていたため、IL用に検出したnetstandard2.0/2.1ライブラリを、そのまま生成テストへ引き継げませんでした。

`ExperimentFrameworkPolicy`で、対象ライブラリの`SubjectTargetFramework`と、実験側の`TargetFramework`を分離しました。portable net8.0以降は従来どおり同じランタイムを使います。netstandard2.0/2.1では、ホスト上のxUnit/BenchmarkDotNet実験をportable .NET 8以降で作成します。runtimeFrameworkを省略した.NET 8は、明示した基準環境であり、本番ランタイムを推定したものではありません。

既存MCPツール`tenka_prepare_experiment`に、任意の`runtimeFramework`引数を追加しています。ツールの数は36のままで、接続設定・サイドバー・追加パス入力は増やしていません。例えば、対象がnetstandard2.0のプロジェクトなら、`targetFramework="netstandard2.0"`, `runtimeFramework="net8.0"`で準備します。旧来のnet8.0プロジェクトにruntimeFrameworkだけnet10.0を指定するような、黙った実行条件変更は拒否します。

ライブラリ本体は既にビルドされたDLLを参照します。対象のcsprojを.NET 8へ書き換えず、netstandardのDLLを単体起動もしません。隣接DLLのコピーは既存の方式を使用し、複雑な推移依存・ネイティブDLL・DIや外部サービスの自動構築まで完成したものではありません。複数の同順位出力は拒否し、違うTFMのDLLへ自動フォールバックしません。

マニフェストと作成結果に対象TFMと実験ランタイムを残し、ホスト実行結果の説明にも両方を表示します。旧マニフェストは追加フィールドがなくても、従来の同一TFMとして読み取れます。準備済みコードのハッシュ検査・許可フォルダー・別個のホスト実行承認は維持します。

**対応範囲はホストの生成テスト・ベンチマークです。** .NET Standardに対する生成JIT、および旧コンテナーのProjectReference経路は拒否します。実行アプリを通常起動する既存のJIT/トレース機能は別です。これを理由にDockerを新たに必要としたり、CIを再開したりしません。

## 手元での確認

本体ルートに **check-tenka.bat** を追加しました。選ぶのは次の3つです。

| 選択 | 内容 | 実行条件 |
|---|---|---|
| 1 | SDK・ランタイム一覧の確認 | ビルドや対象の実行はしません |
| 2 | 本体ビルドと関連C#回帰テスト | 甜花ちゃんと外部MCPアダプターを終了し、実行を承認します |
| 3 | 起動中のWebとstdio MCPの接続・契約確認 | start.batで起動後。検査用にPython 3.10以降が必要です |

2ではNuGet復元の許可を別に確認します。失敗した工程では停止し、古いDLLを起動して成功扱いにはしません。対象のDLLが使用中の場合も、プロセスを強制停止せず、解除方法を表示します。TRXがない、実行0件、失敗やスキップがある場合は全件成功扱いにしません。選択した回帰テストの成功は、任意プロジェクトの安全性や全機能の実機検証とは別です。

結果は `%LOCALAPPDATA%\TenkaChan\launcher\<本体の識別子>\checks\<実行時刻>` に、`summary.json`と工程ごとのログを保存します。対象コード、Git、CI設定やチェック履歴は変更しません。接続トークンや全環境変数を収集せず、外部へログを自動送信もしません。ただしビルドログにはローカルパスやテスト出力が含まれ得るので、共有前に確認してください。

個別実行もできます。

```powershell
# 甜花ちゃんとMCPアダプターを終了した後
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-tenka.ps1 -Mode Build -ApproveBuild -Restore

# start.batで起動した後。通常のWindowsセットアップなら追加パス指定は不要
python tests/Fixtures/agent_protocol_smoke.py
```

検証スクリプトは.NET SDKやPythonを自動インストールしません。通常の甜花ちゃん利用にNode/Pythonを必須にしたわけではありません。DLLロック検査は検査時点の状態確認であり、その後の競合を完全に防ぐものではありません。

## MCP接続検証の強化

従来の「ツール数が36個あるか」だけでは、同じ数の古いDLLを検知できません。現在のProgram.csで登録しているツールクラスから期待する名前を読み、MCPのtools/listの続きを取得して全体と比較します。さらに、生成実験のruntimeFramework、確認キューのcursor/trackableOnly、マークの期待revision等の入力スキーマを確認します。

接続ファイルとアダプターDLLの場所は本体の位置から自動解決します。別インスタンスでは引き続き`--connection-file`/`--adapter-dll`を指定できます。接続ファイルは最大サイズ、BOM、形式を検査し、localhostはDNSを使わずループバックへ正規化します。外部URL、プロキシ経由、リダイレクトによる資格情報転送は許可しません。tools/listの繰り返しcursorや無限ページングも拒否します。

接続検証は、接続、状態の読取り、テスト用の未検証メモ、終了通知を確認します。マーク変更、対象のビルド・実行、LLM推論はしません。テスト用のセッション・メモは起動中の活動履歴に残ります。読み込み済みソースがある場合、その内容が応答に含まれ得ますが、標準出力にはソース本文や秘密を表示しません。

## 説明書の整理

READMEを現在の「外部エージェント→共有Webプロセス→表示」という構成に書き換えました。内蔵モデルの起動設定、旧MCP名での作業、WebとMCPを別ワークスペースとして扱う案内、CIが稼働中という説明は、現行手順から外しています。現在の36ツール、主要4画面、サンプルと制限、チェック記録の意味をまとめました。external-agent.mdのツール一覧も確認・差分の5ツールを含めて更新しました。旧実装のソースや履歴は消しません。

## 今回実行した検証と未実行の範囲

- **Node.js 133テスト成功**：取得済みの関連UIクライアントの125テストと、新しい文書・配線・スクリプトの8検査。C#のコンパイルを代替するものではありません。
- **Python 18テスト成功**：接続ファイルの検証、既定パス、現在のツール定義、入力スキーマとtools/listページングを検査。
- **Pythonの結合テスト2件成功**：新しい接続検証スクリプトを実際に子プロセスで起動し、ローカルHTTPとstdioを通して検査。ただしサーバーとMCPアダプターはPython製の模擬実装です。本物の.NET/公式SDK接続成功ではありません。
- Python構文、Git差分の空白、関連元ファイルのGit blob一致を確認しました。

C#は新しいフレームワーク選択・マニフェストの29ケースと、DLL選択の2ケースを追加しています。**この31ケースは未実行です。** この作業環境には.NET SDKとPowerShellがなく、SDKダウンロードもDNS解決エラーで失敗しました。本体ビルド、WindowsのBAT/PowerShell、本物のMCP接続、実ILSpyに対する生成実験の成功は確認していません。

次に必要なのはcheck-tenka.batの2→start.bat→3を実機で通し、その後で実際のライブラリに対する生成実験を確認することです。性能改善の実測値はありません。CIは停止したままです。

参考仕様： [.NETのTFMと.NET Standard対応](https://learn.microsoft.com/ja-jp/dotnet/standard/frameworks)、[MCP stdio](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)。
