"""Controlled CI-only end-to-end check. Never executes arbitrary user inputs."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

repo = Path(__file__).resolve().parents[2]
base = 'http://127.0.0.1:5098'
csrf = ''
def request(path, data=None, token=True):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['X-Tenka-CSRF'] = csrf
    req = urllib.request.Request(base + path, headers=headers,
        data=None if data is None else json.dumps(data).encode())
    with urllib.request.urlopen(req, timeout=120) as response:
        return json.load(response)

with tempfile.TemporaryDirectory(prefix='tenka-web-trace-') as output:
    env = os.environ.copy()
    env.update(TENKA_WEB_PORT='5098', TENKA_TRUST_MSBUILD='1', TENKA_ENABLE_LOCAL_EXEC='1',
        TENKA_DOTNET_TRACE_PATH=env['TENKA_TEST_TRACE_TOOL'],
        TENKA_ALLOWED_ROOTS=os.pathsep.join([str(repo), output]), TENKA_DATA_DIR=output)
    web = repo / 'src/TenkaChan.Web'
    with open(Path(output) / 'web.log', 'w+') as log:
        process = subprocess.Popen(['dotnet', 'bin/Release/net10.0/TenkaChan.Web.dll'], cwd=web, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            for _ in range(80):
                try:
                    session = request('/api/session')
                    break
                except (urllib.error.URLError, TimeoutError):
                    time.sleep(.25)
            else:
                raise AssertionError('Local Web API did not start')
            csrf = session['csrfToken']
            with urllib.request.urlopen(base + '/trace.html') as response:
                assert b'/trace.js' in response.read()
            payload = {'assemblyPath': env['TENKA_TRACE_FIXTURE'], 'outputDirectory': output, 'trustedBinary': True, 'timeoutSeconds': 90}
            for data, token, status in [(payload, False, 403), ({**payload, 'trustedBinary': False}, True, 400)]:
                try:
                    request('/api/traces/capture', data, token)
                    raise AssertionError('Unauthorized execution accepted')
                except urllib.error.HTTPError as error:
                    assert error.code == status, error
            captured = request('/api/traces/capture', payload)
            assert captured['status'] == 'trace_completed', captured
            report_path = captured['reportPath']
            saved = request('/api/traces/report?' + urllib.parse.urlencode({'reportPath': report_path}))
            assert saved['id'] == captured['id']
            request('/api/workspace/open', {'path': str(repo / 'tests/Fixtures/TrustedTraceApp/TrustedTraceApp.csproj')})
            matched = None
            for choice in saved['profiles']:
                if not choice['observations']:
                    continue
                imported = request('/api/traces/import', {'reportPath': report_path, 'profileIndex': choice['index'], 'maxFrames': 500})
                if any('Workload.Busy' in f['frameName'] and f['matchedMethodId'] for f in imported['report']['frames']):
                    matched = imported
                    break
            assert matched is not None, 'Real captured busy method not linked to Roslyn source'
            triage = request('/api/hotspots/triage?maxObserved=200&maxStatic=30')
            assert any(e['methodId'] and 'Workload.Busy' in e['name'] for e in triage['observed']), triage
            comparison = request('/api/traces/compare-binary', {'reportPath': report_path, 'assemblyPath': env['TENKA_TRACE_FIXTURE']})
            assert comparison['sameRecordedInputs'] is True, comparison
            print('PASS: trusted capture -> saved integrity -> explicit thread -> Roslyn -> measured triage; CSRF/trust denial')
        except Exception:
            log.flush(); log.seek(0); print(log.read()[-20000:])
            raise
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=5)
