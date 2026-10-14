"""CI-only real runtime capture plus read-only HTTP/stdio MCP validation."""
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

REPO = Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:5096'


def stop(process):
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def validate(report):
    assert report['runtimes'], report
    assert not report['eventLimitReached'], report
    assert any(r['collectionsCompleted'] >= 4 for r in report['runtimes']), report
    assert any(r['gcSuspensionWindows'] >= 4 for r in report['runtimes']), report
    assert any(r['allocationTicks'] > 0 for r in report['runtimes']), report
    assert any(r['contentionWaitMilliseconds'] > 0 for r in report['runtimes']), report
    for runtime in report['runtimes']:
        assert runtime['cpuMilliseconds'] is None, runtime
        assert runtime['otherWaitMilliseconds'] is None, runtime
        assert all(not h['defectConfirmed'] for h in runtime['investigationHints'])
    assert report['sourceAssociation'] == 'capture_input_identity_only__no_native_or_source_attribution'


def run_mcp(env, report_path, expected_hash, log):
    safe_env = {**env, 'TENKA_ENABLE_LOCAL_EXEC': '0'}
    proc = subprocess.Popen(['dotnet', str(REPO / 'src/TenkaChan.Mcp/bin/Release/net10.0/TenkaChan.Mcp.dll')],
        cwd=REPO, env=safe_env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
        text=True, encoding='utf-8', bufsize=1)
    responses = queue.Queue()
    def receive():
        for line in proc.stdout:
            try:
                responses.put(json.loads(line))
            except json.JSONDecodeError:
                responses.put({'unexpected_output': line})
    threading.Thread(target=receive, daemon=True).start()
    next_id = 0
    def call(method, params):
        nonlocal next_id
        next_id += 1
        proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': next_id, 'method': method, 'params': params}) + '\n')
        proc.stdin.flush()
        deadline = time.monotonic() + 65
        while time.monotonic() < deadline:
            result = responses.get(timeout=max(.1, deadline - time.monotonic()))
            assert 'unexpected_output' not in result, result
            if result.get('id') == next_id:
                assert 'error' not in result, result
                return result['result']
        raise AssertionError('MCP response timeout')
    try:
        call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'runtime-cost-ci', 'version': '1'}})
        proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n'); proc.stdin.flush()
        tools = call('tools/list', {})
        names = {tool['name'] for tool in tools['tools']}
        assert {'performance_runtime_costs', 'performance_runtime_capture'} <= names, names
        response = call('tools/call', {'name': 'performance_runtime_costs', 'arguments': {'reportPath': report_path}})
        assert not response.get('isError'), response
        report = json.loads(next(c['text'] for c in response['content'] if c['type'] == 'text'))
        validate(report)
        assert report['traceSha256'] == expected_hash
        denied = call('tools/call', {'name': 'performance_runtime_capture', 'arguments': {
            'assemblyPath': env['TENKA_RUNTIME_FIXTURE'], 'outputDirectory': str(Path(report_path).parent), 'trustedBinary': True}})
        assert denied.get('isError') is True, denied
    finally:
        stop(proc)


def main():
    with tempfile.TemporaryDirectory(prefix='tenka-runtime-http-') as output:
        env = {**os.environ, 'TENKA_WEB_PORT': '5096', 'TENKA_TRUST_MSBUILD': '0', 'TENKA_ENABLE_LOCAL_EXEC': '1',
            'TENKA_DOTNET_TRACE_PATH': os.environ['TENKA_TEST_TRACE_TOOL'],
            'TENKA_ALLOWED_ROOTS': os.pathsep.join([str(REPO), output]), 'TENKA_DATA_DIR': output}
        token = ''
        def request(path, body=None, expected=200, csrf=True, origin=None):
            headers = {'Content-Type': 'application/json'}
            if csrf:
                headers['X-Tenka-CSRF'] = token
            if origin:
                headers['Origin'] = origin
            req = urllib.request.Request(BASE + path, headers=headers, data=None if body is None else json.dumps(body).encode())
            try:
                with urllib.request.urlopen(req, timeout=120) as r:
                    status, data = r.status, r.read()
            except urllib.error.HTTPError as error:
                status, data = error.code, error.read()
            assert status == expected, (path, status, expected, data[:3000])
            return json.loads(data) if data else None
        with open(Path(output) / 'process.log', 'w+', encoding='utf-8') as log:
            web = subprocess.Popen(['dotnet', 'bin/Release/net10.0/TenkaChan.Web.dll'],
                cwd=REPO / 'src/TenkaChan.Web', env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                for _ in range(100):
                    try:
                        session = request('/api/session'); break
                    except (urllib.error.URLError, TimeoutError):
                        time.sleep(.2)
                else:
                    raise AssertionError('Web host failed to start')
                token = session['csrfToken']
                with urllib.request.urlopen(BASE + '/trace.html') as r:
                    html = r.read()
                    assert b'runtimeCostEvents' in html and b'runtime-costs' in html
                missing = {'reportPath': 'missing.json'}
                request('/api/traces/runtime-costs', missing, 403, csrf=False)
                request('/api/traces/runtime-costs', missing, 403, origin='https://untrusted.invalid')
                request('/api/traces/runtime-costs', {**missing, 'maxRows': 0}, 400)
                payload = {'assemblyPath': env['TENKA_RUNTIME_FIXTURE'], 'outputDirectory': output,
                    'trustedBinary': True, 'runtimeCostEvents': True, 'timeoutSeconds': 90}
                request('/api/traces/capture', {**payload, 'trustedBinary': False}, 400)
                captured = request('/api/traces/capture', payload)
                assert captured['status'] == 'trace_completed', captured
                assert captured['runtimeProviderConfiguration'].endswith(':5'), captured
                args = {'reportPath': captured['reportPath'], 'pauseBudgetMs': 10}
                report = request('/api/traces/runtime-costs', args)
                validate(report)
                assert report['traceSha256'] == captured['netTrace']['sha256']
                request('/api/traces/runtime-costs', {**args, 'processId': -1}, 400)
                bounded = request('/api/traces/runtime-costs', {**args, 'maxEvents': 100, 'maxRows': 1})
                assert bounded['eventLimitReached'] and bounded['eventsRead'] == 100, bounded
                assert bounded['status'] == 'partial_event_limit'
                run_mcp(env, captured['reportPath'], report['traceSha256'], log)
                (REPO / 'runtime-http-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
                trace = Path(captured['reportPath']).parent / 'workload.nettrace'
                with trace.open('ab') as f:
                    f.write(b'changed')
                request('/api/traces/runtime-costs', args, 400)
                print('PASS: real verbose HTTP capture, independent runtime cost evidence, stdio MCP read with execution disabled, bounds, CSRF/Origin/trust and modified-artifact rejection')
            except Exception:
                log.flush(); log.seek(0); print(log.read()[-20000:]); raise
            finally:
                stop(web)


if __name__ == '__main__':
    main()
