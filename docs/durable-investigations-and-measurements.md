# セッションを跨ぐ調査と、レビュー記録への測定添付

基点: main `85d3381d96a85f46dc11a5b1ba2cd1e3e25e14e7`。
PR #20の永続調査・ファイル保存の考え方を、現在のgateway/承認/レビュー記録へ限定移植した。
古いPR全体、提案ソースの自動適用、baseline/candidate作業コピー作成は取り込んでいない。
kind=all、coverage、review_context、contextHash v2、既存の実行ゲートは維持する。

## 1. 接続と調査を分ける

最初のツール呼び出しで独立した investigationId を生成する。
`tenka_context.session.investigation`、`tenka_session_status.investigation`、
`tenka_investigation {"action":"status"}` でIDと保存状況を確認できる。

接続終了・期限切れ・サーバー再起動後、再接続してユーザーが保存データへのアクセスを承認したら:

```text
tenka_investigation {"action":"list"}
tenka_investigation {"action":"resume","investigationId":"<保存したID>","approved":true}
tenka_context {}
```

実験所有権（IDと元のcsprojパス）、完了呼び出しの証跡、小さい結果、測定時の対象情報を引き継ぐ。
`tenka_read_result` はまず同一セッションの生きた結果を読み、なければ現在の調査に保存した結果を読む。
`tenka_mark_review` と `tenka_note` の evidenceCallIds は、明示的に再開した調査の完了呼び出しも使える。
失敗・キャンセルの証跡はその状態のままで、成功証拠へ変えない。

**実行承認、snapshotId、projectId等の一時的な選択は引き継がない。** resumeは同じセッションにあった承認も取り消す。
最新contextから選び直し、必要なプロジェクト/操作だけを `tenka_approve_session` で承認する。
`tenka_measure_target` は `run_experiment` のセッション承認範囲で実行する。
clientNameの一致を本人確認には使わない。既存のローカル接続資格情報の範囲で扱う。

保存先は TENKA_DATA_DIR/`investigations-v1`。導入前のメモリ内セッションや、未マージPR #20の
別形式の保存データは自動再構成しない。別セッションの実験を名前だけで採用しない。
実験の実行時は保存したcsprojと実際のmanifestの所属を照合し、従来のworkspace allowlistも検査する。

## 2. 測定値をレビュー記録へ添付

既存の `tenka_prepare_experiment` で、対象プロジェクトを計測するbenchmarkを準備する。
テストの期待値・計測処理・入力・意味的同値性は外部エージェントが仕様から確認する。
実行前に対象を正しい構成でビルドし、workspaceを再ロードする。**この測定ツールは対象を自動ビルドしない。**

```text
tenka_measure_target {
  "snapshotId":"<現在のID>","targetId":"<調査対象>",
  "experimentId":"<変更前のprepared benchmark>","approved":true
}
```

対象workspaceのgateを保持し、従来のDockerなしhost runnerを呼ぶ。
前後で既知のソース入力を確認し、targetId / sourceHash / contextHash と、
**保存されたexecution-report.jsonの実バイトのSHA-256**を観測として保存する。
ハッシュは型の再シリアライズに依存しないため、無関係なモデル拡張だけで過去の報告が別物にならない。
後検査中のキャンセルや保存失敗では元の実行結果を消さず、未保存/確認不可を明示する。

変更後をビルド・再ロードし、同じbenchmarkコード・テンプレート・runner・フレームワークで別実験を準備し、
`tenka_measure_target` を再度呼ぶ。実験は単発であり、実行済みIDを再送して測り直さない。
接続を跨いでも、同じ調査を再開すれば両実験を使える。

通常のレビュー記録に2つのIDを任意で追加する:

```text
tenka_mark_review {
  "snapshotId":"<現在>","targetId":"<同一対象>",
  "sourceHash":"<markBasis>","contextHash":"<markBasis>",
  "expectedRevision":0,"status":"optimized","summary":"<実施内容と限界>",
  "baselineExperimentId":"<変更前のID>","candidateExperimentId":"<変更後のID>"
}
```

expectedRevisionには上の例の0を固定せず、現在のmarkBasisの値を使う。
両IDを省略する従来の呼び出しは変更しない。片方だけの指定や数値の直接送信は受け付けない。
記録前に、所属調査/対象/プロジェクト、正常完了、実行時receiptと保存報告の一致、
同じworkload/runner/テンプレート、TFM、変更前後の順序、candidateと現在ソース/contextの一致を検査する。
記録直前にもソースの古さを再確認し、revision競合は従来どおり拒否する。

`state.performance.status` は次のように作業状態と分ける:

| 値 | 意味 |
|---|---|
| unmeasured | このチェックポイントに測定添付がない。optimizedでも未測定 |
| measured_comparison | 捕捉された環境が一致し、ケース比較可能。統計的有意差の証明ではない |
| insufficient_samples | 比較値はあるが、5サンプル未満のケースがある |
| incomparable | 環境/ケース等に比較できない部分がある。比較可能な部分は詳細に残す |
| stale_measurement | 測定を添付した時のソース/contextと現在が異なる、または再ロードが必要 |

詳細は `checkpoint.measurement` に保存する。平均ns/op、変化率、bytes/op、サンプル数、
実験ID、報告SHA、対象観測を含む。queueは状態/件数/IDだけを保持し、全ケースを複製しない。
旧JSON/SQLite記録に添付がなければ未測定として読み込み、履歴をリセットしない。

## 3. 検証の限界を隠さない

これは、エージェントの「速くなった」という文章ではなく、runnerが保存した数値を確認して添付する機能。
ただし **independentlyVerifiedは常にfalse**。

- 既存DLLと観測したソースの同一ビルド/PDB対応は、この経路では証明していない。
- benchmarkが対象メソッドを実際に通ること、入力の妥当性、動作保持を自動証明しない。
- source前後検査は、非協調エディターによる変更・復元まで防ぐ不変コピーではない。
- 同一環境fingerprint・集計平均だけから、統計的有意差や本番性能を断定しない。
- ローカルファイルとSHAは、同じOSユーザーで動く任意コードに対する署名/隔離ではない。

ソースの独立コピーでbefore/afterをビルドするPR #20の提案検証全体、独立テストオラクル、
完全なバイナリ/ソース対応は残る。測定添付を「独立検証完了」と表示しない。

## 4. 上限と失敗時

接続の16セッション/200呼び出し/15分アイドルという上限は残るが、終了・再接続後に調査を再開できる。
1調査あたり4096証跡・256実験。小さい応答だけを保存し、大きなlive-onlyデータは再起動後に失われうる。
結果だけを得るために実行を繰り返さず、保存済みexperiment reportを利用する。
一覧は最大200調査で、打ち切り/読み取り不可を明示する。把握済みIDでのresumeは一覧上限によらない。
同一調査の実行と保存はファイルlockで直列化。別プロセス/セッションの同時操作を黙って割り込ませない。

保存失敗は `PersistenceWarning`（MCP応答ではcamelCase）または測定結果のpersistenceWarningとして返し、
完了した操作を失敗扱いにして再実行しない。既存receiptを自動削除しない。
同じrequestIdの再送は既存journalの結果を返し、新しい実行を起こさない。

## 5. 検証記録

追加C#ケース43件（新規2クラス39件＋既存gatewayテスト追加4件）。
保存再開、他調査拒否、取消、重複call、実測添付の検査/未測定/古い測定、SQLite互換などを対象とする。
**この環境には.NET SDKがなく、C#ビルド/xUnit/Windows実MCP/実BenchmarkDotNetは未実行。**
SDK取得も失敗した。C#テストの合格数や性能改善の実測値を報告するものではない。
Pythonのソース契約検査と差分/ハッシュ照合は、コンパイル/実行検証の代わりではない。

```powershell
python scripts/test-durable-review-contract.py
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DurableInvestigationIntegrationTests|FullyQualifiedName~ReviewBenchmarkAttachmentTests|FullyQualifiedName~AgentWorkflowGatewayTests"
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release --no-restore
```

CIの新設・再開、依存パッケージ変更、Docker必須化は行っていない。
