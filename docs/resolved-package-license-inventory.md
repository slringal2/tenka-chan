# 復元済みNuGet依存のライセンス台帳（オフライン）

MITは甜花ちゃん自身のソースに適用するライセンスです。依存パッケージまで
MITへ変更するわけではありません。`THIRD-PARTY-NOTICES.md`の手書き一覧を
補うため、**実際に復元された依存グラフ**からライセンスの宣言と関連ファイルの
証跡を収集します。ライセンス適合性の判定や公開の許可は行いません。

## 何を読むか

`scripts/collect-package-licenses.py`は、明示した各`project.assets.json`の
`libraries`に記録された全NuGetパッケージ（直接・推移依存、build/test用を含む）を
列挙し、明示したローカルNuGetキャッシュだけから`.nuspec`を読みます。

宣言が`license type="expression"`なら式をそのまま記録し、
`type="file"`ならそのファイルが存在するか確認します。資産一覧に記録された
LICENSE/COPYING/NOTICE/ThirdPartyNotices等についても、相対パス・バイト数・
SHA-256を台帳に残します。ファイル本文を出力・複製せず、DLLをロードしません。

ライセンスURLしかない、ファイルがない、キャッシュが複数あって曖昧、入力が
上限を超えるなどの場合は`inventoryComplete=false`と理由を返します。
不足したライセンスをプロジェクト側のMITから推測しません。

## Windowsでの実行

Python 3.10以降は**この任意の開発・公開点検ツールだけ**に必要です。
通常の`setup.bat`/`start.bat`やMCP利用には新しいPython依存を追加しません。
事前に信頼するチェックアウトで、意図した構成のNuGet復元・ビルドを済ませてください。
このコマンド自身はrestore/buildやダウンロードを一切行いません。

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
$report = Join-Path 'artifacts' ('nuget-license-inventory-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
py -3 scripts/collect-package-licenses.py `
  --assets src/TenkaChan.Web/obj/project.assets.json `
  --assets src/TenkaChan.Mcp/obj/project.assets.json `
  --assets src/Chan.Core/obj/project.assets.json `
  --package-root "$packageRoot" `
  --output "$report"
```

テスト・ベンチマークも含めて配布する場合は、それらの`obj/project.assets.json`を
追加の`--assets`で明示します。独自のMSBuild intermediate pathを使用している場合は
実際の出力パスを指定してください。追加キャッシュも`--package-root`で明示できますが、
同じパッケージが複数キャッシュに存在する場合は選択を推測せず不完全として返します。

生成先は**新規ファイルのみ**です。同名のレポートを上書きしません。
ローカル絶対パス、licenseUrl、ライセンス本文は台帳へ転記しませんが、
パッケージ名やバージョンも私有情報の場合があるため、共有前に内容を確認してください。

## 結果の読み方

| 項目 | 意味 |
| --- | --- |
| `inventoryComplete` | 指定された入力に記録された対象を、今回の制限内で収集できたか |
| `packages[].licenseExpression` | `.nuspec`に宣言された式。SPDXの構文/意味の検証や法的判断ではない |
| `packages[].evidenceFiles` | ライセンス/通知候補の相対パス、ハッシュ、サイズ。配布へ自動同梱はしない |
| `assets[].sha256` | 読んだ依存グラフの証跡。現在のソース/ビルドと一致する証明ではない |
| `packages[].issues` / `assets[].issues` | 欠落、不整合、未対応形式、上限、参照範囲の問題 |
| `publicReady` / `licenseCompatibilityVerified` | **常にfalse**。収集に成功しても公開・法的適合を断定しない |
| `cacheAuthenticityVerified` | **常にfalse**。復元キャッシュの署名や配布元との一致は未検証 |

終了コードは`0`が収集完了（要人手レビュー）、`1`が不完全な台帳、`2`が
呼び出し・入力ルート・出力先の問題です。`0`は「公開してよい」の意味ではありません。
JSONを使うことで、名称・件数だけを見て不明パッケージを落とすことを避けられます。

## 安全境界と制限

assets内の`packageFolders`は読取許可として信用せず、呼び出し側が明示した
キャッシュルートのみを使います。パスの遡り、絶対パス、代替データストリーム、
symlink/junction/reparse point、XML DTD/entityを拒否します。
これはローカルの信頼できるファイルを点検する仕組みであり、悪意ある別プロセスが
検査中にファイルをすり替える環境へのOSサンドボックスではありません。

上限はassets各16 MB、nuspec各1 MB、NOTICE等各4 MB、合計読取64 MB、
4,000パッケージ、パッケージごとに記録されたファイル10,000件、通知候補64件です。
上限に達した場合は不完全と表示し、「全件問題なし」にはしません。

.NET SDK同梱物、ランタイム、コンテナーイメージ、埋め込みライブラリ、商標・画像・
会社/共同著作者の権利、実際に配布するバイナリとの照合は対象外です。
`license type="expression"`があるだけでは、著作権表示やNOTICE等をすべて回収した
証拠になりません。**本台帳はSBOMの完全な代用品でも法務レビューでもありません。**
古いassetsを読めば古い情報が出るため、公開対象の復元結果を指定してください。

## 検証

```text
python tests/test_package_license_inventory.py
```

2026-10-22の編集環境（Debian 13 / Python 3.13.5）で27件のテストを実行し、
すべて合格しました。CLIを含む模擬assets/nuspec/キャッシュに対するテストです。
甜花ちゃんの実際の復元グラフやWindows環境で実行した結果ではありません。
`.NET SDK`とPowerShellがこの環境にないため、その検証済みという主張はしません。
CI再開・Docker導入・Git履歴書換え・GitHub公開設定の変更はしていません。

## 仕様の根拠

- [Microsoft: .nuspec license metadata](https://learn.microsoft.com/en-us/nuget/reference/nuspec#license)
- [Microsoft: project.assets.json and restore](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1004)
- [公開前チェックリスト](PUBLIC-RELEASE-CHECKLIST.md)
- [第三者ライセンス](../THIRD-PARTY-NOTICES.md)
