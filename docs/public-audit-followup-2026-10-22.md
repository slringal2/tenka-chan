# 公開前監査・追加調査 — 2026-10-22

対象: `slringal2/tenka-chan` / `main fc380d012e02a341d063b5509b4f2d539876871a`。
前回報告: [public-audit-items-2-3-2026-10-22.md](public-audit-items-2-3-2026-10-22.md)。
**公開可否の承認ではない。公開設定、CI、ログ、履歴、実行権限は変更していない。**

## 今回の進展と前回からの訂正

前回は一般のGitHub fetchでログ/ZIP本文を取得できなかったが、今回は
専用のjob-log取得とartifact-downloadで実データを読めた。
「この環境ではActionsログを読めない」という扱いは、下記の取得済み対象には当てはまらない。
前回の1実行でのartifact数0を、リポジトリ全体が0であるという意味にはしない。

今回の確認は、**実際の4ジョブのログ本文と4個の成果物ZIP**に対するものであり、
模擬データのテスト結果ではない。一方、353件すべてのworkflow実行や全job/attemptを
監査したわけではない。

## 2. Actionsログ本文・実際の成果物

### ログ本文を読んだ対象（4ジョブ / 4実行）

| Workflow run | Job ID | 対象 | 確認できたこと |
| --- | --- | --- | --- |
| 38572257410 | 115880230328 | Build/test、Ubuntu、成功 | checkout tokenとGit Basic認証値は`***`。LLM操作はloopbackのfake fixtureで、実モデルAPIキーの露出は認めなかった |
| 38571645087 | 115878085915 | Host experiments、Windows、失敗 | 認証値は`***`。主な失敗出力はオフラインNuGetキャッシュ不足。CIの標準runnerパスを含む |
| 38572258783 | 115880236977 | Trace capture、Windows、成功 | 認証値は`***`。アップロード指定はTRXテスト結果であり、workflow名からraw nettraceの公開とは推定しない |
| 38404711872 | 115321183962 | 最初のCI実行、Ubuntu、失敗 | 認証値は`***`。失敗はMSBuildパッケージ設定。現在のmainがビルド成功する証拠としては使用しない |

ログ本文の読み取りでは、明らかな未マスクの実資格情報は認めなかった。
これは人手相当の本文確認であり、全ログの自動パターンスキャンや秘密情報不存在の証明ではない。
古いログにあるテスト成功数は、今回のmainを実行した結果ではない。

GitHub APIのworkflow run総数は353件、失敗結果の総数は186件だった。
最古の記録は2026-10-13T13:24:36Zのrun 38404711872。
実行履歴が多いことや失敗していること自体は、秘密情報流出を意味しない。
未検査のjob、再実行attempt、別workflow、PRコメント・添付は残っている。

### ZIPを実取得して確認した対象（4個）

| Artifact ID | 所属run | ZIP内の唯一のファイル | ZIP bytes | SHA-256 / GitHub digest一致 |
| --- | --- | --- | ---: | --- |
| 11896944044 | 38571645087 | host-workflow.trx | 2843 | `60988e49cf032777cf8d401c302c0d90b687eb47ad80dcd60f4e0b63d2c32136` / 一致 |
| 11897758039 | 38571645087 | host-workflow.trx | 2704 | `c1202db790e13a0a58b583f570d3f59a353da699548d88fb3cdb86af5c3ed9bd` / 一致 |
| 11897134918 | 38572258783 | trusted-trace.trx | 2807 | `d765ce7e79f83a3efa2e582a8b85738e13f9f54b0a58152ca350ed47958bc1ae` / 一致 |
| 11898013834 | 38572258783 | trusted-trace.trx | 2771 | `262911ea5ad8584514c7f0f3f2cb2dd6464fe99f64abbbad04ff5bd711248b92` / 一致 |

全4個とも取得時点で`expired=false`、API上の期限は2027-01-12。
圧縮ファイルをメモリ上で読み、全entryをXMLとして解析した。ファイルを実行していない。
これらのZIPには、DLL、raw nettrace、設定ファイル、パッケージキャッシュは入っていなかった。
TRXに記録された失敗理由やスタックには標準CI runnerパスが含まれる。

全TRX本文を対象に以下の限定パターンを検索し、各カテゴリ0件だった:
GitHubトークン形状、AWSアクセスキー形状、秘密鍵ヘッダー、APIキー形状、
長いBasic/Bearer認証値、引用符付きcredential代入、メールアドレス。
**パターンに該当しない秘密情報、暗号化/符号化された値、業務情報の不存在は保証しない。**
4個の検査結果は、他の成果物が同じ内容であることを意味しない。

### Git履歴側の限定確認

監査対象main SHAを固定し、GitHub commits APIの完全一致pathフィルターで
ルートの`mcp-config.json`、`.env`、`settings.json`を照会した。3件とも結果は空だった。
これはmainから到達するこれら3パスだけの照会であり、別ブランチ、別名/下位ディレクトリ、
改名前のパス、ファイル本文に埋め込まれた値を含む全履歴スキャンではない。

この実行環境に完全な認証済みcloneはなく、ローカルGitからGitHubへのDNS解決も失敗した。
したがって、`git rev-list --all`で列挙した実repo全blobのスキャンは今回未実行。
一方、GitHub接続の専用ログ/ZIP取得は上記範囲で成功している。

## 3. 指定バージョンのライセンス宣言

同じmain SHAの`src/TenkaChan.Mcp/TenkaChan.Mcp.csproj`と
`tests/TenkaChan.Tests/TenkaChan.Tests.csproj`を読み、重複を除いた13個の直接参照を確認した。
下表はそれぞれの**指定バージョンのNuGet Galleryライセンス宣言**を照合したもの。
すべてのサンプルプロジェクト、実restore結果、NuGet archive内の全第三者ファイルを監査した表ではない。

| Package | Version | Declared license |
| --- | --- | --- |
| [Microsoft.Build.Framework](https://www.nuget.org/packages/Microsoft.Build.Framework/17.11.48) | 17.11.48 | MIT |
| [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol/2.2.0) | 2.2.0 | Apache-2.0 |
| [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.12) | 10.0.12 | MIT |
| [Microsoft.CodeAnalysis.CSharp.Workspaces](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Workspaces/5.9.0) | 5.9.0 | MIT |
| [Microsoft.CodeAnalysis.Workspaces.MSBuild](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Workspaces.MSBuild/5.9.0) | 5.9.0 | MIT |
| [Microsoft.Build.Locator](https://www.nuget.org/packages/Microsoft.Build.Locator/1.11.2) | 1.11.2 | MIT |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.0) | 10.0.0 | MIT |
| [SQLitePCLRaw.lib.e_sqlite3](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.13) | 2.1.13 | Apache-2.0 |
| [Iced](https://www.nuget.org/packages/Iced/1.21.0) | 1.21.0 | MIT |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/3.2.8) | 3.2.8 | MIT |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1) | 17.14.1 | MIT |
| [xunit](https://www.nuget.org/packages/xunit/2.9.3) | 2.9.3 | Apache-2.0 |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5) | 3.1.5 | Apache-2.0 |

この範囲はMIT 9件・Apache-2.0 4件。
`Microsoft.Build.Framework`は`ExcludeAssets=runtime`、`PrivateAssets=all`付きであり、
参照があることと、配布先へそのruntime DLLを同梱することは同じではない。

### 重要: TraceEventの同梱ネイティブDLL

[TraceEvent v3.2.8のnuspec](https://github.com/microsoft/perfview/blob/v3.2.8/src/TraceEvent/Microsoft.Diagnostics.Tracing.TraceEvent.nuspec)
（取得blob `185283f94c2d3ed1aeb685626fab71b81efd5cd3`）はMITを宣言する一方、
`msdia140.dll`、`KernelTraceControl.dll`、x86の`KernelTraceControl.Win61.dll`等を
package内の`build/native`へ含める指定を持つ。Dia2Lib/TraceReloggerLibも含む。
[src/Directory.Packages.props](https://github.com/microsoft/perfview/blob/v3.2.8/src/Directory.Packages.props)
ではupstreamビルド用SupportFilesを1.0.30に指定していた。
これは**Tenkaの復元済み推移依存一覧そのものではない**。

パッケージIDのMIT表示と、内包する全ファイルが一律MITで再許諾できるという判断は分ける。
Microsoftの[Visual Studio 2022 redistribution](https://learn.microsoft.com/ja-jp/visualstudio/releases/2022/redistribution)
は`msdia140.dll`をライセンス条件付きの再頒布対象に列挙している。
その記載だけで、NuGet経由の今回のDLLにどの条件が適用されるかまで確定しない。
**再頒布禁止と断定する発見ではない**。実際に配るDLLの出所・版・同梱条件を確認するための具体的な追加項目である。

そのため、実際の復元キャッシュからLICENSE/NOTICEを読む既存のcollectorに加えて、
バイナリ配布予定ならpublishディレクトリのnative DLLも別途棚卸しする。
元のソースだけを公開する場合と、ビルド済みDLL/EXEを配布する場合は、審査範囲を分ける。

## 残るゲートと判定

今回の対象では明らかな実資格情報の露出は発見していないが、**公開可能とは判定しない**。
残るのは、全ref/コミット/削除済みblob、未読のActionsログ・成果物・PR添付、実restoreされた
推移依存とnativeファイルの配布条件、雇用/共同著作/名称・素材の権利確認である。
著作者の権利をGitHub/NuGetメタデータだけで確定することはできない。

この調査中に新規CI実行、アプリ実行、ビルド、秘密の値の掲載、成果物削除、履歴改変、
リポジトリ公開設定の変更は行っていない。既存のログ/成果物を読んだだけであり、
過去のテスト成否を最新mainのビルド検証として流用していない。
