# Dynamic trace diagnosis / 動的ホットスポット診断

## 対応内容

`tenka_dynamic_diagnose` は、**すでに取得済み**のEventPipeトレースを
現在ロードしているRoslynスナップショットと読み取り専用で照合します。

- Speedscope内の選択した**1つのプロファイル**の観測スタックフレームを、
  名前が一意に一致するRoslynメソッドID、ファイル、行番号へ対応付ける
- メソッドごとに観測exclusive重みを集計。複数のフレーム名が同じメソッドを
  指す場合も重複を明示し、inclusive重みやサンプル数は足し込まない
- 静的解析の疑い（ループ内割り当て等）を**別の列**に表示。
  静的スコアと実測重みを混ぜてランキングしない
- 同じNettraceのGC停止窓、割り当てtick、Monitor競合待ち時間を
  PID/CLR単位の別枠に表示
- 不明フレーム、同名メソッドのオーバーロード、レポート切り詰め、
  EventPipeイベント欠損、提供情報不足を明示
- トレースと元の保存マニフェストのSHA-256を前後で確認し、
  **source/binary identityは検証済みとしない**

### 最短の操作

```text
1. tenka_context
   → 現在のsnapshotId、実行可能なassemblyIdを取得

2. tenka_trace {
     "snapshotId":"現在のID",
     "assemblyId":"実行するDLL",
     "approved":true
   }
   → 実際にアプリを動かしてnettrace / Speedscope /
     GC・allocation・Monitorイベントを収集
   → 応答のtrace artifactIdとprofiles[]を確認

3. tenka_dynamic_diagnose {
     "snapshotId":"現在のID",
     "artifactId":"traceの取得結果ID",
     "profileIndex":0,
     "limit":30
   }
   → 動的メソッド観測と、別枠のGC/割り当て/競合の診断
   → このステップはコードを実行しない
```

`profileIndex=0` は例です。実際にはトレースが返したプロファイル番号を明示的に選んでください。
`artifactId` は現在のスナップショット内で返されたものしか使用できません。
読み込み・再ビルド・セッション再起動などでIDが変わる場合、`tenka_context` からやり直してください。

同じ機能はWebの `/api/explorer/action` の
`action="dynamic-diagnose"`、`profileIndex`、`artifactId`、`limit` でも利用できます。
通常の `tenka_import_thread` と異なり、
`tenka_dynamic_diagnose` は共有している `CurrentHotspotProfile` を上書きしません。

### 応答を読むポイント

- `methods[].exclusiveWeight` / `exclusivePercent`:
  そのソースメソッド名に対応する**観測スタックの葉の重み**。
  スレッドの待機時間を含み得るため**正確なCPU時間ではない**
- `methods[].staticCandidates`:
  ソース解析で疑われる処理。動的に問題と確認されたわけではない
- `runtime[].gcSuspensionWindowTotalMs`:
  観測されたGCによる停止窓の合計。GCスレッドのCPU使用時間とは別
- `runtime[].tickReportedAllocationBytes`:
  割り当てのサンプリングtickで報告された量。型別の全割り当て量ではない
- `runtime[].overlappingMonitorWaitMs`:
  観測されたMonitor待ち時間の合計。複数スレッドの待ちが重複し得る
- `runtime[].sourceAttribution="process_clr_only__not_per_method"`:
  GC/alloc/Monitorの値を**どのメソッドにも勝手に割り当てていない**
- `binariesVerified=false`:
  現在のRoslynソースと実行時DLLが一致する根拠はまだない
- `frameListTruncated` / `methodListTruncated` / `runtimeListTruncated`:
  一覧は上限付き。表示されない処理のコストをゼロと解釈しない
- `runtimeEventStatus="partial_*"`:
  ログ欠損・読み取り上限があるため完全なイベント数を断定しない

## 現状の制限

- EventPipe SampleProfilerは**スレッド時間**をサンプリングする。オンCPU時間とは限らない
- Speedscopeスタックのソース対応は**名前ベース**。非同期ステートマシン・
  ジェネリック・同名オーバーロード・動的コードで対応できない場合がある
- 純粋なCPUサンプリング、割り当てスタックに基づく**メソッド単位のバイト数**、
  ヒープリーク解析、デッドロック証明、例外スタック診断は未実装
- runtime-costs は元トレース最大128MB・イベント数最大500,000・
  解析時間約45秒・返すprocess行12の上限がある
- トレース採取はユーザー承認が必要で、対象コードはOSユーザー権限で動く。
  読み取り専用の診断自体はアプリの再実行・ビルドをしない

## 検証方法

Windowsで.NET 10 SDKと事前復元済み依存関係がある場合:

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Debug --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DynamicTraceDiagnosisTests|FullyQualifiedName~SpeedscopeHotspotParserTests|FullyQualifiedName~RoslynIntegrationTests|FullyQualifiedName~RuntimeCostTests|FullyQualifiedName~RuntimeCostIntegrationTests"
```

EventPipe統合実測は独立した**オプトイン**検証（`TENKA_RUNTIME_INTEGRATION=1`）で、
信頼できる.NET 8 fixture、`dotnet-trace`、明示した実行設定が必要です。
この開発環境には.NET SDKがないため**ビルド・xUnit・Windows実機収集は未実施**。
生成されたテストコードを実行済みと表現しません。

CIは引き続き停止。Docker不要。
