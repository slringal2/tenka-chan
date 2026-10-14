"""Exercise the real local HTTP and MCP surfaces using only controlled fixtures.
No application/profiler/LLM/Docker execution is enabled. No third-party packages.
"""
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
from html.parser import HTMLParser

ROOT = Path(__file__).resolve().parents[2]

class PageParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = set()
        self.options = set()
    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if 'id' in attrs:
            assert attrs['id'] not in self.ids, 'Duplicate page ID'
            self.ids.add(attrs['id'])
        if tag == 'option':
            self.options.add(attrs.get('value', ''))

def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=15)

def run():
    artifacts = ROOT / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='tenka-priorities-') as temporary:
        temp = Path(temporary)
        env = os.environ.copy()
        env.update(TENKA_ALLOWED_ROOTS=os.pathsep.join([str(ROOT), str(temp)]),
                   TENKA_TRUST_MSBUILD='1', TENKA_ENABLE_LOCAL_EXEC='0', TENKA_ENABLE_DOCKER='0',
                   TENKA_DATA_DIR=str(temp / 'data'))
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        env['TENKA_WEB_PORT'] = str(port)
        base = f'http://127.0.0.1:{port}'
        csrf = ''
        def request(path, body=None, expected=200, use_csrf=True, origin=None):
            headers = {}
            if body is not None:
                headers['Content-Type'] = 'application/json'
                if use_csrf:
                    headers['X-Tenka-CSRF'] = csrf
            if origin is not None:
                headers['Origin'] = origin
            req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=90) as response:
                    code, data = response.status, response.read()
            except urllib.error.HTTPError as error:
                code, data = error.code, error.read()
            assert code == expected, (path, code, expected, data[:1200])
            return data
        with (temp / 'web.log').open('w', encoding='utf-8') as log:
            web = subprocess.Popen(['dotnet', str(ROOT / 'src/TenkaChan.Web/bin/Release/net10.0/TenkaChan.Web.dll')],
                                   cwd=ROOT / 'src/TenkaChan.Web', env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                for attempt in range(150):
                    if web.poll() is not None:
                        raise AssertionError((temp / 'web.log').read_text(encoding='utf-8'))
                    try:
                        session = json.loads(request('/api/session'))
                        csrf = session['csrfToken']
                        break
                    except (urllib.error.URLError, ConnectionError):
                        time.sleep(0.2)
                else:
                    raise AssertionError('Local Web host did not start')
                assert not session['trustedLocalExecutionEnabled'] and not session['dockerEnabled']
                page = PageParser()
                page.feed(request('/priorities.html').decode())
                assert {'priority-form', 'priority-run', 'priority-results', 'suppressions'} <= page.ids
                assert {'observed_profile', 'candidate', 'semantic_fact', 'compiler', 'analyzer'} <= page.options
                assert json.loads(request('/api/priorities/policy'))['version'] == '2026.10-priority.1'
                request('/api/priorities/report', {}, 403, use_csrf=False)
                request('/api/priorities/report', {}, 403, origin='https://untrusted.invalid')
                request('/api/priorities/report', {}, 400)  # no workspace yet
                request('/api/workspace/open', {'path': str(ROOT / 'tests/Fixtures/PrioritySample/PrioritySample.csproj')})
                request('/api/priorities/report', {'maxItems': 1001}, 400)
                request('/api/priorities/report', {'suppressions': [{'id': 'fake', 'reason': ''}]}, 400)
                initial = json.loads(request('/api/priorities/report', {}))
                assert initial['profileStatus'] == 'no_profile' and initial['distinctCount'] > 0
                assert initial['priorityCounts']['P0'] == 0 and all(not x['defectConfirmed'] for x in initial['items'])
                profile = temp / 'synthetic.speedscope.json'
                profile.write_text(json.dumps({'shared': {'frames': [{'name': 'PriorityFixture.Work.Run(int[])'}]},
                    'profiles': [{'type': 'sampled', 'name': 'SYNTHETIC POLICY FIXTURE', 'unit': 'milliseconds',
                                  'samples': [[0]] * 40, 'weights': [1] * 40}]}), encoding='utf-8')
                request('/api/hotspots/profile/import', {'filePath': str(profile), 'profileIndex': 0, 'maxFrames': 100})
                measured = json.loads(request('/api/priorities/report', {}))
                top = measured['items'][0]
                assert top['priority'] == 'P1' and top['kind'] == 'observed_frame' and top['unit'] == 'milliseconds', top
                assert top['methodId'] and not top['defectConfirmed'] and top['selfPercent'] == 100
                assert all(x['priority'] != 'P1' for x in measured['items'] if x['kind'] != 'observed_frame')
                suppressed = [{'id': top['id'], 'reason': 'controlled fixture, not a production defect'}]
                hidden = json.loads(request('/api/priorities/report', {'suppressions': suppressed}))
                assert hidden['suppressedCount'] == 1 and hidden['distinctCount'] == measured['distinctCount']
                assert hidden['priorityCounts'] == measured['priorityCounts'] and all(x['id'] != top['id'] for x in hidden['items'])
                visible = json.loads(request('/api/priorities/report', {'suppressions': suppressed, 'includeSuppressed': True}))
                assert visible['items'][0]['suppressed']
                filtered = json.loads(request('/api/priorities/report', {'priorityFilter': 'P0'}))
                assert not filtered['items'] and filtered['distinctCount'] == measured['distinctCount']
                limited = json.loads(request('/api/priorities/report', {'maxItems': 1}))
                assert limited['returnedCount'] == 1 and limited['outputTruncated']
                (artifacts / 'priority-http.json').write_text(json.dumps({'scope': 'synthetic HTTP fixture',
                    'initial': initial, 'measured': measured, 'suppressionCount': hidden['suppressedCount']}, indent=2), encoding='utf-8')
            finally:
                stop(web)
        # Check actual stdio MCP registration, JSON argument binding and tool invocation.
        with (temp / 'mcp.log').open('w', encoding='utf-8') as log:
            mcp = subprocess.Popen(['dotnet', str(ROOT / 'src/TenkaChan.Mcp/bin/Release/net10.0/TenkaChan.Mcp.dll')],
                                   cwd=ROOT, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=log, text=True, encoding='utf-8')
            messages = queue.Queue()
            def read_lines():
                for line in mcp.stdout:
                    messages.put(line)
            threading.Thread(target=read_lines, daemon=True).start()
            next_id = 0
            def call(method, params):
                nonlocal next_id
                next_id += 1
                mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': next_id, 'method': method, 'params': params}) + '\n')
                mcp.stdin.flush()
                while True:
                    message = json.loads(messages.get(timeout=90))
                    if message.get('id') == next_id:
                        assert 'error' not in message, message
                        return message['result']
            def tool(name, arguments):
                result = call('tools/call', {'name': name, 'arguments': arguments})
                assert not result.get('isError'), result
                return json.loads(next(x['text'] for x in result['content'] if x['type'] == 'text'))
            try:
                call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'priority-ci', 'version': '1'}})
                mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n'); mcp.stdin.flush()
                tools = call('tools/list', {})['tools']
                assert {'review_priority_report', 'review_priority_policy'} <= {x['name'] for x in tools}
                assert tool('review_priority_policy', {})['version'] == '2026.10-priority.1'
                tool('workspace_open', {'path': str(ROOT / 'tests/Fixtures/PrioritySample/PrioritySample.csproj')})
                report = tool('review_priority_report', {'maxItems': 50})
                assert report['distinctCount'] > 0 and report['profileStatus'] == 'no_profile'
                assert report['priorityCounts']['P0'] == 0
                (artifacts / 'priority-mcp.json').write_text(json.dumps({'scope': 'synthetic MCP fixture', 'report': report}, indent=2), encoding='utf-8')
            finally:
                stop(mcp)
    print('HTTP and MCP priority workflows passed; no target, profiler, LLM or Docker execution was enabled.')

if __name__ == '__main__':
    run()
