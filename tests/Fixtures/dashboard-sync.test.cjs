const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { dashboardRevision, dashboardPathValue, dashboardDllSelection, dashboardStageText,
  DashboardSyncController, DashboardRefreshSuperseded } = require('../../src/TenkaChan.Web/wwwroot/dashboard-sync.js');
const web = path.resolve(__dirname, '../../src/TenkaChan.Web/wwwroot');
const overview = extra => ({ loaded: true, path: 'C:\\Demo\\App.csproj', snapshotId: 'one',
  lastOpened: '2026-10-15T00:00:00Z', lastScanned: '2026-10-15T00:01:00Z',
  scanned: true, findingCount: 3, deterministicComplete: true, deterministicFindingCount: 4,
  hotspotCandidates: 5, hotspotMeasuredFrames: 0, ...extra });
function fixture(extra = {}) {
  const calls = [], applied = [];
  const data = overview(extra);
  const responses = {
    '/overview': data,
    '/workspace/quick-config': { snapshotId: data.snapshotId, assemblies: [], jitMethods: [] },
    '/methods?limit=80': [], '/diagnostics': { diagnostics: [] },
    '/deterministic/report': { summary: { totalFindings: 4 }, findings: [], methodMetrics: [] },
    '/hotspots/report': { staticAnalysis: { totalSites: 5 }, sampledProfile: null }
  };
  const read = async key => { calls.push(key); assert.ok(Object.hasOwn(responses, key), key); return responses[key]; };
  const client = new DashboardSyncController({ read, apply: bundle => applied.push(bundle) });
  return { client, calls, applied, responses, read, data };
}
test('prepared results are read and published without any mutation endpoint', async () => {
  const { client, calls, applied } = fixture();
  assert.equal(await client.refresh(), true);
  assert.equal(applied.length, 1);
  assert.equal(applied[0].overview.findingCount, 3);
  assert.equal(applied[0].deterministic.summary.totalFindings, 4);
  assert.equal(calls.filter(x => x === '/overview').length, 2);
  assert.ok(calls.every(x => !/\/(open|build|run|scan|capture|prepare)(\?|$)/.test(x)));
});
test('stable polling only checks overview and does not rebuild result lists', async () => {
  const { client, calls, applied } = fixture();
  await client.refresh(); calls.length = 0;
  assert.equal(await client.refresh(), false);
  assert.deepEqual(calls, ['/overview']); assert.equal(applied.length, 1);
});
test('manual or focus refresh reads detail reports even at the same revision', async () => {
  const { client, calls, applied } = fixture();
  await client.refresh(); calls.length = 0;
  assert.equal(await client.refresh(true), true);
  assert.ok(calls.includes('/hotspots/report')); assert.equal(applied.length, 2);
});
test('returning after another tab completes a scan detects the new timestamp', async () => {
  const { client, responses, applied } = fixture({ scanned: false, lastScanned: null });
  await client.refresh();
  responses['/overview'] = overview({ lastScanned: '2026-10-15T01:00:00Z' });
  assert.equal(await client.refresh(), true); assert.equal(applied.length, 2);
});
test('restart/unloaded workspace clears server-scoped data instead of showing old results', async () => {
  const { client, responses, calls, applied } = fixture();
  await client.refresh(); calls.length = 0;
  responses['/overview'] = { loaded: false, path: null, snapshotId: null };
  await client.refresh();
  const final = applied.at(-1);
  assert.equal(final.overview.loaded, false); assert.equal(final.deterministic, null);
  assert.equal(final.quick, null); assert.deepEqual(final.targets, []);
  assert.deepEqual(calls, ['/overview', '/overview']);
});
test('snapshot mismatch in quick configuration never publishes a mixed report', async () => {
  const { client, responses, applied } = fixture();
  responses['/workspace/quick-config'].snapshotId = 'two';
  await assert.rejects(client.refresh(), DashboardRefreshSuperseded);
  assert.equal(applied.length, 0);
});
test('workspace changed during detail reads is discarded', async () => {
  const { client, read, applied } = fixture(); let reads = 0;
  client.read = async key => key === '/overview' && ++reads > 1 ? overview({ snapshotId: 'two' }) : read(key);
  await assert.rejects(client.refresh(), DashboardRefreshSuperseded);
  assert.equal(applied.length, 0);
});
test('explicit local action invalidates an in-flight refresh', async () => {
  const { client, read, applied } = fixture();
  client.read = async key => { if (key === '/diagnostics') client.invalidate(); return read(key); };
  await assert.rejects(client.refresh(), DashboardRefreshSuperseded);
  assert.equal(applied.length, 0);
});
test('refresh is suppressed while an explicit operation is running', async () => {
  const { client, calls } = fixture(); client.busy = () => true;
  assert.equal(await client.refresh(true), false); assert.equal(calls.length, 0);
});
test('operations starting after a request prevent publishing its late response', async () => {
  const { client, read, applied } = fixture(); let busy = false;
  client.busy = () => busy;
  client.read = async key => { if (key === '/diagnostics') busy = true; return read(key); };
  await assert.rejects(client.refresh(), DashboardRefreshSuperseded);
  assert.equal(applied.length, 0);
});
test('focus and visibility events coalesce rather than duplicating full requests', async () => {
  const { client, read, applied } = fixture(); let release;
  const barrier = new Promise(resolve => { release = resolve; });
  client.read = async key => { await barrier; return read(key); };
  const first = client.refresh(true), second = client.refresh(true);
  assert.equal(first, second); release(); await first; assert.equal(applied.length, 1);
});
test('read failure is not retried by the controller and does not publish data', async () => {
  const { client, applied } = fixture(); let attempts = 0;
  client.read = async () => { attempts++; throw new Error('server unavailable'); };
  await assert.rejects(client.refresh(), /server unavailable/);
  assert.equal(attempts, 1); assert.equal(applied.length, 0); assert.equal(client.pending, null);
});
test('failed detail reads do not cache a completed revision', async () => {
  const { client, read, applied } = fixture();
  client.read = async key => { if (key === '/diagnostics') throw new Error('busy'); return read(key); };
  await assert.rejects(client.refresh(), /busy/); assert.equal(client.revision, null);
  client.read = read; await client.refresh(); assert.equal(applied.length, 1);
});
test('invalid overview responses are rejected rather than interpreted as an empty workspace', async () => {
  for (const response of [{}, { loaded: 'false' }, { loaded: true }, { loaded: true, snapshotId: '' }]) {
    const { client, responses, applied } = fixture(); responses['/overview'] = response;
    await assert.rejects(client.refresh()); assert.equal(applied.length, 0);
  }
});
test('active methods view keeps its query and separate unfiltered overview targets', async () => {
  const { client, responses, applied } = fixture();
  client.context = () => ({ view: 'methods', methodQuery: 'Sum value' });
  responses['/methods?q=Sum+value&limit=80'] = [{ marker: 'filtered' }];
  await client.refresh();
  assert.deepEqual(applied[0].targets, []); assert.equal(applied[0].methods[0].marker, 'filtered');
});
test('active findings preserve the selected filter', async () => {
  const { client, responses, applied } = fixture();
  client.context = () => ({ view: 'findings', findingQuery: 'loop', findingCategory: 'performance' });
  responses['/findings?q=loop&category=performance&offset=0&limit=80'] = { total: 1, items: [{ ruleId: 'x' }] };
  await client.refresh(); assert.equal(applied[0].findings.total, 1);
});
test('empty project input receives the server path', () => assert.equal(dashboardPathValue('', '', 'C:\\App.sln'), 'C:\\App.sln'));
test('previously auto-filled path follows a newly opened workspace', () =>
  assert.equal(dashboardPathValue('old', 'old', 'new'), 'new'));
test('manual path edits survive refresh including a deliberately emptied field', () => {
  assert.equal(dashboardPathValue('manual', 'old', 'new'), 'manual');
  assert.equal(dashboardPathValue('', 'old', 'new', true), '');
});
test('failed/restarted workspace preserves a path for retry', () => assert.equal(dashboardPathValue('old', 'old', null), 'old'));
test('ambiguous DLLs are not silently selected while a single candidate is usable', () => {
  const list = [{ assemblyPath: 'one' }, { assemblyPath: 'two' }];
  assert.equal(dashboardDllSelection(list, ''), '');
  assert.equal(dashboardDllSelection(list, 'two'), 'two');
  assert.equal(dashboardDllSelection(list, 'missing'), '');
  assert.equal(dashboardDllSelection([list[0]], ''), 'one');
  assert.equal(dashboardDllSelection([], ''), '');
});
test('setup-only, unrun, confirmed zero findings and pending report differ', () => {
  assert.match(dashboardStageText({ loaded: false }, null), /セットアップだけ/);
  assert.match(dashboardStageText(overview({ scanned: false, deterministicComplete: false }), null), /未実行/);
  assert.match(dashboardStageText(overview({ findingCount: 0, deterministicFindingCount: 0, hotspotCandidates: 0 }), { staticAnalysis: {} }), /0 件の候補/);
  assert.match(dashboardStageText(overview(), undefined), /確認中/);
});
test('revision tracks rebuild and scan state even if counts stay unchanged', () => {
  assert.notEqual(dashboardRevision(overview()), dashboardRevision(overview({ snapshotId: 'two' })));
  assert.notEqual(dashboardRevision(overview()), dashboardRevision(overview({ lastScanned: 'later' })));
  assert.equal(dashboardRevision(overview()), dashboardRevision(overview()));
});
test('dashboard includes the bridge after its core and native picker, with reachable workbench', () => {
  const html = fs.readFileSync(path.join(web, 'index.html'), 'utf8');
  assert.ok(html.indexOf('src="/app.js"') < html.indexOf('src="/dashboard-sync.js"'));
  assert.ok(html.includes('src="/path-picker.js"'));
  for (const id of ['dashboard-refresh', 'dashboard-stage-summary', 'dashboard-sync-status'])
    assert.ok(html.includes(`id="${id}"`));
  for (const page of ['workspace-build', 'assembly', 'trace', 'priorities'])
    assert.ok(html.includes(`href="/${page}.html"`));
  assert.match(html, /id="workspace-path" data-path-kind="project"/);
});
test('all dashboard picker kinds are supported and IDs or relative paths have no picker', () => {
  const html = fs.readFileSync(path.join(web, 'index.html'), 'utf8');
  const kinds = [...html.matchAll(/data-path-kind="([^"]+)"/g)].map(x => x[1]);
  assert.equal(kinds.length, 7);
  for (const kind of kinds) assert.ok(['project', 'assembly', 'source', 'profile', 'folder'].includes(kind));
  for (const name of ['projectRelativePath', 'jitExperimentId', 'localJitCaptureId', 'assemblyFile']) {
    for (const match of html.matchAll(new RegExp(`<input[^>]*name="${name}"[^>]*>`, 'g')))
      assert.doesNotMatch(match[0], /data-path-kind/);
  }
});
test('build results link straight to dashboard views without reopening the workspace', () => {
  const html = fs.readFileSync(path.join(web, 'workspace-build.html'), 'utf8');
  for (const view of ['overview', 'deterministic', 'hotspots']) assert.ok(html.includes(`href="/#${view}"`));
  assert.match(html, /パスの再入力・読み込み直しは不要です/);
});
