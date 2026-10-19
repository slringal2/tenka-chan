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
- 追加で`ExceptionThrown_V1`（イベントID80）を読み、**first-chance例外**の
  型別発生回数・再スローフラグ・時系列の最初/最後をPID/CLR別に集計。
  最大256種類まで保持し、表示は上位だけとする。
  `ExceptionMessage`などの文字列や例外発生アドレスは読み取り結果に保存しない。
  **catch済みの例外も数えるため、不具合や未処理例外とは断定しない**
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
     GC・allocation・Monitor・first-chance例外イベントを収集
   → 応答のtrace artifactIdとprofiles[]を確認

3. tenka_dynamic_diagnose {
     "snapshotId":"現在のID",
     "artifactId":"traceの取得結果ID",
     "profileIndex":0,
     "limit":30
   }
   → 動的メソッド観測と、別枠のGC/割り当て/競合/例外発生回数の診断
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
  GC/alloc/Monitor/例外発生回数を**どのメソッドにも勝手に割り当てていない**
- `runtime[].exceptionThrows`、`runtime[].exceptionTypes`:
  そのPID/CLRに観測されたfirst-chanceの総スロー数と型別頻度。
  `exceptionKindsRetained`は最大256種類に**保持された**分類数で、
  全種類の数とは限らない。
- `exceptionHotspots[]` / `totalExceptionThrows`:
  GC時間順の表示から漏れたプロセスも含め、例外型別の発生回数上位20件と
  トレース全体のfirst-chanceスロー数を独立表示。CPU時間ではない。
  `exceptionHotspotsTruncated`なら一覧は部分的で、総数を示すものではない。
- `exceptionHotspots[].rethrowFlagsObserved`:
  ExceptionFlagsの再スローbit(0x04)が付いた観測数。
  **catch済みか未処理か、同一例外インスタンスかは判断できない**
- `binariesVerified=false`:
  現在のRoslynソースと実行時DLLが一致する根拠はまだない
- `frameListTruncated` / `methodListTruncated` / `runtimeListTruncated`:
  一覧は上限付き。表示されない処理のコストをゼロと解釈しない
- `runtimeEventStatus="partial_*"`:
  ログ欠損・読み取り上限があるため完全なイベント数を断定しない

## サンプリングされた割り当てのスタック（追加）

`tenka_trace` が返した `artifactId` を使用し、同じ`snapshotId`で
`tenka_allocation_stacks` を呼び出すと、CLR `GCAllocationTick` のイベントに紐づく
マネージドスタックを**読み取り専用で**解析できます。

```json
{
  "snapshotId": "tenka_contextで確認した現在のID",
  "artifactId": "tenka_traceで得たartifactId",
  "limit": 30
}
```

`/api/explorer/action` では `action="allocation-stacks"`。
対象アプリやビルドは実行せず、外部のシンボルサーバーにも接続しません。
既存のTraceEventパッケージを使って、元`.nettrace`（最大16 MB）を
**同じ保存フォルダーに一時的にETLX変換し、終了時に削除**します。
変換途中でアプリが強制終了した場合には一時ファイルが残る可能性があるため、
ローカルの機密ファイルとして管理してください。変換は同期的な前処理を伴い、
最大の処理時間は保証していません。

応答の重要な区別:

- `allocationTicksObserved`: 解析した`GCAllocationTick`数。
  **オブジェクトの全割り当て回数ではない**
- `sourceLinkedTicks`: スタック上の**最も近い一意な**Roslynメソッド名に関連付けたtick数。
  正確な割り当て命令・実行バイナリの一致は未検証
- `hotspots[].sampledTriggers`: その候補に関連付いたサンプリングtick数
- `hotspots[].triggerObjectBytesObserved`: tickを発火させた**個々のオブジェクトのサイズだけ**を合計した数。
  対象メソッドの総割り当てバイト数や同種オブジェクトの全量ではない
- `ambiguousTicks` / `unmatchedTicks` / `missingStackTicks`:
  名前衝突・認識できないソース・スタック欠落を勝手に補完しない
- `reportedEventsLost` / `eventLimitReached` / `hotspotsTruncated`:
  サンプリングや表示の範囲を過小評価しないための情報

`binariesVerified=false`、`totalAllocatedBytesVerified=false`は常に維持します。
別のプロジェクトを読み込むとartifactIdは無効です。
開いたワークスペース外の任意の`.nettrace`パスをMCPから指定する経路は追加していません。

### 技術上の注意

`EventPipeEventSource` にはそのままでは解決済みの`CallStack()`を得られない制限があり、
`TraceLog.CreateFromEventPipeDataFile`のETLX変換を使用します。
TraceLogがマネージドメソッド名を解決できない場合は未解決として返すため、
メソッド名の有無・サンプル数は、ランタイムのトレース/rundown内容で変わります。
ETW/kernelsのオンCPUサンプリングは今回も未実装です。

出典: [TraceEvent APIとETLX](https://github.com/microsoft/dotnet-samples/blob/master/Microsoft.Diagnostics.Tracing/TraceEvent/docs/TraceEvent.md)、
[MicrosoftのEventPipe概要](https://learn.microsoft.com/dotnet/core/diagnostics/eventpipe)。

## 現状の制限

- EventPipe SampleProfilerは**スレッド時間**をサンプリングする。オンCPU時間とは限らない
- Speedscopeスタックのソース対応は**名前ベース**。非同期ステートマシン・
  ジェネリック・同名オーバーロード・動的コードで対応できない場合がある
- **Windowsの通常EventPipeプロファイルはスレッド時間サンプリング**。
  ネイティブを含む真のon-CPUサンプルを得るには、ETWなどOS側の別の観測基盤が必要。
  純粋なCPUサンプリング、割り当てスタックに基づく**メソッド単位のバイト数**、
  ヒープリーク解析、デッドロック証明、例外スタック/throw-siteの自動照合は未実装
- CLRの例外イベントは発生時点（first-chance）を示すだけで、
  アプリケーション失敗や未処理例外を意味しない。
  **生の`.nettrace`には例外メッセージ等の機密情報が含まれる可能性がある**。
  集計結果でメッセージを省略しても元トレースを安全な公開物に変換したことにはならない。
- runtime-costs は元トレース最大128MB・イベント数最大500,000・
  解析時間約45秒・返すprocess行12の上限がある。
  例外型の分類はPID/CLRあたり最大256種類、メソッド診断は
  別途最大20個の例外タイプを表示し、超過・欠損を明示する
- トレース採取はユーザー承認が必要で、対象コードはOSユーザー権限で動く。
  読み取り専用の診断自体はアプリの再実行・ビルドをしない

## 検証方法

Windowsで.NET 10 SDKと事前復元済み依存関係がある場合:

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Debug --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AllocationStackObservationsTests|FullyQualifiedName~AllocationStackTraceReaderTests|FullyQualifiedName~DynamicTraceDiagnosisTests|FullyQualifiedName~SpeedscopeHotspotParserTests|FullyQualifiedName~RoslynIntegrationTests|FullyQualifiedName~RuntimeCostTests|FullyQualifiedName~RuntimeCostIntegrationTests"
```

EventPipe統合実測は独立した**オプトイン**検証（`TENKA_RUNTIME_INTEGRATION=1`）で、
信頼できる.NET 8 fixture、`dotnet-trace`、明示した実行設定が必要です。
この開発環境には.NET SDKがないため**ビルド・xUnit・Windows実機収集は未実施**。
生成されたテストコードを実行済みと表現しません。

CIは引き続き停止。Docker不要。
