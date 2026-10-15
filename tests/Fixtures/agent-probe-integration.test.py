"""Execute the probe over real local HTTP and stdio with a Python fake adapter.
This tests the probe/transport wiring, NOT Tenka's C# server or official MCP SDK.
"""
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from agent_probe_support import expected_tool_names

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipIf(os.name == 'nt', 'The fake dotnet executable uses a Unix shebang; real Windows checks use the BAT.')
class ProbeWireTests(unittest.TestCase):
    def run_probe(self, old_schema=False):
        token, instance, session = 'A'*64, uuid.uuid4().hex, uuid.uuid4().hex
        events, calls, cached = [], [], {}
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def respond(self, status, data):
                body=json.dumps(data).encode();self.send_response(status)
                self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)))
                self.end_headers();self.wfile.write(body)
            def do_GET(self):
                calls.append(('GET',self.path))
                if self.path=='/api/session':self.respond(200,{'csrfToken':'csrf'})
                elif self.path=='/api/agent/monitor':self.respond(200,{'instanceId':instance,'events':events})
                else:self.respond(404,{})
            def do_POST(self):
                n=int(self.headers.get('Content-Length','0'));data=json.loads(self.rfile.read(n) or b'{}')
                calls.append(('POST',self.path))
                if self.path=='/api/reviews/start':self.respond(410,{});return
                if (self.headers.get('Authorization')!='Bearer '+token
                    or 'Origin' in self.headers or 'Sec-Fetch-Site' in self.headers):
                    self.respond(403,{});return
                if self.path=='/agent/v1/begin':self.respond(200,{'id':session});return
                if self.path=='/agent/v1/end':self.respond(200,{'status':'finished'});return
                if self.path!='/agent/v1/call':self.respond(404,{});return
                request_id=data['requestId']
                if request_id in cached:self.respond(200,cached[request_id]);return
                name=data['tool'];call_id=uuid.uuid4().hex
                self.assert_readonly(name)
                events.append({'sessionId':session,'callId':call_id,'kind':'tool_result','tool':name})
                if name=='note':events.append({'sessionId':session,'kind':'agent_hypothesis','observedStatus':'agent_reported_unverified'})
                reply={'outcome':'returned','callId':call_id,'data':{}}
                cached[request_id]=reply;self.respond(200,reply)
            def assert_readonly(self, name):
                if name not in ('context','note'):
                    raise AssertionError('Probe attempted a target operation: '+name)
        server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        try:
            with tempfile.TemporaryDirectory() as folder:
                folder=Path(folder)
                origin='http://127.0.0.1:'+str(server.server_port)
                descriptor=folder/'connection.json'
                descriptor.write_text(json.dumps(dict(schema=1,endpoint=origin,token=token,instanceId=instance)))
                adapter=folder/'adapter.dll';adapter.write_bytes(b'fake, not a .NET binary')
                tools={name:{'name':name,'inputSchema':{'type':'object','properties':{}}} for name in expected_tool_names(ROOT)}
                for name,fields in {'tenka_prepare_experiment':['projectId','targetFramework','runtimeFramework'],
                                    'tenka_review_queue':['snapshotId','cursor','trackableOnly'],
                                    'tenka_mark_review':['expectedRevision','sourceHash','contextHash']}.items():
                    tools[name]['inputSchema']['properties']={f:{'type':'string'} for f in fields}
                if old_schema:del tools['tenka_prepare_experiment']['inputSchema']['properties']['runtimeFramework']
                (folder/'tools.json').write_text(json.dumps(list(tools.values())))
                fake=folder/'dotnet'
                fake.write_text('#!'+sys.executable+'\n'+FAKE_ADAPTER)
                fake.chmod(0o700)
                env=dict(os.environ,PATH=str(folder)+os.pathsep+os.environ.get('PATH',''))
                result=subprocess.run([sys.executable,str(ROOT/'tests/Fixtures/agent_protocol_smoke.py'),
                    '--connection-file',str(descriptor),'--adapter-dll',str(adapter)],env=env,
                    text=True,capture_output=True,timeout=20)
                self.assertNotIn(token,result.stdout+result.stderr)
                return result,calls,cached,events
        finally:server.shutdown();server.server_close();thread.join(timeout=5)

    def test_real_probe_accepts_paginated_matching_contract_and_records_no_target_action(self):
        result,calls,cached,events=self.run_probe()
        self.assertEqual(result.returncode,0,result.stderr)
        output=json.loads(result.stdout)
        self.assertEqual(output['mcpTools'],36)
        self.assertFalse(output['modelInference']);self.assertFalse(output['targetExecution'])
        # Context: one MCP call + one duplicate HTTP pair; note: one call.
        self.assertEqual(len(cached),3)
        self.assertTrue(any(e.get('kind')=='agent_hypothesis' for e in events))
        self.assertFalse(any('/api/explorer/action' in path for _,path in calls))

    def test_real_probe_refuses_old_schema_even_with_same_tool_count(self):
        result,calls,cached,events=self.run_probe(old_schema=True)
        self.assertNotEqual(result.returncode,0)
        self.assertIn('old/missing input schema',result.stderr)
        self.assertFalse(cached);self.assertFalse(events)


FAKE_ADAPTER = r'''
import json, sys, uuid
from pathlib import Path
from urllib.request import Request, build_opener, ProxyHandler
path=Path(sys.argv[sys.argv.index('--connection-file')+1]);config=json.loads(path.read_text())
tools=json.loads(path.with_name('tools.json').read_text());session=None
opener=build_opener(ProxyHandler({}))
def call(route,body):
    req=Request(config['endpoint']+route,json.dumps(body).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+config['token']})
    with opener.open(req,timeout=5) as response:return json.loads(response.read())
for line in sys.stdin:
    message=json.loads(line);method=message['method'];args=message.get('params',{})
    if method=='notifications/initialized':continue
    if method=='initialize':result={'protocolVersion':'2025-11-25','capabilities':{'tools':{}},'serverInfo':{'name':'fixture','version':'1'}}
    elif method=='tools/list':result={'tools':tools[18:]} if args.get('cursor') else {'tools':tools[:18],'nextCursor':'page2'}
    elif method=='tools/call':
        name=args['name'];arguments=args.get('arguments',{})
        if name=='tenka_connect':
            session=call('/agent/v1/begin',{})['id'];value={'sessionId':session,'instanceId':config['instanceId']}
        elif name=='tenka_finish':value=call('/agent/v1/end',{'sessionId':session})
        else:value=call('/agent/v1/call',{'sessionId':session,'requestId':uuid.uuid4().hex,'tool':name.removeprefix('tenka_'),'arguments':arguments})
        result={'content':[{'type':'text','text':json.dumps(value)}]}
    else:raise RuntimeError(method)
    print(json.dumps({'jsonrpc':'2.0','id':message['id'],'result':result}),flush=True)
'''

if __name__=='__main__':unittest.main(verbosity=2)
