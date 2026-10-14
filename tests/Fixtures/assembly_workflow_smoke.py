"""Read-only assembly HTTP/MCP checks; no application, profiler, MSBuild or Docker execution enabled."""
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
LISTING = '''; Assembly listing for method Fixture:Work():int (FullOpts)
; Emitting BLENDED_CODE for X64
       31C0 xor eax, eax
       FFC0 inc eax
       83F80A cmp eax, 10
       7CF9 jl loop
       C3 ret
; Total bytes of code 10
'''

class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = set()
    def handle_starttag(self, tag, attributes):
        value = dict(attributes).get('id')
        if value:
            assert value not in self.ids, value
            self.ids.add(value)

def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)

def run():
    artifacts = ROOT / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='tenka-assembly-http-') as temporary:
        temp = Path(temporary)
        listing = temp / 'loop.txt'
        listing.write_text(LISTING, encoding='utf-8')
        missing = temp / 'missing.txt'
        missing.write_text(LISTING.replace('31C0 xor', 'xor'), encoding='utf-8')
        dll = ROOT / 'tests/Fixtures/TrustedLocalJitApp/bin/Release/net8.0/TrustedLocalJitApp.dll'
        env = os.environ.copy()
        env.update(TENKA_ALLOWED_ROOTS=os.pathsep.join([str(ROOT), str(temp)]),
                   TENKA_TRUST_MSBUILD='0', TENKA_ENABLE_LOCAL_EXEC='0', TENKA_ENABLE_DOCKER='0',
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
            if origin:
                headers['Origin'] = origin
            req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=90) as response:
                    code, data = response.status, response.read()
            except urllib.error.HTTPError as error:
                code, data = error.code, error.read()
            assert code == expected, (path, code, expected, data[:2000])
            return data
        with (temp / 'web.log').open('w', encoding='utf-8') as log:
            web = subprocess.Popen(['dotnet', str(ROOT / 'src/TenkaChan.Web/bin/Release/net10.0/TenkaChan.Web.dll')],
                                   cwd=ROOT / 'src/TenkaChan.Web', env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                for _ in range(150):
                    if web.poll() is not None:
                        raise AssertionError((temp / 'web.log').read_text(encoding='utf-8'))
                    try:
                        session = json.loads(request('/api/session'))
                        csrf = session['csrfToken']
                        break
                    except (urllib.error.URLError, ConnectionError):
                        time.sleep(0.2)
                else:
                    raise AssertionError('Web host did not start')
                assert not session['msbuildEnabled'] and not session['trustedLocalExecutionEnabled'] and not session['dockerEnabled']
                page = Page()
                page.feed(request('/assembly.html').decode())
                assert {'managed', 'native', 'native-result', 'methods', 'il', 'compare', 'export'} <= page.ids
                selected = {'listingPath': str(listing), 'methodIndex': 0}
                request('/api/assembly/native', selected, 403, use_csrf=False)
                request('/api/assembly/native', selected, 403, origin='https://untrusted.invalid')
                report = json.loads(request('/api/assembly/native', selected))
                analysis = report['analysis']
                assert analysis['bytesDecoded'] and len(analysis['instructions']) == 5 and len(analysis['blocks']) == 3, analysis
                assert analysis['instructions'][3]['localBranchTarget'] == 2
                assert all(not s['defectConfirmed'] and s['priority'] == 'P2' for s in analysis['investigationSites'])
                comparison = json.loads(request('/api/assembly/compare', {'baseline': selected, 'candidate': selected}))
                assert comparison['sameCodeBytes'] and comparison['instructionCountDelta'] == 0
                assert comparison['verdict'] == 'code_generation_comparison_only' and not comparison['sameReportedEnvironment']
                invalid = json.loads(request('/api/assembly/native', {'listingPath': str(missing)}))
                assert not invalid['analysis']['bytesDecoded'] and not invalid['analysis']['blocks']
                request('/api/assembly/native', {}, 400)
                request('/api/assembly/native', {**selected, 'localJitCaptureId': 'mixed'}, 400)
                request('/api/assembly/native', {**selected, 'methodIndex': 99}, 400)
                request('/api/assembly/native', {'listingPath': str(temp.parent / 'outside.txt')}, 400)
                request('/api/assembly/compare', {'baseline': None, 'candidate': selected}, 400)
                catalog = json.loads(request('/api/assembly/methods', {'assemblyPath': str(dll), 'query': 'Compute'}))
                method = next(m for m in catalog['methods'] if m['name'] == 'Compute')
                args = {'assemblyPath': str(dll), 'methodToken': method['metadataToken'], 'expectedAssemblySha256': catalog['assemblySha256']}
                il = json.loads(request('/api/assembly/il', args))
                assert il['instructions'] and il['assemblySha256'] == catalog['assemblySha256']
                request('/api/assembly/il', {**args, 'expectedAssemblySha256': '0' * 64}, 400)
                request('/api/assembly/il', {**args, 'expectedAssemblySha256': None}, 400)
                request('/api/assembly/il', {**args, 'methodToken': 1}, 400)
                (artifacts / 'assembly-http.json').write_text(json.dumps({'scope': 'controlled read-only fixture',
                    'native': report, 'comparison': comparison, 'managed': catalog, 'il': il}, indent=2), encoding='utf-8')
            finally:
                stop(web)
        with (temp / 'mcp.log').open('w', encoding='utf-8') as log:
            mcp = subprocess.Popen(['dotnet', str(ROOT / 'src/TenkaChan.Mcp/bin/Release/net10.0/TenkaChan.Mcp.dll')],
                                   cwd=ROOT, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=log, text=True, encoding='utf-8')
            messages = queue.Queue()
            def read_lines():
                for line in mcp.stdout:
                    messages.put(line)
            threading.Thread(target=read_lines, daemon=True).start()
            number = 0
            def call(method, params):
                nonlocal number
                number += 1
                mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': number, 'method': method, 'params': params}) + '\n')
                mcp.stdin.flush()
                while True:
                    message = json.loads(messages.get(timeout=90))
                    if message.get('id') == number:
                        assert 'error' not in message, message
                        return message['result']
            def tool(name, arguments):
                result = call('tools/call', {'name': name, 'arguments': arguments})
                assert not result.get('isError'), result
                return json.loads(next(x['text'] for x in result['content'] if x['type'] == 'text'))
            try:
                call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {}, 'clientInfo': {'name': 'assembly-ci', 'version': '1'}})
                mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n'); mcp.stdin.flush()
                available = call('tools/list', {})['tools']
                assert {'assembly_native_analyze', 'assembly_native_compare', 'assembly_methods', 'binary_source_inspect'} <= {x['name'] for x in available}
                report = tool('assembly_native_analyze', selected)
                assert report['Analysis']['BytesDecoded'] and len(report['Analysis']['Blocks']) == 3, report
                compared = tool('assembly_native_compare', {'baseline': selected, 'candidate': selected})
                assert compared['SameCodeBytes'] and compared['Verdict'] == 'code_generation_comparison_only'
                catalog = tool('assembly_methods', {'assemblyPath': str(dll), 'query': 'Compute'})
                assert catalog['Methods'] and len(catalog['AssemblySha256']) == 64
                (artifacts / 'assembly-mcp.json').write_text(json.dumps({'scope': 'controlled MCP fixture', 'report': report}, indent=2), encoding='utf-8')
            finally:
                stop(mcp)
    print('Read-only assembly HTTP and MCP checks passed; no target execution or MSBuild was enabled.')

if __name__ == '__main__':
    run()
