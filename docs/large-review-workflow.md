# 大規模コードの確認を続けるための改善

対象は外部エージェントの確認キュー、確認履歴・差分の再利用、.NET Standardライブラリの読取用DLL検出です。GameLab / RaidLabに改善答案を戻したり、ILSpyのソースにパッチを当てたりはしません。外部エージェントがMCPで接続し、Webはその結果を表示する構成を維持します。新しいメニューやモデル接続設定はありません。MCPツール名も36個のままです。

## 1. チェックを付けながら、一覧の続きを安全に取得する

従来のpending一覧をoffset=0で50件取得し、その50件をチェック済みにした後にoffset=50を指定すると、未確認集合自体が縮んでいるため次の50件を飛ばし得ます。新しい`tenka_review_queue`は、最後に返した対象の固定IDを基準にする`nextCursor`を返します。

最初の呼出し:

```json
{"snapshotId":"CURRENT_SNAPSHOT","state":"pending","kind":"method","limit":50,"trackableOnly":true}
```

取得した対象を調べ、現在のハッシュ・revisionを確認して記録します。その後、**同じ条件のまま**次を呼びます。

```json
{"snapshotId":"CURRENT_SNAPSHOT","state":"pending","kind":"method","limit":50,"trackableOnly":true,"cursor":"PREVIOUS_RESPONSE.nextCursor","offset":0}
```

状態ではなくプロジェクト・パス・宣言名・種別・固定IDの順で並べます。前ページの対象がchecked/optimizedに変わっても、続きの位置はずれません。cursorと0以外のoffsetの併用、不正cursor、別snapshot/プロジェクト/検索条件/状態/kindへの持ち越しは拒否します。古いoffset呼出しは互換性のため残りますが、記録しながら全件を巡回するときはcursorを使ってください。要再確認を先に調べたい場合はstate=needs_reviewを指定します。

`hasMore=false`は「この位置より後に、読み取り時点で条件に一致する索引内の対象がない」という意味です。途中で前の対象が再び未確認になった場合は先頭からもう一度pendingを取得します。**キューの終端は、全コードの確認完了や誤りがないことの証明ではありません。** 索引上限・対象外の宣言は以前のまま残ります。複数エージェントに対する排他的な仕事の割当てでもありません。記録時のexpectedRevisionによる競合検出は維持しています。

`trackableOnly=true`で局所関数など個別マーク不可の対象を作業キューから除外できますが、summaryのuntrackable/blockedPendingは隠しません。blockedPendingは個別マーク不可または再読み込みが必要な未確認・要再確認の対象数です。requiresReloadがある場合はソースを再読み込みします。対象を除外してもチェック済みにはなりません。既存のSource本文JsonIgnoreも維持しています。

Webの「確認状況・差分」の「さらに表示」もcursorを使います。表示済み件数と現在の一致件数を分け、重複IDは重ねて表示せず、新しいsnapshotの応答を古い一覧に追加しません。GETのみで、閲覧時の記録変更や自動実行はありません。

## 2. 確認状態と差分の再計算を減らす

ReviewProgressStoreは、SQLiteの追記済み最大revisionを確認し、変更がなければ既に復元した最新チェック一覧を再利用します。変更を確認するSQLと一覧の読取りは同じ読取りトランザクションで行います。別プロセス・別ストアが追記した場合も最大revisionの更新から検出します。Saveの後は自分のキャッシュも無効化します。返却済みの一覧を後から書き換えません。

これは既存APIの追記専用DBを前提としています。DBを外部ツールで直接書き換えたり、同じrevisionを持つ別ファイルへ置き換えたりした場合は、サーバー再起動が必要です。既存の履歴を削除せず、スキーマ変更や新規NuGet依存関係は追加しません。

行差分は、比較元と比較先の**本文全体の正規化ハッシュ**をキーにして計算結果を再利用します。2ページ目以降に同じLCS計算をやり直しません。本文が変われば別の差分になります。保持上限は8組、保持する行テキスト計160万文字です。超過時は古いメモリーキャッシュだけを捨て、SQLiteの確認履歴は残します。

元からある行数・本文サイズ・計算量上限、および大きな変更を区間置換で表示する制限は維持します。命令やソースの同値性の証明ではなく、ソース確認時点同士の行差分です。実環境での何倍高速化という数値は未計測です。

## 3. .NET StandardのDLLをIL調査へつなぐ

DLLの自動検出に`netstandard2.0`と`netstandard2.1`を追加しました。従来の.NET 8以降の実行用出力と区別し、**IL/メタデータ調査用のライブラリ候補としてのみ**返します。例えばILSpyのDecompilerプロジェクトのように、実行入口とは異なるターゲットの中核ライブラリを選べるようにする変更です。

フォルダー名だけでなくPE内のTargetFrameworkAttributeを確認し、不一致の候補は採用しません。標準の出力先と明示的なOutDir等の既存ヒントを使い、許可範囲外を読まず、MSBuild評価やアセンブリのロード・実行は行いません。

.NET Standardはランタイムではありません。エントリーポイントやruntimeconfigがあっても直接起動可能とは扱わず、RequireBuiltProjectの実行対象は従来どおり.NET 8以降です。JIT/トレースは参照する実行アプリを選び、その実行中にメソッドが呼ばれる必要があります。ソースとバイナリの同一性、PDBの照合、実際のDLLロード経路は別の検査・制限です。新しいコードが実際のILSpy成果物で最後まで動作したという主張ではありません。

## 更新と検証

甜花ちゃん本体と外部MCPアダプターを終了し、`git pull --ff-only`、`start.bat`で更新します。ブラウザーはCtrl+F5、外部クライアントはMCPのツール定義を再取得してください。接続パス・APIキー・新しい手入力は増やしていません。差分履歴やチェック済みマークを消す必要はありません。

この変更で実行した検査:

- Node.js: 既存16件＋追加15件、計31テスト成功。cursorの引継ぎ、状態変更中のページ追加、重複ID、新snapshot拒否、読取専用を検査。
- Chromium: 5画面幅で新しい続き取得70項目＋既存の確認/差分画面65項目が成功。実際のHTML/新JS/共通CSSを使用、APIは模擬GET応答。既存styles.cssは分離DOM検証では省略。本物のASP.NET/MCP接続の検証ではありません。
- SQLite: C#ソースから抽出した実際のSQLに対して16項目成功。2接続を使い、同じ読取りトランザクション中と終了後で、追加revisionと一覧の対応が変わることを確認。C#のキャッシュサービス自体の実行ではありません。
- JavaScript/Python構文、差分の空白、変更前ファイルのGit blob一致を確認。

C#には新規34ケース（キューページング20、キャッシュ5、合成PEによる.NET Standard検出9）を追加しました。2,000対象を途中でチェックしながら巡回するケースも含みます。ただし、**この作業環境には.NET SDK / PowerShellがなく、C#ビルド・この34ケースの実行・Windows・実ILSpyのビルドと解析は未実施**です。Node/SQL/模擬ブラウザー検査をC#の成功扱いにはしていません。CIは停止したままでDockerも使用しません。

ローカル検証:

```powershell
.\scripts\test-large-review.ps1 -Restore
```

このスクリプトはWeb/MCPをビルドして対象のC#テストとNodeテストを実行します。通常利用にNodeは不要です。SQLite単体の検査は`python tests/Fixtures/review_progress_sql.py`、模擬ブラウザー検査は`python tests/Fixtures/large_review_browser.py`です。

設計参照: Microsoft.Data.Sqliteのトランザクション仕様 https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions 、.NET Standardの説明 https://learn.microsoft.com/en-us/dotnet/standard/net-standard 。
