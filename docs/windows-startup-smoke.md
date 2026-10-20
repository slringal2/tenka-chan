# Windows ランチャー実起動スモークテスト

対象: [RaidLab 検証フィードバック 第3回](validation-feedback-2026-10-18-raidlab-fix3.md) の
「PowerShell 5.1パーサは検証済みだが、実起動は未確認」という残課題。

## 実行方法

Windows 11・PowerShell 5.1・.NET 10 SDKで、既に **setup.bat を実行済み**であること。
起動中の甜花ちゃん・同じRelease DLLを使用するMCPアダプターを先に閉じる。
（検証コマンドは既存プロセスを終了しません。）

```powershell
# tenka-chanリポジトリ直下
.\verify-start.bat
```

同等のコマンド:
```powershell
powershell.exe -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File scripts\tenka-windows.ps1 -Mode Start -VerifyStartup
```

起動プローブは **保存済み設定のみ**を使用する。初回setupのユーザー確認、
NuGetダウンロード/SDKインストール、解析対象のopen・build・テスト・測定、
ブラウザー起動、LLMモデル起動は行わない。

ただし通常のStartと同じ、ローカル **Release Webビルド** と
`TenkaChan.Web` ホストの実起動を行う。信頼できるリポジトリでのみ実行すること。
Build targetsはOSユーザー権限で実行される（OSサンドボックスではない）。

## 確認する条件

1. 設定ファイルが存在し、許可ルートが検査を通る（変更しない）。
2. 対応する.NET SDKが利用可能で、Releaseビルドに成功する。
3. 設定ポートが空いている。**使用中なら別ポートで成功扱いせず停止**し、
   既存プロセスは触らない。
4. 起動したWebサーバーの `GET /api/session` がloopbackで応答し、
   port、MSBuild、Docker無効、host実験の許可が保存済み設定と一致する。
5. ローカルMCP接続ファイルが同じポートを参照し、
   認証つき `POST /agent/v1/begin` が成功する。
6. テスト用エージェントセッションを `/agent/v1/end` で終了し、
   **今回開始したWebプロセスのみ**を終了する。
7. 強制終了により接続資格情報が残った場合、
   **検証中に読み取った同一 `instanceId` のファイルに限り**削除する。
   資格情報とCSRFトークンはログに出さない。

成功時はコンソールに `実起動検証に成功: HTTP / MCP認証 / ポート...` と表示する。
詳細ログは通常と同じ `%LOCALAPPDATA%\TenkaChan\launcher\<識別子>\logs`。
このプローブは実行して初めて検証になる。静的コード検査だけでは成功を主張しない。

## よくある失敗

- 設定が存在しない: `setup.bat` を一度実行する
- Release DLLが使用中: 既存サーバー/アダプターを**自分で**終了する
- ポートが使用中: 使用者を調べる。検証スクリプトは既存サービスをkillしない
- SDK/Build失敗: 最初のエラー行をログで確認。自動修復やCI起動は行わない
- MCP接続ファイル/認証失敗: 起動したWebプロセスと接続設定の整合性を確認

## この検査で証明しないこと

- 実際のRaidLabの `.slnx` open、JIT、実測の成功
- 対象ソースとビルド済みDLLが一致すること（`binariesVerified` はfalseのまま）
- モデルクライアント側のstdio MCP登録が成功すること（別途実クライアントから接続を確認）
- 最新C#の全xUnit回帰テスト

CIは再開しない。Dockerも起動しない。
