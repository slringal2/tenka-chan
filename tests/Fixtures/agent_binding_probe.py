"""Exercise real SDK binding/error transport with a deliberately missing descriptor.
No Web server, target project, build, generated experiment, or LLM is contacted.
Build the adapter separately before using --adapter-dll. Standard library only.
"""
from __future__ import annotations
import argparse
import json
import queue
import subprocess
import tempfile
import threading
import time
from pathlib import Path


class ProbeError(RuntimeError):
    """Only fixed diagnostic text is safe to print."""


class RpcProcess:
    def __init__(self, command: list[str], timeout: float = 20.0):
        self.timeout = timeout
        self.counter = 0
        self.output: queue.Queue = queue.Queue(maxsize=32)
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, text=True, encoding='utf-8', bufsize=1)
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self):
        try:
            while True:
                line = self.process.stdout.readline(1_000_001)
                if not line:
                    self.output.put(ProbeError('Adapter exited before returning a reply.'), timeout=1)
                    return
                if len(line) > 1_000_000:
                    raise ProbeError('Adapter response exceeds the probe limit.')
                try:
                    value = json.loads(line)
                except ValueError:
                    raise ProbeError('Non-JSON data was emitted on MCP stdout.') from None
                self.output.put(value, timeout=1)
        except (OSError, UnicodeError, queue.Full, ProbeError):
            try:
                self.output.put_nowait(ProbeError('Adapter output could not be read safely.'))
            except queue.Full:
                pass

    def call(self, method: str, params: dict | None = None, notification: bool = False):
        self.counter += 1
        request = {'jsonrpc': '2.0', 'method': method, 'params': params or {}}
        if not notification:
            request['id'] = self.counter
        try:
            self.process.stdin.write(json.dumps(request) + '\n')
            self.process.stdin.flush()
        except (BrokenPipeError, OSError):
            raise ProbeError('Adapter input closed unexpectedly.') from None
        if notification:
            return None
        deadline = time.monotonic() + self.timeout
        for _ in range(100):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise ProbeError('MCP reply timed out; no retry was performed.')
            try:
                response = self.output.get(timeout=remaining)
            except queue.Empty:
                raise ProbeError('MCP reply timed out; no retry was performed.') from None
            if isinstance(response, ProbeError):
                raise response
            if not isinstance(response, dict):
                raise ProbeError('MCP response is not an object.')
            if response.get('id') == self.counter:
                if 'error' in response or 'result' not in response:
                    raise ProbeError('Unexpected protocol error or missing result.')
                return response['result']
        raise ProbeError('Too many unrelated MCP messages.')

    def close(self):
        if self.process.stdin:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.process.terminate()  # Only the child created by this probe.
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        self.reader.join(timeout=1)
        if self.process.stdout:
            self.process.stdout.close()


def error_detail(result: dict) -> dict:
    if not isinstance(result, dict) or result.get('isError') is not True:
        raise ProbeError('Expected an MCP tool error, not success.')
    content = result.get('content')
    if not isinstance(content, list) or len(content) != 1 or content[0].get('type') != 'text':
        raise ProbeError('Expected one JSON text error block.')
    try:
        detail = json.loads(content[0]['text'])
    except (ValueError, TypeError, KeyError):
        raise ProbeError('Error text is not directly parseable JSON; SDK prefix may remain.') from None
    if not isinstance(detail, dict) or detail != result.get('structuredContent'):
        raise ProbeError('Text and structuredContent errors do not match.')
    if detail.get('automaticRetry') is not False:
        raise ProbeError('Error did not explicitly disable automatic retries.')
    if not all(k in detail for k in ('code', 'message', 'nextAction', 'callId', 'requestId', 'sessionId', 'httpStatus')):
        raise ProbeError('Error is missing its stable diagnostic fields.')
    return detail


def probe(rpc: RpcProcess) -> dict:
    initialized = rpc.call('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                         'clientInfo': {'name': 'tenka-binding-probe', 'version': '1'}})
    if not initialized.get('protocolVersion'):
        raise ProbeError('MCP protocol negotiation failed.')
    rpc.call('notifications/initialized', notification=True)
    tools, cursor, seen = {}, None, set()
    for _ in range(40):
        page = rpc.call('tools/list', {} if cursor is None else {'cursor': cursor})
        if not isinstance(page, dict) or not isinstance(page.get('tools'), list):
            raise ProbeError('Invalid tool catalogue.')
        for tool in page['tools']:
            name = tool.get('name')
            if not isinstance(name, str) or name in tools or len(tools) >= 400:
                raise ProbeError('Duplicate or excessive tool catalogue.')
            tools[name] = tool
        cursor = page.get('nextCursor')
        if cursor is None:
            break
        if not isinstance(cursor, str) or not cursor or len(cursor) > 4096 or cursor in seen:
            raise ProbeError('Invalid tool catalogue continuation.')
        seen.add(cursor)
    else:
        raise ProbeError('Tool catalogue pagination exceeded the limit.')
    for name in ('tenka_review_queue', 'tenka_build', 'tenka_prepare_experiment'):
        schema = tools.get(name, {}).get('inputSchema', {})
        properties = schema.get('properties', {})
        required = schema.get('required', [])
        if 'snapshotId' not in properties or 'snapshotId' in required:
            raise ProbeError('Adapter still requires snapshotId; rebuild it.')
        if name != 'tenka_review_queue' and ('projectId' not in properties or 'projectId' in required):
            raise ProbeError('Adapter still requires projectId; rebuild it.')
    cases = [
        ('tenka_inspect_method', {}, 'invalid_arguments', 'missingArguments', 'methodId'),
        ('tenka_open_project', {}, 'invalid_arguments', 'missingArguments', 'path'),
        ('tenka_review_queue', {'limit': '50'}, 'invalid_arguments', 'invalidArguments', 'limit'),
        ('tenka_review_queue', {'limit': 2147483648}, 'invalid_arguments', 'invalidArguments', 'limit'),
        ('tenka_build', {'approved': 'true'}, 'invalid_arguments', 'invalidArguments', 'approved'),
        ('tenka_note', {'kind': 'progress', 'message': 'probe', 'evidenceCallIds': [None]},
         'invalid_arguments', 'invalidArguments', 'evidenceCallIds'),
        # The missing descriptor proves the wrapper, rather than SDK binding, handled these calls.
        ('tenka_review_queue', {}, 'connection_file_missing', None, None),
        ('tenka_context', {}, 'connection_file_missing', None, None),
        ('tenka_sessions_list', {}, 'connection_file_missing', None, None),
    ]
    for name, arguments, expected, field, parameter in cases:
        detail = error_detail(rpc.call('tools/call', {'name': name, 'arguments': arguments}))
        if detail['code'] != expected:
            raise ProbeError('A tool returned the wrong diagnostic code.')
        if field:
            entries = detail.get(field, [])
            names = entries if field == 'missingArguments' else [entry.get('name') for entry in entries]
            if parameter not in names or detail.get('operationStarted') is not False:
                raise ProbeError('Pre-binding error did not identify the argument or reject execution.')
    return {'passedCases': len(cases), 'registeredTools': len(tools),
            'descriptor': 'deliberately absent', 'webContacted': False, 'targetExecuted': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adapter-dll', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    if not args.adapter_dll.is_file():
        raise ProbeError('Build the adapter first; its DLL is missing.')
    with tempfile.TemporaryDirectory(prefix='tenka-binding-') as folder:
        rpc = RpcProcess([args.dotnet, str(args.adapter_dll.resolve()), '--connection-file',
                          str(Path(folder) / 'deliberately-missing.json')])
        try:
            print(json.dumps(probe(rpc)))
        finally:
            rpc.close()


if __name__ == '__main__':
    try:
        main()
    except (ProbeError, OSError, ValueError, TypeError, KeyError, AttributeError):
        import sys
        print('Binding probe failed. Check the build and error contract; raw responses were not printed.', file=sys.stderr)
        sys.exit(1)
