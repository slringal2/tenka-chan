# 実機フィードバックと証拠の区別

## 入手した報告

本会話でユーザーが共有した外部エージェントの報告（87/100点）を記録する。
報告された結果は、同一構成でビルド＋再ロードしても contextHash v2 が不変で、
変更時は changedComponents（sourceFiles 等）が理由を示し、以前の全件 needs_review 化が実効で解消したというもの。

これは **ユーザー環境からの報告** であり、この開発環境で実行した結果ではない。
当該実機の生ログ・保存DB・対象コミットが併記された測定ファイルは未受領のため、
**実測ログへのリンクはまだない。存在しないログやURLを補わない。**

## リポジトリで確認できるもの

- `fe6378eb06b2b9427e10e0f9a657a925792aa613` は必須引数エラー修正を取り込んだ基点。
- `807e3d9a5921d0d65140ff5f87b345a90c2358bb` のコミットメッセージには631/631合格と記載されている。
  これはコミット作者による実行報告で、この文書に生のテスト出力は添付されていない。
- 既存の [ReviewContextChurnTests](../tests/TenkaChan.Tests/ReviewContextChurnTests.cs) の
  `ReferencePathOnlyChurnIsReproducedByV1AndRemovedByV2` は、異なるdisplay pathを付けた同一PEメタデータを
  C#/Roslynの実際のReviewCatalogAsyncに通すテスト。PR #23から存在する。
- 今回の `PhysicalDllRelocationKeepsV2CheckpointAndChangesLegacyHash` は、同じDLLを異なるディレクトリに
  実ファイルとして置き、CreateFromFileで読み込むケース。ファイル内容一致、v1不一致、v2一致、checked維持を検査する。

## 未確定の点

参照パス由来の変動を再現するテストの存在、v2導入後の安定という実機報告、
過去に実際に不一致になった旧チェックポイントの根因は別々の話である。
旧DBに成分が保存されていなければ、後の実機安定だけで過去の原因を断定しない。

今回の追加テストとコードはこの開発環境では未実行。dotnetコマンドが存在せず、
公式インストーラーの取得もDNS解決失敗／ダウンロード失敗となった。
CIは停止中のまま。過去の631/631を今回の変更の合格数として使わない。
