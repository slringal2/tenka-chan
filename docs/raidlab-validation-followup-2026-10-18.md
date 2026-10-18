# RaidLab 実機フィードバック追補・修正内容 (2026-10-18)

参照: [validation-feedback-2026-10-18-raidlab-fix.md](validation-feedback-2026-10-18-raidlab-fix.md)
基点: `main 71a875c`。今回の改修は「新機能を増やす」より、既に報告された障害の修正に限定する。

## 1. NuGetから追加されるテストSDK補助ソース

原因: `RoslynReviewService.OpenAsync` が `MSBuildWorkspace` の全 `Project.Documents` に
`WorkspaceGuard.RequireFile` を行う。復元後のテストプロジェクトには
`.../.nuget/packages/microsoft.net.test.sdk/17.14.1/build/net8.0/Microsoft.NET.Test.Sdk.Program.cs`
のような合成Compile項目があり、許可ルート外のためソリューション全体のロードが失敗していた。

変更:
- `NuGetSdkGeneratedDocumentPolicy` は **パッケージ名、バージョン階層、build/net*、
  固定ファイル名** に一致し、実在し、reparse/symlinkのない
  `Microsoft.NET.Test.Sdk.Program.cs` のみ除外可能とする。
- `TENKA_ALLOWED_ROOTS` は拡張しない。通常の外部リンクソース、別パッケージ、
  名前の異なるファイル、symlinkは従来どおり拒否する。
- 該当ドキュメントを解析用のRoslyn `Solution` から取り除き、
  クラス/メソッドのレビュー対象に混ぜない。除外件数をwarningsに表示。
- **トレードオフ:** 削除後のRoslyn-onlyコンパイル診断は、実際のテストビルドと異なる
  場合がある。対象プロジェクトの実ビルド結果は従来のビルド/テスト経路で確認する。
- 標準キャッシュは `NUGET_PACKAGES` が指定されていればその値、なければ
  ユーザーの `.nuget/packages` を使用。別の独自キャッシュ配置は現在未対応。

新規 `NuGetSdkGeneratedDocumentTests` は、別ディレクトリに模擬NuGetヘルパーを置いた
2プロジェクトの本物の `.slnx` をMSBuildWorkspaceで読み、
既知ヘルパーだけを落とし、通常の外部参照は拒否する回帰検査を定義。

## 2. MCPの引数バインディングエラー

`Program.cs` のCallToolフィルターで、実行**前**に公開MCP
`tools/list.inputSchema` の必須項目および既知のJSON型を検査。
`expectedRevision=null` はフィールド名を示すmissing/null、
`expectedRevision="2"`、小数値、int64範囲外はフィールド名と期待型を示す
type mismatchとして返す。配列要素とnullable unionも検証する。

生の引数値や内部JsonExceptionをクライアントへ流さない。未対応のschema型や
実処理で発生した例外を「ユーザーの引数ミス」と決めつけない。
`AgentToolErrorAdviceTests` に回帰ケースを追加。

## 3. BenchmarkJob表示

`ReviewBenchmarkEvidence` と `ReviewPerformanceState` に省略可能な
`benchmarkJob` を追加。生成実験または旧保存データではnull、
companionの測定添付時は両実験で一致確認した`short` / `medium`を表示。
`tenka_mark_review` の`checkpoint.measurement.benchmarkJob`と
`state.performance.benchmarkJob`から取得できる。過去JSONにはフィールドがなくても読み込み可能。
`short`（サンプル数3など）は引き続き`insufficient_samples`であり、
数値の倍率が大きくても統計的有意性や正しさの検証済みとは扱わない。

## 検証範囲と残る課題

- **この編集環境に.NET SDKがなく、ビルド、xUnit、実MCP/RaidLab実機検証は未実施。**
  `dotnet`がPATHに存在せず、公式配布先へのDNS解決もできなかった。
  今回のC#テストは追加しただけで、通過数には含めない。
- 実機報告の `742/742`・`46/46` は**前コミットでの報告**で、今回のテスト合格数とは違う。
- 元の修正A（生成host実験での多プロジェクト参照）は
  `TrustedExperimentReferenceClosure` と既存fixtureを実機で測る必要がある。
- 既に外部エージェントが行った `RaidAnalyzer.Analyze` 最適化コードは
  今回の `main` に含まれていない。ユーザーの検証ワークコピーには触れない。
- CIは停止のまま、Docker依存なし、host実行権限の緩和なし。

## Windows側で再実行する確認

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NuGetSdkGeneratedDocumentTests|FullyQualifiedName~AgentToolErrorAdviceTests|FullyQualifiedName~ReviewBenchmarkAttachmentTests|FullyQualifiedName~GeneratedHostMultiProjectBuildTests"
```

1. restore済みのRaidLab `.slnx` を `tenka_open_project` して6プロジェクトが見えるか。
2. `tenka_prepare_experiment`→`tenka_measure_target` でDomain型を含む実コードをコンパイルできるか。
3. `tenka_mark_review` に `expectedRevision:null` / 不正な型を渡し、フィールド名がMCP応答に出るか。
4. `short` companionの前後測定を添付し `state.performance.benchmarkJob=short` と
   `insufficient_samples` が同時に返るか。

プロセス実行は信頼されたWindowsローカル環境で明示承認された場合のみ行う。
