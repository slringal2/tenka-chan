# RaidLabの仕様（テストの期待値の根拠）

この仕様はbaseline/indexed双方に適用する。最適化の都合で変更してはいけない。

1. メタデータのプレイヤーID・スキルIDは正の一意な値。重複は入力エラーとして例外。ギルドIDは正。スキルのBonusPercentは0～100。
2. 入力は順序付きCombatEvent配列。EventId <= 0は拒否し、重複判定用のIDには登録しない。
3. 正のEventIdは最初の出現を採用する。最初のイベントが不正・キャンセルでもIDを消費する。同IDの後続イベントはすべてduplicate。後勝ちにはしない。
4. 重複判定後、Cancelled、Damage < MinimumDamage、未知プレイヤー、未知スキル、未装備スキルはreject。MinimumDamageは0以上なので負のダメージも拒否。
5. 採用ダメージは `(long)Damage * (100 + BonusPercent) / 100`。整数除算の切り捨て。装備リスト中のスキル重複でボーナスを倍増させない。
6. プレイヤー集計は採用されたイベントのみ。ダメージ0でも採用ならHitsに数える。最終順位はDamage降順、同点ならPlayerId昇順。
7. 上位TopPlayersを切り出す前の全プレイヤーでギルドを集計する。参加人数は1回以上採用イベントのあるプレイヤー数。ギルド順位はDamage降順、同点ならGuildId昇順。
8. `InputEvents == AcceptedEvents + DuplicateEvents + RejectedEvents`。入力配列は変更しない。呼出しごとのインデックス構築・入力検査コストは測定に含む。
9. キャンセルはOperationCanceledExceptionを伝播する。ストレージの例外を握り潰さない。並列バッチは入力順に結果を返す。
10. レポートはInvariantCulture・LF改行。checksumは一致確認の補助であり、正しさ・性能の証明ではない。

## 固定の手計算例

`Verification/ContractChecks.Golden()` は14イベント。最低ダメージ5、TopPlayers=2。

- 採用4、重複2、拒否8。
- Player 1: ダメージ40、2ヒット。Player 3: 12、1ヒット。Player 2: 5、1ヒット（上位2からは外れる）。
- Guild 10: ダメージ45、3ヒット、2人。Guild 20: 12、1ヒット、1人。
- EventId 11は未知プレイヤーのイベントが先に来る。後から有効な同IDが来ても採用しない。

両実装の単なる一致だけでなく、この固定値と不正入力・同点・整数上限・キャンセルを検査する。共有の仕様テストは56ケース。Hostの`--verify`とxUnitの両方から同じテストを使う。全入力での形式的な等価性証明ではない。
