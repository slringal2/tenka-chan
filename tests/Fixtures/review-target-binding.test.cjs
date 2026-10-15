// Source-shape regression checks, not a replacement for compiling C#.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const source = fs.readFileSync(path.join(root, 'src/TenkaChan.Mcp/Analysis/SourceExplorerIndex.Review.cs'), 'utf8');

function checkTrackingBinding(text) {
  const code = text.replace(/\/\/[^\r\n]*/g, '').replace(/\/\*[\s\S]*?\*\//g, '');
  const match = code.match(/using\s+(\w+)\s*=\s*global::TenkaChan\.Mcp\.Tracking\.ReviewTarget\s*;/);
  assert.ok(match, 'use an explicit global tracking type alias');
  const alias = match[1];
  assert.notEqual(alias, 'ReviewTarget', 'a same-named outer alias is shadowed by Analysis.ReviewTarget');
  assert.ok(code.includes(`new List<${alias}>()`), 'the collection must use the tracking alias');
  assert.ok(code.includes(`new ${alias}(`), 'the constructed record must use the same tracking alias');
  const withoutAlias = code.replace(match[0], '');
  assert.doesNotMatch(withoutAlias, /\bReviewTarget\b/, 'no unqualified ReviewTarget in the review catalogue');
  return alias;
}

test('review catalogue uses the tracking type explicitly for both construction and collection', () => {
  assert.equal(checkTrackingBinding(source), 'TrackedReviewTarget');
});
test('regression guard rejects restoring the originally shadowed alias', () => {
  const originalShape = source.replace(/TrackedReviewTarget/g, 'ReviewTarget');
  assert.throws(() => checkTrackingBinding(originalShape), /shadowed/);
});
test('regression guard rejects fixing the constructor but not its collection', () => {
  assert.throws(() => checkTrackingBinding(source.replace('List<TrackedReviewTarget>', 'List<ReviewTarget>')), /collection/);
});
test('regression guard rejects fixing the collection but not its constructor', () => {
  assert.throws(() => checkTrackingBinding(source.replace('new TrackedReviewTarget(', 'new ReviewTarget(')), /constructed/);
});
test('build-failure guidance does not prescribe restore for C# compiler errors', () => {
  const launcher = fs.readFileSync(path.join(root, 'scripts/tenka-windows.ps1'), 'utf8');
  assert.ok(launcher.includes('CSから始まるエラーはC#のコンパイルエラーです'));
  assert.ok(launcher.includes('setup.batの再実行だけでは解決しない場合があります'));
  assert.ok(launcher.includes('NETSDK1004など依存関係の不足が原因の場合'));
  assert.ok(launcher.includes('MSB3027/MSB3021の場合'));
});
