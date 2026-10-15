"""Real local HTTP + official-SDK stdio MCP integration probe.
Requires a running updated Tenka Web server and built MCP adapter. No model inference,
MSBuild opening/building, target application, test or benchmark is requested.
"""
import argparse
import json
import queue
import subprocess
import threading
import time
import uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, build_opener, ProxyHandler, HTTPRedirectHandler
from agent_probe_support import (ProbeError, default_paths, validate_descriptor,
                                 expected_tool_names, list_tools, validate_tools)

class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--connection-file', type=Path)
    parser.add_argument('--adapter-dll', type=Path)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[2]
    if args.connection_file is None:
        args.connection_file, default_adapter=default_paths(root)
    else:
        default_adapter=root/'src/TenkaChan.Mcp/bin/Release/net10.0/TenkaChan.Mcp.dll'
    args.adapter_dll=args.adapter_dll or default_adapter
    if not args.adapter_dll.is_file():
        raise ProbeError('MCP adapter DLL is missing. Run setup.bat and start.bat first.')
    origin,token,instance=validate_descriptor(args.connection_file)
    expected=expected_tool_names(root)
    opener=build_opener(ProxyHandler({}),NoRedirect())
    def http(path, body=None, auth=False, extra=None, expected=200):
        headers={'Accept':'application/json'}
        if body is not None:headers['Content-Type']='application/json'
        if auth:headers['Authorization']='Bearer '+token
        headers.update(extra or {})
        request=Request(origin.rstrip('/')+path, data=None if body is None else json.dumps(body).encode(),headers=headers)
        try:
            with opener.open(request,timeout=180) as response:status,data=response.status,response.read(2_000_001)
        except HTTPError as e:status,data=e.code,e.read(2_000_001)
        if len(data)>2_000_000: raise ProbeError("HTTP result exceeds the probe limit.")
        assert status==expected,(path,status,'Unexpected HTTP status; response is not printed to avoid exposing source data.')
        return json.loads(data) if data else {}
    for auth,extra in [(False,{}),(True,{'Origin':origin}),(True,{'Sec-Fetch-Site':'same-origin'})]:
        http('/agent/v1/begin',{},auth,extra,403)
    monitor=http('/api/agent/monitor')
    assert token not in json.dumps(monitor)
    if monitor.get('instanceId')!=instance: raise ProbeError('Server instance changed. Reconnect after restarting Tenka.')
    csrf=http('/api/session')['csrfToken']
    http('/api/reviews/start',{},extra={'X-Tenka-CSRF':csrf},expected=410)
    process=subprocess.Popen(['dotnet',str(args.adapter_dll.resolve()),'--connection-file',str(args.connection_file.resolve()),'--client-name','integration-probe'],
        stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',bufsize=1)
    output=queue.Queue(); errors=[]
    def drain_stdout():
        for line in iter(lambda: process.stdout.readline(1_000_001), ''):
            if len(line)>1_000_000:
                output.put({'transport_error':'MCP message exceeds the probe limit'});return
            try:output.put(json.loads(line))
            except json.JSONDecodeError:output.put({'transport_error':'Non-JSON data on MCP stdout'})
    def drain_stderr():
        for line in process.stderr:errors.append(line[:1000]);del errors[:-30]
    threading.Thread(target=drain_stdout,daemon=True).start()
    threading.Thread(target=drain_stderr,daemon=True).start()
    counter=0
    def send(method,params=None,notification=False):
        nonlocal counter
        counter+=1; payload={'jsonrpc':'2.0','method':method,'params':params or {}}
        if not notification:payload['id']=counter
        process.stdin.write(json.dumps(payload)+'\n');process.stdin.flush()
        if notification:return None
        deadline=time.monotonic()+180
        for _ in range(100):
            remaining=deadline-time.monotonic()
            if remaining<=0: raise ProbeError("MCP reply timed out; the call was not retried.")
            answer=output.get(timeout=remaining)
            if not isinstance(answer,dict): raise ProbeError("MCP returned a non-object message.")
            if answer.get('transport_error'):raise AssertionError(answer['transport_error'])
            if answer.get('id')==counter:
                assert 'error' not in answer,(method,answer.get('error'))
                return answer['result']
        raise AssertionError('Too many unrelated MCP messages')
    def tool(name, arguments=None):
        result=send('tools/call',{'name':name,'arguments':arguments or {}})
        assert not result.get('isError'),name+' returned a tool error'
        return json.loads(next(c['text'] for c in result['content'] if c.get('type')=='text'))
    try:
        init=send('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'tenka-integration-probe','version':'1'}})
        assert init.get('protocolVersion')
        send('notifications/initialized',notification=True)
        listed=list_tools(send);validate_tools(listed,expected);names=set(listed)
        session=tool('tenka_connect',{'goal':'Integration probe only; no target execution.'})['sessionId']
        context=tool('tenka_context');assert context['outcome']=='returned'
        request={'sessionId':session,'requestId':uuid.uuid4().hex,'tool':'context','arguments':{}}
        one=http('/agent/v1/call',request,True);two=http('/agent/v1/call',request,True)
        assert one['callId']==two['callId']
        tool('tenka_note',{'kind':'hypothesis','message':'Test note only, not a detected defect.','evidenceCallIds':[context['callId']]})
        monitor=http('/api/agent/monitor')
        events=[e for e in monitor['events'] if e['sessionId']==session]
        assert any(e['kind']=='tool_result' and e['tool']=='context' for e in events)
        assert any(e['kind']=='agent_hypothesis' and e['observedStatus']=='agent_reported_unverified' for e in events)
        assert token not in json.dumps(monitor)
        tool('tenka_finish')
        print(json.dumps({'mcpTools':len(names),'transport':'official SDK stdio -> authenticated shared Web process',
            'duplicateRequestExecutedOnce':True,'modelInference':False,'targetExecution':False}))
    finally:
        if process.stdin:process.stdin.close()
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:process.wait(timeout=5)
            except subprocess.TimeoutExpired:process.kill();process.wait()

if __name__=='__main__':
    try: main()
    except (ProbeError, OSError, AssertionError, queue.Empty, ValueError, KeyError) as error:
        # Do not print arbitrary response bodies, tracebacks or credentials on failure.
        import sys
        print(str(error) if isinstance(error, ProbeError) else
              'Protocol check failed ('+type(error).__name__+'). No target was executed; inspect local logs.', file=sys.stderr)
        sys.exit(1)
