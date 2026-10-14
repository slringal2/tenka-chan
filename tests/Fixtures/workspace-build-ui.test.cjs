const test = require('node:test');
const assert = require('node:assert/strict');
const { WorkspaceBuildClient, describeWorkspaceBuild, identifyWorkspaceBuildProblem } = require('../../src/TenkaChan.Web/wwwroot/workspace-build.js');
const options = () => ({ loaded: true, buildEnabled: true, snapshotId: 'snapshot-1', path: 'C:\\work\\App.csproj', projects: [{ name: 'App', path: 'C:\\work\\App.csproj' }] });
const values = () => ({ projectPath: options().projects[0].path });
const success = () => ({ build: { status: 'build_succeeded' }, workspaceReloaded: true, snapshotId: 'snapshot-2', assemblies: [], analysis: { findings: 2, compilerErrors: 0, deterministicFindings: 3, hotspotCandidates: 4 } });
function fixture(handler) {
  const calls = [];
  const client = new WorkspaceBuildClient(async (path, init) => {
    calls.push({ path, init });
    if (handler) return handler(path, init);
    return { ok: true, status: 200, json: async () => success() };
  });
  client.session = { csrfToken: 'csrf-token' };
  client.options = options();
  return { client, calls };
}
test('connect uses read-only requests', async () => {
  const { client, calls } = fixture(async path => ({ ok: true, status: 200, json: async () => path.endsWith('/session') ? { csrfToken: 'new' } : options() }));
  await client.connect();
  assert.deepEqual(calls.map(c => [c.path, c.init.method]), [['/api/session', 'GET'], ['/api/workspace/build-options', 'GET']]);
  assert.equal(client.session.csrfToken, 'new');
});
test('opening a workspace sends CSRF and refreshes choices', async () => {
  const { client, calls } = fixture(async () => ({ ok: true, status: 200, json: async () => options() }));
  await client.open('  C:\\work\\App.csproj  ');
  assert.equal(calls[0].path, '/api/workspace/open');
  assert.equal(calls[0].init.headers['X-Tenka-CSRF'], 'csrf-token');
  assert.equal(JSON.parse(calls[0].init.body).path, values().projectPath);
  assert.equal(calls[1].init.method, 'GET');
});
test('declining approval never sends a build request', async () => {
  const { client, calls } = fixture();
  assert.equal(await client.build(values(), () => false), null);
  assert.equal(calls.length, 0);
  assert.equal(client.busy, false);
  assert.equal(client.options.snapshotId, 'snapshot-1');
});
test('truthy nonboolean consent is not approval', async () => {
  const { client, calls } = fixture();
  assert.equal(await client.build(values(), () => 'yes'), null);
  assert.equal(calls.length, 0);
});
test('approved request has snapshot, CSRF, no restore by default and no shell command', async () => {
  const { client, calls } = fixture();
  await client.build(values(), () => true);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].path, '/api/workspace/build');
  assert.equal(calls[0].init.credentials, 'same-origin');
  assert.deepEqual(JSON.parse(calls[0].init.body), {
    expectedSnapshotId: 'snapshot-1', projectPath: values().projectPath, targetFramework: null,
    allowRestore: false, approveUnisolatedBuild: true, analyzeAfterBuild: true, timeoutSeconds: 180
  });
  assert.equal(client.options, null);
});
test('restore, framework and analysis options require explicit values', async () => {
  const { client, calls } = fixture();
  await client.build({ ...values(), targetFramework: ' net8.0 ', allowRestore: true, analyzeAfterBuild: false, timeoutSeconds: 60 }, () => true);
  const body = JSON.parse(calls[0].init.body);
  assert.equal(body.targetFramework, 'net8.0');
  assert.equal(body.allowRestore, true);
  assert.equal(body.analyzeAfterBuild, false);
  assert.equal(body.timeoutSeconds, 60);
});
test('disabled build is rejected before confirmation or HTTP', async () => {
  const { client, calls } = fixture(); client.options.buildEnabled = false;
  await assert.rejects(client.build(values(), () => { throw new Error('must not confirm'); }), /無効/);
  assert.equal(calls.length, 0);
});
test('unloaded or missing snapshot is rejected', async () => {
  const { client, calls } = fixture(); client.options.snapshotId = null;
  await assert.rejects(client.build(values(), () => true), /読み込/);
  assert.equal(calls.length, 0);
});
test('a project outside current choices is rejected', async () => {
  const { client, calls } = fixture();
  await assert.rejects(client.build({ projectPath: 'C:\\other\\Other.csproj' }, () => true), /選択/);
  assert.equal(calls.length, 0);
});
test('invalid timeout is rejected locally', async () => {
  for (const timeoutSeconds of [0, 9, 301, 12.5, NaN, Infinity]) {
    const { client, calls } = fixture();
    await assert.rejects(client.build({ ...values(), timeoutSeconds }, () => true), /10〜300/);
    assert.equal(calls.length, 0);
  }
});
test('duplicate click is not queued while confirmation is pending', async () => {
  const { client, calls } = fixture();
  let approve;
  const pending = client.build(values(), () => new Promise(resolve => { approve = resolve; }));
  await assert.rejects(client.build(values(), () => true), /処理中/);
  approve(true); await pending;
  assert.equal(calls.length, 1);
});
test('HTTP 409 invalidates old choices and never retries the build', async () => {
  const { client, calls } = fixture(async () => ({ ok: false, status: 409, json: async () => ({ error: 'snapshot changed' }) }));
  await assert.rejects(client.build(values(), () => true), error => error.status === 409);
  assert.equal(calls.length, 1); assert.equal(client.options, null); assert.equal(client.busy, false);
});
test('HTTP 403 is not retried or treated as successful', async () => {
  const { client, calls } = fixture(async () => ({ ok: false, status: 403, json: async () => ({ error: 'denied' }) }));
  await assert.rejects(client.build(values(), () => true), /denied/);
  assert.equal(calls.length, 1); assert.equal(client.lastResult, null);
});
test('malformed results and non-JSON responses are never successes', async () => {
  for (const handler of [async () => ({ ok: true, status: 200, json: async () => ({}) }), async () => ({ ok: false, status: 500, json: async () => { throw new Error('html'); } })]) {
    const { client } = fixture(handler);
    await assert.rejects(client.build(values(), () => true), /応答/);
    assert.equal(client.lastResult, null); assert.equal(client.busy, false);
  }
});
test('a missing CSRF token prevents mutation', async () => {
  const { client, calls } = fixture(); client.session = null;
  await assert.rejects(client.build(values(), () => true), /接続情報/);
  assert.equal(calls.length, 0);
});
test('build success is distinguished from incomplete reload or analysis', () => {
  assert.match(describeWorkspaceBuild({ ...success(), workspaceReloaded: false }, true), /再読み込みは完了していません/);
  assert.match(describeWorkspaceBuild({ ...success(), analysis: null }, true), /まとめて解析は完了していません/);
  assert.match(describeWorkspaceBuild({ ...success(), analysis: null }, false), /再読み込みが完了/);
  assert.match(describeWorkspaceBuild(success(), true), /性能調査候補 4件/);
});
test('failed, cancelled and unknown statuses are not represented as success', () => {
  for (const s of ['build_failed', 'timed_out', 'cancelled', 'dotnet_unavailable', 'build_io_error', 'unknown']) {
    const message = describeWorkspaceBuild({ build: { status: s } }, true);
    assert.doesNotMatch(message, /成功|静的解析が完了/);
  }
});
test('refresh failure discards stale choices', async () => {
  const { client } = fixture(async () => ({ ok: false, status: 409, json: async () => ({ error: 'busy' }) }));
  await assert.rejects(client.refresh(), /busy/);
  assert.equal(client.options, null);
});

test('NETSDK1004 without restore provides a specific manual recovery choice', () => {
  const issue = identifyWorkspaceBuildProblem({build:{status:'build_failed',output:'error NETSDK1004: project.assets.json is missing',stderr:'',restoreRequested:false}});
  assert.equal(issue.kind,'nuget-assets-missing');
  assert.equal(issue.canRetryWithRestore,true);
  assert.match(issue.message,/再読み込み/);
});
test('NETSDK1004 after explicit restore does not suggest repeating a futile recovery', () => {
  const issue = identifyWorkspaceBuildProblem({build:{status:'build_failed',output:'error NETSDK1004',restoreRequested:true}});
  assert.equal(issue.canRetryWithRestore,false);
  assert.match(issue.message,/復元時のログ/);
});
test('DLL lock is differentiated from missing NuGet assets', () => {
  const issue=identifyWorkspaceBuildProblem({build:{status:'build_failed',output:'error MSB3027 file locked',restoreRequested:false}});
  assert.equal(issue.kind,'locked-output'); assert.equal(issue.canRetryWithRestore,false);
});
test('other failed builds do not trigger automatic NuGet retries', () => {
  assert.equal(identifyWorkspaceBuildProblem({build:{status:'build_failed',output:'CS1002'}}),null);
  assert.equal(identifyWorkspaceBuildProblem({build:{status:'build_succeeded',output:'NETSDK1004'}}),null);
});
