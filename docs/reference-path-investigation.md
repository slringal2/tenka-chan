# 参照パス問題: 原因の切り分けと取得する証拠

基点: `85d3381d96a85f46dc11a5b1ba2cd1e3e25e14e7` (PR #24マージ後)。

## 確認できたことと、まだ確認できないこと

「reference-path」は少なくとも3つの異なる状態を指しうる。

| 条件 | 現行の設計上の結果 | 調べる証拠 |
| --- | --- | --- |
| 同一モジュール・同一参照設定、表示/参照場所だけ変更。ソースと明示的csprojは不変 | v1はDisplayを含むため変動、v2のmetadataReferencesは不変 | DLLのSHA-256、参照identityHash、v1/v2ハッシュ |
| 同じDLLでもcsprojのHintPathを書き換えた | projectFilesが変動する。metadataReferencesのパス依存とは別 | changedComponents=projectFiles、csproj差分 |
| DLL欠落・不正PE・アクセス不可・必要な型/アセンブリが参照されない | 読み取り失敗、またはRoslynが参照を落としてコンパイラ診断を返す | failureCode、workspace warnings、コンパイラエラー |

旧実装は `MetadataReference.Display` を連結してハッシュ化していた。これはコードで確認できる具体的な誤判定経路であり、原因が全く分からないわけではない。
一方、「過去の実機の既存マークが変わった直接の引き金」と「現行v2にも参照解決バグが残る」という主張は、同じではない。
この会話で生の失敗ログ・変更前後のスナップショットは受領していない。過去の事象が上記のどれだったかは断定しない。

MicrosoftのAPI定義でもDisplayはエラー表示用の文字列で、CreateFromImageのfilePathは実ファイルを要求しない。
GetMetadataは参照メタデータを取得するAPIで、遅延参照ではI/O失敗がありうる。診断でDisplayをファイルパスとして勝手に開かない。
- [MetadataReference実装](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/MetadataReference/MetadataReference.cs)
- [PortableExecutableReference API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.portableexecutablereference)
- [GetMetadata API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.portableexecutablereference.getmetadata)

## 今回の診断改善 (ハッシュ方式は変更しない)

`tenka_review_context` に **referenceDiagnostics** の件数と失敗例（最大5件）を追加。
通常の1回の文脈取得を成功参照の大量表示で肥大化させない。detailsToolは `tenka_review_target` を示す。
`tenka_review_target` では同名フィールドに下記の詳細情報を返す。
既存のsource、selection、markBasis、binary候補、contextHash v2、権限チェックは維持する。

```text
referenceDiagnostics.available
referenceDiagnostics.total
referenceDiagnostics.unresolvedIdentities
referenceDiagnostics.limited
referenceDiagnostics.items[].projectPath
referenceDiagnostics.items[].kind                  // metadata or project
referenceDiagnostics.items[].display / filePath
referenceDiagnostics.items[].identityHash
referenceDiagnostics.items[].identityComplete
referenceDiagnostics.items[].failureCode
```

対象のプロジェクトと、読み込み済みの推移的なソース依存プロジェクトを含む。
identityは既存contextHashを計算した **同じReadの結果** から収集し、後から読み直した値を混ぜない。
失敗項目を優先し、各プロジェクトで最大100項目、合成後も最大100項目。
省略分を含む総数・識別失敗数は保持する。文字列ラベルは2,048文字まで、切り詰めはlabelsTruncatedで明示。
identityHashは切り詰め前の識別値のハッシュ。件数/文字列が切られている場合、全参照の対応を確定したとは扱わない。

failureCode: file_not_found / directory_not_found / bad_image / access_denied / not_supported /
io_error / metadata_unavailable / unsupported_reference / no_modules / missing_mvid / project_not_loaded。
例外メッセージ本文は新しい診断にコピーしない。キャンセルを通常の読み取り失敗へ変換しない。

**identityCompleteはコンパイル成功でも実行時の依存解決成功でもない。**
MSBuildが不足参照を一覧から省くと、返ってきた参照の識別にはすべて成功していてもコンパイルエラーが残る。
`source.inspection`、静的解析のコンパイラ診断、workspace warningsも確認する。
この情報はロードしたカタログの観測で、現在のディスクを再検査した結果ではない。取得日時を変えて再取得するだけでは再ロードしない。

診断パス/失敗コードをcontextHashに追加しない。チェックポイント/DB形式/旧マークの互換判定にも変更を加えない。
queueへの診断複製、新しいビルド/restore/実行/自動承認は行わない。

## テストの区別

既存の `ReferencePathOnlyChurnIsReproducedByV1AndRemovedByV2` はC#/RoslynのAdhocWorkspaceを使う。
`PhysicalDllRelocationKeepsV2CheckpointAndChangesLegacyHash` は実ファイルをCreateFromFileで読み込むが、MSBuildによるHintPath評価の再現ではない。

新しい **ReferencePathMsBuildProbeTests.MsbuildRelocationSeparatesLabelsProjectEditsAndMissingReferences** は次を実行する。

1. 一時ディレクトリに固定の依存ライブラリとconsumerを作り、インストール済み.NET 10 SDKでビルドする。
   NuGet.Configのソースは空。fixtureにPackageReferenceはない。対象アプリは実行しない。
2. 同じDLLを2箇所へコピーしSHA-256の一致を確認。実RoslynReviewService/MSBuildWorkspaceでconsumerをロードする。
3. imported Directory.Build.props内の参照先だけを変え、新しいMSBuildWorkspaceで再ロード。
   実際に選ばれたfilePath、同じidentityHash、v1不一致、v2一致、コンパイラエラー0を検査する。
4. 同じ実効DLLでも明示的csprojのHintPath表現を変え、projectFilesだけが変化することを検査する。
5. 存在しないDLLへの参照を指定し、コンパイラエラーが出ることを負の対照として検査する。

各観測を `schema=tenka.reference-path-probe/v1` のJSONでxUnit出力へ記録し、TRXロガーで保持できる。
SDK不足・復元/ビルド失敗・観測の不一致はテスト失敗。未実行をスキップや合格として処理しない。
これは **制御されたfixtureでの機序確認** であり、過去のユーザー環境での事象を復元するものではない。

.NET 10 SDKと本体の復元済み依存関係がある環境で実行:

```powershell
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ReferencePathMsBuildProbeTests" --logger "trx;LogFileName=reference-path.trx" --results-directory TestResults/reference-path
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ReviewReferenceDiagnosticTests|FullyQualifiedName~ReviewContextChurnTests"
```

**作成環境での実行状態: C#ビルド・追加テストは未実行。** dotnet --infoはexit 127、公式インストーラー取得も名前解決/ダウンロード失敗。
検証済みTRX/生ログはまだない。過去の631/631や88点評価を今回の実行証拠に流用しない。
新しいハッシュアルゴリズムや一括checked復旧を入れて「直った」ことにはしていない。CIは停止のまま。

## 実環境で残す最小情報

同じtargetIdについて、変更前後のtenka_review_contextとtenka_review_targetのdataを保存する。
比較するのはcontextHash/ContextChangeだけでなく、requiresReload、referenceDiagnostics、コンパイラ診断。
変更内容がパスのみ、csproj編集、同構成ビルド、構成変更のどれだったかを併記する。
歴史的成分のない旧記録については依然として原因を逆算できない。観測不足は未確定と記載する。
