"""Tests OF the probe using synthetic messages/a fake child, NOT tests of the C# SDK."""
import json
import sys
import tempfile
import unittest
from pathlib import Path
from agent_binding_probe import ProbeError, RpcProcess, error_detail, probe


def error(code='invalid_arguments', **extra):
    detail = dict(code=code, message='message', sessionId=None, requestId=None, callId=None,
                  httpStatus=None, automaticRetry=False, nextAction='action', **extra)
    return dict(isError=True, content=[dict(type='text', text=json.dumps(detail))], structuredContent=detail)


class ProbeTests(unittest.TestCase):
    def test_direct_structured_error_is_accepted(self):
        self.assertEqual('invalid_arguments', error_detail(error())['code'])

    def test_generic_sdk_prefix_is_rejected(self):
        result = error(); result['content'][0]['text'] = 'An error occurred invoking tool. ' + result['content'][0]['text']
        with self.assertRaises(ProbeError): error_detail(result)

    def test_mismatched_structured_error_is_rejected(self):
        result = error(); result['structuredContent'] = dict(code='something_else')
        with self.assertRaises(ProbeError): error_detail(result)

    def test_success_is_not_counted_as_expected_failure(self):
        result = error(); result['isError'] = False
        with self.assertRaises(ProbeError): error_detail(result)

    def test_missing_contract_fields_are_rejected(self):
        result = error(); del result['structuredContent']['callId']; result['content'][0]['text'] = json.dumps(result['structuredContent'])
        with self.assertRaises(ProbeError): error_detail(result)

    def test_retry_not_explicitly_disabled_is_rejected(self):
        result = error(); result['structuredContent']['automaticRetry'] = True; result['content'][0]['text'] = json.dumps(result['structuredContent'])
        with self.assertRaises(ProbeError): error_detail(result)

    def test_complete_probe_against_fake_stdio_child(self):
        script = r'''
import json,sys
for line in sys.stdin:
 q=json.loads(line); method=q['method']; p=q.get('params',{})
 if 'id' not in q: continue
 if method=='initialize': r={'protocolVersion':'2025-11-25'}
 elif method=='tools/list':
  r={'tools':[{'name':n,'inputSchema':{'properties':{'snapshotId':{},'projectId':{}},'required':[]}} for n in ['tenka_review_queue','tenka_build','tenka_prepare_experiment']]}
 else:
  name=p['name']; a=p['arguments']; missing=[]; invalid=[]
  if name=='tenka_inspect_method':missing=['methodId']
  elif name=='tenka_open_project':missing=['path']
  elif 'limit' in a:invalid=[{'name':'limit','expected':'32-bit integer'}]
  elif 'approved' in a:invalid=[{'name':'approved','expected':'boolean'}]
  elif name=='tenka_note':invalid=[{'name':'evidenceCallIds','expected':'array of strings'}]
  d=dict(code='invalid_arguments' if missing or invalid else 'connection_file_missing',message='test',sessionId=None,requestId=None,callId=None,httpStatus=None,automaticRetry=False,nextAction='test',missingArguments=missing,invalidArguments=invalid,operationStarted=False)
  r={'isError':True,'structuredContent':d,'content':[{'type':'text','text':json.dumps(d)}]}
 print(json.dumps({'jsonrpc':'2.0','id':q['id'],'result':r}),flush=True)
'''
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/'fake_peer.py'; path.write_text(script)
            rpc=RpcProcess([sys.executable,str(path)],timeout=3)
            try:
                result=probe(rpc)
                self.assertEqual(9,result['passedCases'])
            finally: rpc.close()
            self.assertIsNotNone(rpc.process.returncode)

    def test_non_json_child_output_fails_without_hanging(self):
        rpc=RpcProcess([sys.executable,'-c',"import sys;sys.stdin.readline();print('not-json',flush=True)"],timeout=2)
        try:
            with self.assertRaises(ProbeError): rpc.call('initialize')
        finally: rpc.close()


if __name__=='__main__':unittest.main(verbosity=2)
