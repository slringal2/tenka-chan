const test = require('node:test');
const assert = require('node:assert/strict');
const { TenkaPathPicker, attachTenkaPathPickers } = require('../../src/TenkaChan.Web/wwwroot/path-picker.js');
function make(result, status = 200) {
  const calls = [];
  const picker = new TenkaPathPicker(async (path, init) => {
    calls.push({ path, init });
    return { ok: path === '/api/session' || status === 200, status,
      json: async () => path === '/api/session' ? { csrfToken: 'test-only' } : result };
  });
  return { picker, calls };
}
test('native selection sends CSRF and data, never uploads file content', async () => {
  const path = 'C:\\日本語 & !\\App.sln';
  const { picker, calls } = make({ cancelled: false, path });
  assert.equal(await picker.pick('project', path), path);
  assert.equal(calls.length, 2);
  assert.equal(calls[1].init.credentials, 'same-origin');
  assert.equal(calls[1].init.headers['X-Tenka-CSRF'], 'test-only');
  assert.deepEqual(JSON.parse(calls[1].init.body), { kind: 'project', initialPath: path });
});
test('cancel returns no path and releases lock', async () => {
  const { picker } = make({ cancelled: true, path: 'must-not-use' });
  assert.equal(await picker.pick('project', ''), null);
  assert.equal(picker.busy, false);
});
for (const status of [403, 409, 408, 501, 500]) {
  test(`HTTP ${status} is visible and not automatically retried`, async () => {
    const { picker, calls } = make({ error: 'explicit failure' }, status);
    await assert.rejects(picker.pick('project', ''), /explicit failure/);
    assert.equal(calls.length, 2);
    assert.equal(picker.busy, false);
  });
}
test('missing CSRF never sends pick request', async () => {
  let calls = 0;
  const picker = new TenkaPathPicker(async () => {
    calls++; return { ok: true, json: async () => ({}) };
  });
  await assert.rejects(picker.pick('project',''), /接続情報/);
  assert.equal(calls, 1);
});
test('malformed results are not used as paths', async () => {
  for (const data of [{}, { cancelled: false, path: '' }, { path: 'x' }, { cancelled: 'false', path: 'x' }]) {
    const { picker } = make(data);
    await assert.rejects(picker.pick('project', ''), /選択結果/);
  }
});
test('double click does not create two native dialogs', async () => {
  let finish;
  const picker = new TenkaPathPicker(async path => {
    if (path === '/api/session') return { ok: true, json: async () => ({ csrfToken: 'test-only' }) };
    return new Promise(resolve => { finish = () => resolve({ ok: true, json: async () => ({ cancelled: true }) }); });
  });
  const first = picker.pick('project', '');
  while (!finish) await new Promise(resolve => setImmediate(resolve));
  await assert.rejects(picker.pick('project', ''), /選択画面/);
  finish(); await first;
});
// Minimal DOM fixture tests the real click handler (not a browser rendering test).
async function domCase(result, changeWhileOpen) {
  const old = { document: global.document, fetch: global.fetch, location: global.location };
  const events = [];
  const controls = [];
  const input = { value: 'C:\\old.sln', disabled: false, dataset: { pathKind: 'project' },
    after: (...items) => controls.push(...items), dispatchEvent: e => events.push(e.type) };
  global.document = { querySelectorAll: () => [input], querySelector: () => input,
    createElement: () => ({ setAttribute() {}, addEventListener(name, fn) { this[name] = fn; } }) };
  global.location = { href: 'http://127.0.0.1:5077/workspace-build.html' };
  global.fetch = async path => ({ ok: true, json: async () => {
    if (path === '/api/session') return { csrfToken: 'test-only' };
    if (changeWhileOpen) input.value = 'C:\\manually-changed.sln';
    return result;
  } });
  try {
    attachTenkaPathPickers();
    assert.equal(controls[0].type, 'button');
    await controls[0].click();
    return { input, events, message: controls[1].textContent };
  } finally { Object.assign(global, old); }
}
test('selected path fills the input and emits change without form submission', async () => {
  const { input, events } = await domCase({ cancelled: false, path: 'C:\\new.sln' });
  assert.equal(input.value, 'C:\\new.sln');
  assert.deepEqual(events, ['input', 'change']);
});
test('cancel preserves old input and emits no changes', async () => {
  const { input, events } = await domCase({ cancelled: true });
  assert.equal(input.value, 'C:\\old.sln'); assert.deepEqual(events, []);
});
test('late selection does not overwrite a manual edit', async () => {
  const { input, events, message } = await domCase({ cancelled: false, path: 'C:\\new.sln' }, true);
  assert.equal(input.value, 'C:\\manually-changed.sln'); assert.deepEqual(events, []);
  assert.match(message, /上書きしません/);
});
