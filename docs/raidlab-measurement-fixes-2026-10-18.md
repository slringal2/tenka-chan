# RaidLab フィードバックへの実装対応（2026-10-18）

元の実機報告: [validation-feedback-2026-10-18-raidlab.md](validation-feedback-2026-10-18-raidlab.md)

## 対応A: 多プロジェクト生成実験のコンパイル参照

`TrustedHostExperimentExecutor` は既存の安全方針どおり生成 `.csproj` の `ProjectReference` を削除する。
ただし以前の対象DLL **1本だけ**の `Reference` では、対象の公開APIに現れる `Domain.RaidDataset` などをコンパイルできなかった。

`TrustedExperimentReferenceClosure` で、既にビルド済みの対象DLLと**同じ出力フォルダ**にある
管理対象DLLのPEアセンブリ参照を読み、対象から到達する依存DLLを `Reference` として生成プロジェクトに追加する。
コンパイル**前**に参照を設定するため `CS0234` / `CS0246` の代表的な原因に対応する。

- 既存ビルド出力のみを読み取り、対象ソリューションを勝手にrestore/buildしない。
- DLLはメタデータとして読み、.NETアセンブリをロード・実行しない。
- 解決は同一出力フォルダの推移的依存まで。動的ロードや別フォルダのDLL、対象プロジェクトが参照しない独立アセンブリは自動補完しない。
- 大量/重複のDLLは黙ってランダム選択せず拒否。既存のhost実行承認、キャッシュのみのrestore、タイムアウトは維持。

`GeneratedHostMultiProjectBuildTests` はDomain/Application/Generatedという固定の無害なfixtureをSDKでオフラインビルドし、
生成コードが`RaidDataset`を使えることを検査する**追加回帰テスト**。C#ビルド/xUnitの実機合格を主張するものではない。

## 対応B: 調査所有権の引き継ぎ

`tenka_prepare_experiment` の戻り値に既存の `id` に加えて `investigationId` を付与。
同じアダプター接続内で `measure_target` するか、別接続では次を先に実行する。

```json
{"action":"resume","investigationId":"<prepareで返されたID>","approved":true}
```

その後 `tenka_context` を再取得し、実行は改めて承認する。
**明示的なresumeなしの所有権移譲はしない**。異なる調査での実験横取りも許可しない。
エラーの `nextAction` にはこの2通りを表示する。

## 対応C: 既存BenchmarkDotNetコンパニオンの添付

従来の `tenka_run_companion` はそのまま残す。対象メソッドの証跡に使う場合は
**実行前から選択と観測を行う**新ツール `tenka_measure_companion` を使う。

```json
{
  "snapshotId":"<現在のsnapshotId>",
  "targetId":"<対象のレビューID>",
  "companionId":"<現在のコンパニオンID>",
  "approved":true,
  "allowRestore":false,
  "benchmarkJob":"medium"
}
```

前提: ベンチマークのcsprojは対象プロジェクトへの**直接または推移的なリテラルProjectReference**を宣言する。
ワークスペースのgate/実行許可のもと既存companionをビルド・実行し、実行前後のターゲットソース状態と
ベンチマークのローカル入力を照合する。
正常終了時は、`kind=companion_benchmark` の読取専用experimentを作成し、
実際のBenchmarkDotNet JSONから得た結果・環境fingerprint・ターゲット観測と保存レポートのSHAを結びつける。

変更前と変更後に**別々の `tenka_measure_companion` 実行**を行い、同じinvestigationに戻って
`tenka_mark_review` に `baselineExperimentId` と `candidateExperimentId` を指定する。
両方で同じcompanion・ベンチマーク入力ハッシュ・job（short/medium）・TFMを要求する。
`run_companion` の**過去の未紐づけ結果は事後的に対象検証へ変換しない**。
生成実験とcompanionの結果を組み合わせた測定比較も拒否する。

`medium` は既定、`short` は高速予備測定用。実サンプル数が少ない場合は、従来の
`insufficient_samples` 扱いにして有意差を断定しない。companion側は従来どおり5分の実行上限がある。

## 検証の状態と限界

- この変更は **ソースの実装とテスト追加**。この編集環境に.NET SDKがなく、C#コンパイル、xUnit、
  RaidLabの実MCP呼び出し、実BenchmarkDotNetの成否は**まだ未検証**。過去の46/46や631/631を今回の合格実績へ流用しない。
- ソース/バイナリの一致、benchが対象メソッドを必ず通ること、数学的な統計有意性、動作保持は未証明。
  companionの入力ハッシュは通常のローカルcsソース/csproj/Directory.Buildに限定され、
  リンクされたソース、任意のMSBuild importや生成ファイル、ビルド出力の同一性は網羅しない。
- PowerShellの日本語表示文字化けは今回未対応。出力表現とログの実バイトを別途調査する。
- `.github/workflows` は変更しない。GitHub Actions CIは停止のまま。Docker不要。

## Windows実機での確認コマンド

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TrustedExperimentReferenceClosureTests|FullyQualifiedName~GeneratedHostMultiProjectBuildTests|FullyQualifiedName~CompanionBenchmarkWorkloadTests|FullyQualifiedName~CompanionBenchmarkPersistenceTests|FullyQualifiedName~ReviewBenchmarkAttachmentTests|FullyQualifiedName~AgentRecoveryAdviceTests"
```

その後RaidLabで、生成benchのDomain型参照成功、`measure_companion` を用いたbefore/after測定とmark、
別接続の `investigation resume` を本当に試し、そのログを新規の実機結果として記録する。
