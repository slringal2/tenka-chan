const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pages, route } = require('../../src/TenkaChan.Web/wwwroot/common-shell.js');
const { sampleLinks, load } = require('../../src/TenkaChan.Web/wwwroot/guide.js');
const root = path.join(__dirname, '../../src/TenkaChan.Web/wwwroot');
for (const [key, title, icon, href, purpose] of pages) {
  test(`unified route: ${key}`, () => {
    const u = new URL(href, 'http://localhost:5077');
    assert.equal(route(u.pathname, u.hash), key);
    assert.ok(title && icon && purpose);
    assert.equal(u.origin, 'http://localhost:5077');
  });
}
test('unknown hashes cannot become an external route', () => {
  assert.equal(route('/', '#https://example.com'), 'overview');
  assert.equal(route('/unregistered', ''), null);
  assert.equal(route('/assembly', ''), 'assembly');
});
test('route keys are unique and old screen names remain available', () => {
  assert.equal(new Set(pages.map(p => p[0])).size, 15);
  for (const name of ['project', 'build', 'assembly', 'trace', 'priorities']) assert.ok(pages.find(p => p[0] === name));
});
for (const page of ['index', 'workspace-build', 'assembly', 'trace', 'priorities', 'guide', 'project', 'review-progress']) {
  test(`${page} loads the same theme and initializes the shell first`, () => {
    const html = fs.readFileSync(path.join(root, page + '.html'), 'utf8');
    assert.match(html, /href="\/styles.css"/);
    assert.match(html, /href="\/common-shell.css"/);
    assert.doesNotMatch(html, /href="\/?(?:assembly|trace|priorities)\.css"/);
    assert.doesNotMatch(html, /<iframe\b/i);
    const scripts = [...html.matchAll(/<script[^>]*src="([^"]+)"/g)].map(m => m[1]);
    assert.equal(scripts[0], '/common-shell.js');
    const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]);
    assert.equal(new Set(ids).size, ids.length, 'duplicate input ID');
  });
}
test('sample project paths are encoded as data, never executable navigation', () => {
  const p = 'C:\\projects\\比較 & sample\\App.csproj';
  const links = sampleLinks({ available: true, projectPath: p });
  assert.equal(new URL(links.start, 'http://localhost').searchParams.get('project'), p);
  assert.match(links.start, /^\/workspace-build.html\?/);
});
test('missing sample cannot masquerade as ready', () => {
  for (const value of [null, {}, { available: false }, { available: true, projectPath: '' }])
    assert.throws(() => sampleLinks(value));
});
function dom() {
  const nodes = new Map();
  return { nodes, getElementById(id) { if (!nodes.has(id)) nodes.set(id, { textContent: '', href: '' }); return nodes.get(id); } };
}
test('loading tutorial only sends a read and preserves non-execution semantics', async () => {
  const doc = dom(), calls = [];
  await load(doc, async (...args) => {
    calls.push(args);
    return { ok: true, json: async () => ({ available: true, projectPath: 'C:\\sample\\GameLab.csproj', sampleDirectory: 'C:\\sample', assemblyExists: false }) };
  });
  assert.equal(calls.length, 1); assert.equal(calls[0][0], '/api/samples/game-lab');
  assert.equal(calls[0][1].method, 'GET');
  assert.match(doc.getElementById('guide-start').href, /^\/workspace-build/);
  assert.match(doc.getElementById('guide-status').textContent, /見つかりました/);
});
test('a server needing upgrade is reported without a false ready state', async () => {
  const doc = dom();
  await load(doc, async () => ({ ok: false, status: 404 }));
  assert.match(doc.getElementById('guide-status').textContent, /再起動/);
  assert.equal(doc.getElementById('guide-start').href, '');
});
test('malformed sample response remains unavailable', async () => {
  const doc = dom(); await load(doc, async () => ({ ok: true, json: async () => ({ available: false }) }));
  assert.doesNotMatch(doc.getElementById('guide-status').textContent, /見つかりました/);
});
