# DLL 自動検出の追加開発とローカル検証

## 対応内容

`WorkspaceBinaryDiscovery` は従来の `bin/Release/<TFM>` と
`bin/Debug/<TFM>` に加えて、プロジェクトに明示された `OutputPath`、
`OutDir`、`BaseOutputPath` を探索する。`AssemblyName` の文字列指定、
`$(Configuration)`、`$(TargetFramework)`、`$(MSBuildProjectDirectory)` の
限定的な置換に対応する。Windows のバックスラッシュもパス区切りとして扱う。

これは MSBuild 評価ではない。`Directory.Build.props`、インポート、任意の
プロパティ式、条件式は評価しない。これらに依存する出力先の完全な解決は未対応。
明示的な出力先も候補であり、ソースとビルド成果物が一致する証明ではない。
共有 `OutDir` など配置から構成を判断できないものは `Unknown` と表示する。

候補 DLL はロード・実行せず PE メタデータの `TargetFrameworkAttribute` を
読み、.NET 8 以降の基本フレームワークを確認する。フォルダー名が net8.0 でも
中身が .NET 9 の DLL なら採用しない。プラットフォーム修飾と構成はパスの
ヒントであり、完全なビルド識別や CPU アーキテクチャの検証ではない。

実行可能候補にはエントリーポイントと、基本 TFM が一致する JSON 形式の
runtimeconfig が必要。他 OS/CPU の RID フォルダーはローカル実行候補から外す。
この段階で実際のランタイム導入状況・依存 DLL の充足を保証するわけではない。

削除済みのプロジェクト、壊れた DLL、許可範囲外のパスが混ざっても別の正常な
プロジェクトの検出は継続する。シンボリックリンクの許可や許可ルートの拡大はしない。
`ref`、`refint`、`publish` への再帰探索は行わず、項目数にも上限を設ける。

生成実験用 DLL の自動選択は Release、構成不明、Debug の順で、同構成では
RID なし、ホストに合う RID の順。異なる DLL が同順位で残れば任意に選択せず
エラーにする。古いビルド出力を整理して対象を再ビルドする必要がある。

## 検証

`WorkspaceBinaryDiscoveryTests` に 22 ケースを追加した。環境変数による
早期 return はなく、Roslyn で小さな PE を生成してメタデータだけを検査する。
生成した PE や対象アプリは実行しない。.NET 8 ランタイムや Docker は不要だが、
テストプロジェクト自体は net10.0 のため .NET 10 SDK が必要。

```powershell
.\scripts\test-local-discovery.ps1
# パッケージを復元済みの環境のみ:
.\scripts\test-local-discovery.ps1 -NoRestore
```

通常の dotnet test でも実行できる。

```powershell
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter FullyQualifiedName~TenkaChan.Tests.WorkspaceBinaryDiscoveryTests
```

結果は `artifacts/local-discovery-tests/workspace-discovery.trx` に出力する。

**この変更の作成環境には .NET SDK がなく、追加した C# テストおよびビルドは
未実行。テスト成功や Windows 実機での動作確認を主張しない。**

## CI

2026-10-14 の依頼に従い `main` と未統合 PR #18 / #10 のワークフロー定義を
`.github/disabled-workflows/` に退避した。新しい CI は追加・実行しない。
これはブランチ内の定義退避であり、リポジトリ全体の Actions 設定変更ではない。
古い別ブランチにはワークフローが残る可能性がある。
