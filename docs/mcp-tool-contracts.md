# MCPの引数検査・統一エラー・レビューIDの受け渡し

2026-10-16。PR #20の `93fe8b8` に続く実装です。公開ツールは38個のままです。
目的は、調査結果の数を増やすことではなく、引数エラーからの復旧とIDの取り回しを減らすことです。
通常のエージェントとの作業時間比較はまだ実施していません。

## 引数の不足・型違いを呼び出し前に返す

公式SDKの `WithRequestFilters(...AddCallToolFilter...)` に検査を登録しました。
登録済みの2クラスのC#引数宣言から、引数名、必須／省略可能、null許容、型を読みます。
DIで渡すクライアントとCancellationTokenは対象外です。現在の全ツールが使うstring、bool、int、long、string[]とそのnull許容を扱います。
未知の型を新しいツールへ追加した場合は検査側の更新も必要です。汎用JSON Schema実装ではありません。

例えば `tenka_inspect_method {}` は `missingArguments:["methodId"]` を返します。
`tenka_review_queue {"limit":"50"}` は `invalidArguments:[{"name":"limit","expected":"32-bit integer (not null)"}]` を返します。
数値範囲、boolの文字列化、非null引数へのnull、文字列配列内のnull/数値も検査します。
知らない引数は件数と正しい引数名一覧で案内し、不正な引数名や値そのものは返しません。

この検査で拒否した呼び出しではツール本体へ進まず、調査セッションも作りません。
`phase:"argument_validation"`、`operationStarted:false`、`automaticRetry:false` を返します。
ただし、値の意味・承認・ワークスペース・ハッシュなどの検査は引き続き本体で行います。
`approved` を自動でtrueにする処理や、失敗要求を再送する処理はありません。

## ツールエラーの外形を揃える

既存38メソッドの戻り値を `Task<CallToolResult>` とし、共通の応答処理を通します。
意図的に公開する `AgentClientFailure` の安全なJSONを、そのまま次へ返します。

- MCPの `isError:true`
- 接頭辞のない、直接JSONとして読める `content[0].text`
- 同じJSONオブジェクトの `structuredContent`

既存のcode/message/sessionId/requestId/callId/httpStatus/automaticRetry/nextActionを保持します。
不明な例外や残ったSDK内部エラーは、内部メッセージを公開せず `internal_error` にします。
キャンセルとプロトコル例外は握りつぶしません。未知のツール名や壊れたJSON-RPCエンベロープなど、
ツール呼び出しへ到達する前のプロトコルエラーまでこの形式へ変換したわけではありません。

成功時は従来のJSON文字列を1つのtextブロックでそのまま返します。成功ペイロードやツール引数名は変えません。
C#メソッドを直接呼ぶコードは、戻り値がstringではなくCallToolResultになった点に対応してください。

## priorities → review_target → 調査／記録の受け渡し

`tenka_review_target` の `targetId` 引数は、参照時に限り現在の正確なmethodId/typeIdも受け取ります。
したがって、prioritiesで得たmethodIdをそのまま渡せます。曖昧な一致、表示名からの推測、古い選択IDの救済は行いません。

応答のtargetには、従来のid/selectionIdに加えてtargetId/methodId/typeIdの別名を追加しました。
さらに `navigation` が次を返します。

- 正規のtargetIdと現在のmethodId/typeId。
- `nextActions`：inspect_method、method_references、review_diffなど、スナップショットを明示した参照専用のツール名と引数。実際には実行しません。
- `reviewInputs`：snapshotId、targetId、sourceHash、contextHash、expectedRevision。status、summary、evidenceは自動作成しません。

読み込み後にソースが変わっている場合は、次の操作と記録用入力を返さず再読み込みを促します。
個別記録不可の対象には記録用入力とdiff操作を出しません。
**書き込みのmarkは従来どおり正規のtargetIdだけを受け付け、ソース・コンテキストハッシュとrevisionを検査します。**
この変更はワンストップ検証や自動レビューではなく、既存の操作の間をつなぐものです。

## 0行差とコンテキスト差を区別

`tenka_review_diff` に `comparison` を追加しました。比較に選んだチェック点同士、またはチェック点と読み込み済みソースについて、
新旧source/contextハッシュと、それぞれが変わったかを返します。保存履歴同士の比較では現在のコンテキストを混ぜません。
比較元がないときはchangedをfalseではなくnullにします。

**コンテキストの構成要素を過去に保存していないため、どの依存ファイル／設定が原因かを特定する機能ではありません。**
`contextInputsAvailable:false` と明記します。needs_reviewを抑制したり、0行差を確認済みの根拠にしたりはしません。
再起動だけで再確認になるという報告の原因特定も未完です。

## ローカル検証の入口

`check-tenka.bat` のビルド検査に、PR #19のエラー検査、PR #20のID／セッション検査、今回の2クラスを追加しました。
接続検査には、実在しない接続ファイルを指定して動かす `agent_binding_probe.py` を追加しました。
このプローブは、ビルド済みの実MCPアダプターをstdioで起動し、引数エラーと統一エラーを9ケース検査します。
Webサーバー、対象プロジェクト、LLMには接続しません。プローブ自身が起動した子プロセスだけを終了します。
通常利用にPythonを追加する変更ではなく、Pythonは従来の接続検査と同じ開発用の前提です。

本体とMCPアダプターを終了し、依存関係復元後、.NET 10 SDKがある環境で次を使えます。

```powershell
dotnet test tests\TenkaChan.Tests\TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentToolContractTests|FullyQualifiedName~ReviewSelectionBridgeTests"
python tests\Fixtures\agent_binding_probe.py --adapter-dll src\TenkaChan.Mcp\bin\Release\net10.0\TenkaChan.Mcp.dll
```

## 今回実施できた検査と限界

実行済み：プローブ自身のPythonテスト8件（偽のstdio子プロセスとの通信を含む）、ソース構造テスト3件（保護を壊す7種類の改変を拒否）、
既存38ツールの引数名・既定値・転送値の同一性比較、C#11ファイルの字句的な括弧対応、gitの空白検査。
旧workflowソース検査は戻り値の表記に追従させ、カタログ抽出が38ツールを認識することを検査しました。旧スイート全体を再実行したわけではありません。

C#回帰テスト44ケース（AgentToolContractTests 29、ReviewSelectionBridgeTests 15）を追加しましたが、**未実行**です。
作業環境には.NET SDK／PowerShellがなく、SDK配布先の名前解決も失敗しました。
**本体ビルド、44ケース、実SDKの9ケース、ASP.NET、本物のWindows／opencodeの動作確認は未実施**です。
Pythonの偽通信やソース検査をこれらの成功として扱いません。

残作業：evidence／実験所有権の安全なセッション間引き継ぎ、コンテキスト入力の永続差分、非破壊の候補コピーとapply/revert、
ワンストップのテスト・ベンチ比較、review_batch、永続ベースライン管理、ツールあり／なしの調査時間比較。
CIは停止したままです。Docker、新しいパッケージ、UIメニュー、サンプルの改善答案は追加していません。

SDKの公開契約：
- https://csharp.sdk.modelcontextprotocol.io/v2/concepts/filters.html
- https://csharp.sdk.modelcontextprotocol.io/v2/api/ModelContextProtocol.Protocol.CallToolResult.html
- https://csharp.sdk.modelcontextprotocol.io/v2/api/ModelContextProtocol.Server.McpServerToolAttribute.html
