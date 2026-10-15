"""Real local HTTP + official-SDK stdio MCP integration probe.
Requires a running updated Tenka Web server and built MCP adapter. No model inference,
MSBuild opening/building, target application, test or benchmark is requested.
"""
import argparse
import json
import queue
import subprocess
import threading
import uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlparse
from urllib.request import Request, build_opener, ProxyHandler, HTTPRedirectHandler

class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--connection-file', type=Path, required=True)
    parser.add_argument('--adapter-dll', type=Path, required=True)
    args=parser.parse_args()
    saved=json.loads(args.connection_file.read_text(encoding='utf-8-sig'))
    origin=saved['endpoint']; uri=urlparse(origin)
    if uri.scheme!='http' or uri.hostname not in ('127.0.0.1','::1','localhost') or uri.username or uri.password or uri.path not in ('','/') or uri.query or uri.fragment:
        parser.error('Connection descriptor must use a loopback HTTP origin.')
    opener=build_opener(ProxyHandler({}),NoRedirect())
    token=saved['token']
    def http(path, body=None, auth=False, extra=None, expected=200):
        headers={'Accept':'application/json'}
        if body is not None:headers['Content-Type']='application/json'
        if auth:headers['Authorization']='Bearer '+token
        headers.update(extra or {})
        request=Request(origin.rstrip('/')+path, data=None if body is None else json.dumps(body).encode(),headers=headers)
        try:
            with opener.open(request,timeout=30) as response:status,data=response.status,response.read()
        except HTTPError as e:status,data=e.code,e.read()
        assert status==expected,(path,status,'Unexpected HTTP status; response is not printed to avoid exposing source data.')
        return json.loads(data) if data else {}
    for auth,extra in [(False,{}),(True,{'Origin':origin}),(True,{'Sec-Fetch-Site':'same-origin'})]:
        http('/agent/v1/begin',{},auth,extra,403)
    monitor=http('/api/agent/monitor')
    assert token not in json.dumps(monitor)
    csrf=http('/api/session')['csrfToken']
    http('/api/reviews/start',{},extra={'X-Tenka-CSRF':csrf},expected=410)
    process=subprocess.Popen(['dotnet',str(args.adapter_dll.resolve()),'--connection-file',str(args.connection_file.resolve()),'--client-name','integration-probe'],
        stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',bufsize=1)
    output=queue.Queue(); errors=[]
    def drain_stdout():
        for line in process.stdout:
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
        for _ in range(100):
            answer=output.get(timeout=30)
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
        listed=send('tools/list');names={t['name'] for t in listed['tools']}
        assert len(names)==31 and {'tenka_context','tenka_prepare_experiment','tenka_read_result'}<=names
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

if __name__=='__main__':main()
