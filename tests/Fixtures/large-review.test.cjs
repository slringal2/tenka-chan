const test = require('node:test');
const assert = require('node:assert/strict');
const { ReviewProgressClient, queueRequest, mergeQueueRows } = require('../../src/TenkaChan.Web/wwwroot/review-progress.js');
const filters = () => ({ kind: 'method', state: 'pending', limit: 80, q: '' });
const data = (more = true) => ({ snapshotId: 's1', total: 180, items: [], summary: {}, nextCursor: more ? 'opaque_next_A-_' : null, hasMore: more });
const row = (id, status = 'unreviewed') => ({ target: { id }, state: { status } });
const response = value => ({ ok: true, status: 200, json: async () => value });

test('first page discards previous cursor and offset', () => {
  assert.deepEqual(queueRequest(filters(), data(), false, 80), {...filters(), offset: 0});
});
test('continuation uses returned cursor, never the shrinking result offset', () => {
  assert.deepEqual(queueRequest(filters(), data(), true, 80), {...filters(), snapshotId: 's1', cursor: 'opaque_next_A-_', offset: 0});
});
test('next-page availability is independent of displayed row count or mutable totals', () => {
  const current = {...data(), total: 2};
  assert.equal(queueRequest(filters(), current, true, 80).cursor, current.nextCursor);
});
test('final page does not issue another request even if previous rows remain visible', () => {
  assert.equal(queueRequest(filters(), {...data(false), total: 500}, true, 80), null);
});
test('a missing continuation is not silently replaced with an unsafe offset', () => {
  assert.throws(() => queueRequest(filters(), {...data(), nextCursor: null}, true, 80), /続き情報/);
  assert.throws(() => queueRequest(filters(), {...data(), nextCursor: ''}, true, 80), /続き情報/);
});
test('cannot request a continuation before an initial snapshot is loaded', () => {
  assert.throws(() => queueRequest(filters(), null, true), /先に/);
});
test('old servers retain offset UI compatibility only when cursor support is absent', () => {
  const old = {snapshotId: 'old', items: [], summary: {}, total: 100};
  assert.deepEqual(queueRequest(filters(), old, true, 80), {...filters(), snapshotId: 'old', offset: 80});
});
test('changed filters explicitly start a new traversal', () => {
  const changed = {...filters(), q: 'Other', state: 'needs_review'};
  const request = queueRequest(changed, data(), false, 180);
  assert.equal(request.q, 'Other'); assert.equal(request.state, 'needs_review');
  assert.equal(request.cursor, undefined); assert.equal(request.snapshotId, undefined); assert.equal(request.offset, 0);
});
test('duplicate response rows replace state but do not duplicate target entries', () => {
  const merged = mergeQueueRows([row('a'),row('b')], [row('b','checked'),row('c')], true);
  assert.deepEqual(merged.map(r => r.target.id), ['a','b','c']);
  assert.equal(merged[1].state.status, 'checked');
});
test('identical display names would not merge different stable IDs', () => {
  const a = row('a'), b = row('b'); a.target.name = b.target.name = 'Same';
  assert.equal(mergeQueueRows([a], [b], true).length, 2);
});
test('a fresh page does not retain stale entries from an older filter', () => {
  assert.deepEqual(mergeQueueRows([row('a')], [row('b')], false).map(r => r.target.id), ['b']);
});
test('queue GET safely encodes the continuation and contains no mutation credentials', async () => {
  const calls = [];
  const c = new ReviewProgressClient(async (url, init) => { calls.push({url, init}); return response(data(false)); });
  await c.queue(queueRequest(filters(), data(), true, 80));
  const url = new URL(calls[0].url, 'http://localhost');
  assert.equal(url.pathname, '/api/review-progress/queue');
  assert.equal(url.searchParams.get('cursor'), 'opaque_next_A-_'); assert.equal(url.searchParams.get('offset'),'0');
  assert.equal(calls[0].init.method, 'GET'); assert.equal(calls[0].init.body, undefined);
  assert.equal(calls[0].init.headers?.Authorization, undefined);
});
test('a new source snapshot cannot be appended to the old cursor traversal', async () => {
  let next = data(); const c = new ReviewProgressClient(async () => response(next));
  await c.queue({...filters(), offset: 0}); const saved = c.data;
  next = {...data(), snapshotId: 's2'};
  await assert.rejects(c.queue(queueRequest(filters(), saved, true)), /ソースが更新/);
  assert.equal(c.data, saved);
});
test('a server cursor conflict neither clears old data nor retries automatically', async () => {
  let calls = 0;
  const c = new ReviewProgressClient(async () => { calls++; return {ok:false,status:409,json:async()=>({error:'cursor scope changed'})}; });
  const saved=data();c.data=saved;
  await assert.rejects(c.queue(queueRequest(filters(), saved, true)), /cursor scope changed/);
  assert.equal(calls, 1); assert.equal(c.data, saved);
});
test('out-of-order old cursor response is ignored after a newer filter request', async () => {
  let release;
  const c = new ReviewProgressClient(async url => {
    if (url.includes('cursor=')) return new Promise(r => { release=()=>r(response(data(false))); });
    return response({...data(), snapshotId:'fresh'});
  });
  const old=c.queue(queueRequest(filters(),data(),true));
  const fresh=await c.queue({...filters(),q:'New',offset:0});
  release(); assert.equal(await old, null); assert.equal(c.data,fresh);
});
