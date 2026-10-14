"""Opt-in checks against a real local Tenka server. No target/JIT/trace/test run.
Opening a trusted project evaluates MSBuild; --approve-msbuild is mandatory.
"""
import argparse
import json
from urllib.error import HTTPError
from urllib.parse import urlparse, urlencode
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base', default='http://127.0.0.1:5077')
    parser.add_argument('--approve-msbuild', action='store_true', required=True)
    args = parser.parse_args()
    base = args.base.rstrip('/')
    url = urlparse(base)
    if url.scheme != 'http' or url.hostname not in ('localhost', '127.0.0.1', '::1') or url.username or url.password or url.path or url.query or url.fragment:
        parser.error('Use only the loopback HTTP server origin.')
    token = ''

    def call(path, body=None, expected=200):
        headers = {'Accept': 'application/json'}
        if body is not None:
            headers.update({'Content-Type': 'application/json', 'Origin': base, 'X-Tenka-CSRF': token})
        req = Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
        try:
            with urlopen(req, timeout=180) as response:
                status, data = response.status, response.read()
        except HTTPError as error:
            status, data = error.code, error.read()
        assert status == expected, (path, status, data.decode(errors='replace'))
        return json.loads(data)

    token = call('/api/session')['csrfToken']
    sample = call('/api/samples/game-lab')
    assert sample['available'], sample
    call('/api/workspace/open', {'path': sample['projectPath']})
    context = call('/api/explorer/context')
    snapshot = context['snapshotId']
    types = call('/api/explorer/types?' + urlencode({'snapshotId': snapshot, 'q': 'InventorySearch'}))
    assert types['items'], types
    selected = types['items'][0]
    def action(name, **fields):
        return call('/api/explorer/action', {'snapshotId': snapshot, 'action': name, **fields})
    detail = action('type', typeId=selected['id'])
    method = next(m for m in detail['detail']['methods'] if 'CountOwnedSlow' in m['signature'])
    inspection = action('method', methodId=method['id'])
    assert inspection['inspection']['source'], inspection
    references = action('method-references', methodId=method['id'])
    assert 'items' in references, references
    plan = action('test-plan', methodId=method['id'])
    assert plan['oracleStatus'] == 'unresolved', plan
    call('/api/explorer/action', {'snapshotId': 'stale', 'action': 'method', 'methodId': method['id']}, 409)
    call('/api/explorer/action', {'snapshotId': snapshot, 'action': 'jit', 'methodId': method['id'], 'approved': False}, 403)
    call('/api/explorer/action', {'snapshotId': snapshot, 'action': 'type', 'typeId': 'forged'}, 400)
    print(json.dumps({'snapshot': snapshot, 'class': selected['name'], 'method': method['signature'],
                      'references': len(references['items']), 'targetExecution': False}, ensure_ascii=False))


if __name__ == '__main__':
    main()
