# MCPの失敗理由と通信待ちの改善（2026-10-16）

## 今回の対象

外部エージェント → stdio MCPアダプター → 起動中のWebプロセス、という構成は変更しません。
main `b4407daae1a9dbcd949b8618b81380f99d99f666` から、アダプターの失敗報告・通信待ちを修正しました。
公開ツール名・入力スキーマ・成功時のAgentReply形式は変更していません。
Dockerや新しいパッケージは不要です。CIは停止したままです。

以前のチャットのMCPフィードバック全文は今回取得できていません。
本変更は、現行コードとSDKの公開契約で確認できた問題への修正であり、前回の指摘すべてを解消したという報告ではありません。

## 修正内容

- 従来の `InvalidOperationException` ではSDKに詳細を隠されるため、意図的に外部へ返すエラーだけを `McpException` 派生型へ変更しました。未知の例外を一括で公開する設定にはしていません。
- `code`、安全な `message`、判明している `sessionId` / `requestId` / `callId`、`httpStatus`、`automaticRetry: false`、`nextAction` をエラーのテキストにJSONとして含めます。これはMCPの `structuredContent` ではありません。クライアントはSDKのエラー接頭辞を付ける場合があります。
- HTTP 400/403/408/409/413では、最大16,000バイトの `application/json` に含まれる文字列 `error` だけを詳細として読みます。HTML、problem details、HTTP 500の例外情報は公開しません。巨大・不正な本文でもHTTPステータスを残します。
- 現在の接続トークン、Bearer形式、64桁の16進資格情報らしき文字列を、切り詰める前に除外します。一般のアプリ固有シークレットすべてを検出する機能ではありません。エラー詳細は最大1,600文字に制限します。
- 7分の期限をレスポンスヘッダーだけでなく、応答本文の受信にも適用します。呼び出し元のキャンセルはキャンセルのまま伝播します。
- 通信断・時間切れを「対象処理は実行されなかった」とは扱いません。要求IDを残し、結果不明として活動の確認を促します。自動リトライはありません。
- 古いセッションの終了要求が拒否された場合でも、明示的な `tenka_connect` から再接続できます。過去の解析・実行要求は再送しません。
- 成功応答の形式、セッションID、結果のcallId・outcomeを検査し、不正な返答を成功として返さないようにしました。

ホストの実行許可、クライアント側の個別承認、loopback限定、プロキシ・リダイレクト禁止、同時呼び出し拒否は維持しています。
`ValidateConnection` の本体は基準コミットと同一です。

## 検証

実行済み：

- `python scripts/test-agent-client-contract.py`：ソース構造の検査12件。
- C#3ファイルの字句ベースの括弧対応検査。
- `ValidateConnection` と取得した基準ソースの同一性比較。
- `git diff --check`。

上記はC#コンパイル・実行テストではありません。

追加した `DashboardAgentClientFailureTests` は30ケースです。偽のHttpMessageHandlerを介して、実際のC#クライアントの呼び出し・エラー処理を検査するテストです。
失敗/キャンセル結果、HTTPエラー、巨大/不正な本文、資格情報除去、通信断、本文待ちの期限、呼び出し元のキャンセル、同時要求拒否、明示的再接続、成功形式、接続ファイル不備を対象にしています。

**未実行**：C#ビルド・上記30ケース・実SDKによるstdio通信・Windows/PowerShell 5.1・opencodeとの接続。
作業環境に.NET SDK/PowerShellがなく、SDK配布先のDNS解決も失敗しました。偽HTTPのC#テストも実行したとは報告しません。

.NET 10 SDKと復元済みの本体依存関係がある環境では、甜花ちゃんと外部MCPアダプターを終了してから実行します。

```powershell
dotnet test tests\TenkaChan.Tests\TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DashboardAgentClientFailureTests" --logger "trx;LogFileName=agent-client-failures.trx"
```

このテストはHTTPハンドラーを差し替えるため、対象アプリ・MSBuild解析・LLMは起動しません。
ただし、テストプロジェクト自体のビルドには.NET SDKと復元済み依存関係が必要です。
この追加クラスが既存 `check-tenka.bat` の選択フィルターに含まれることは確認していません。上記の明示的フィルターを使ってください。

## 残っている確認

SDKが実際に `isError` とエラーテキストを返すこと、opencodeがそれを表示することは実接続で確認が必要です。
SDK内でツールメソッドへ到達する前に起こる引数バインドエラーは今回の対象外です。
再起動後の調査履歴・生成実験所有権の引き継ぎや、大規模プロジェクトの検証は今回変更していません。

SDKの根拠：
https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.Server.McpServerTool.html
https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.McpException.html
