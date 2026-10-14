"""Opt-in test against the actual running Tenka server; NOT CI.
This opens the trusted bundled project and runs actual Roslyn analysis.
Requires --approve-msbuild and an already-running .NET 10 Web server.
"""
import argparse
import json
from urllib.parse import urlparse
from urllib.request import Request, urlopen

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base', default='http://127.0.0.1:5077')
    parser.add_argument('--approve-msbuild', required=True, action='store_true')
    args = parser.parse_args()
    u = urlparse(args.base)
    if u.scheme != 'http' or u.hostname not in ('127.0.0.1', 'localhost', '::1') or u.username or u.password or u.path not in ('', '/') or u.query or u.fragment:
        parser.error('Use the local Tenka origin only.')
    origin = args.base.rstrip('/')
    token = ''
    def call(path, body=None):
        headers = {'Accept': 'application/json'}
        data = None
        if body is not None:
            headers.update({'Content-Type': 'application/json', 'X-Tenka-CSRF': token, 'Origin': origin})
            data = json.dumps(body).encode()
        with urlopen(Request(origin + '/api' + path, data=data, headers=headers), timeout=180) as response:
            return json.load(response)
    token = call('/session')['csrfToken']
    sample = call('/samples/game-lab')
    assert sample['available'], sample
    call('/workspace/open', {'path': sample['projectPath']})
    report = call('/deterministic/run', {'maxFindings': 5000, 'maxMethodMetrics': 500})
    rules = {x['ruleId'] for x in report['findings'] if x['file'].endswith('GameWorkloads.cs')}
    expected = {'PERF1001', 'PERF1002', 'ASYNC1001', 'ASYNC1003'}
    assert expected <= rules, (expected - rules, report)
    print('Actual Roslyn sample checks passed:', ', '.join(sorted(expected)))
if __name__ == '__main__': main()
