"""Run with: python tests/Fixtures/agent-probe.test.py. No .NET/server required."""
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from agent_probe_support import (ProbeError, default_paths, validate_descriptor,
                                 expected_tool_names, list_tools, validate_tools)

ROOT = Path(__file__).resolve().parents[2]


class ProbeTests(unittest.TestCase):
    def descriptor(self, **updates):
        return dict(schema=1, endpoint='http://localhost:5077', token='A'*64,
                    instanceId='b'*32, **updates)

    def validate(self, values):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'connection.json'
            path.write_text(json.dumps(values), encoding='utf-8-sig')
            return validate_descriptor(path)

    def test_descriptor_bom_and_dns_free_localhost(self):
        origin,token,instance=self.validate(self.descriptor())
        self.assertEqual(origin,'http://127.0.0.1:5077')
        self.assertEqual(len(token),64); self.assertEqual(len(instance),32)

    def test_bad_endpoints_never_receive_credentials(self):
        for endpoint in ['https://127.0.0.1:5077', 'http://127.0.0.1.evil.test:5077',
                         'http://user:pass@127.0.0.1:5077', 'http://127.0.0.1:5077/api',
                         'http://127.0.0.1:5077?token=secret', 'http://127.0.0.1:5077#x',
                         'http://127.0.0.1:80', 'http://127.0.0.1:99999', None, 7, ['url'],
                         'http://localhost:\n5077']:
            with self.subTest(endpoint=endpoint):
                d=self.descriptor();d['endpoint']=endpoint
                with self.assertRaises(ProbeError) as caught:self.validate(d)
                self.assertNotIn('A'*64,str(caught.exception))

    def test_bad_credentials_and_schema(self):
        for key,value in [('schema',True),('schema',2),('token',None),('token','short'),
                          ('token','X'*64),('instanceId','bad')]:
            with self.subTest(key=key,value=value):
                d=self.descriptor();d[key]=value
                with self.assertRaises(ProbeError): self.validate(d)

    def test_ipv6(self):
        d=self.descriptor();d['endpoint']='http://[::1]:5078/'
        self.assertEqual(self.validate(d)[0],'http://[::1]:5078')

    def test_invalid_nonobject_descriptor(self):
        for obj in [None, [], 'text', 7]:
            with self.subTest(obj=obj), self.assertRaises(ProbeError):self.validate(obj)

    def test_oversize_and_missing_descriptor(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'connection.json'
            with self.assertRaises(ProbeError):validate_descriptor(path)
            path.write_text('x'*16001)
            with self.assertRaises(ProbeError):validate_descriptor(path)

    def test_windows_launcher_path_is_derived_without_secret_in_config(self):
        with patch.dict(os.environ,{'LOCALAPPDATA':'C:/LocalAppData'},clear=True):
            path,adapter=default_paths(ROOT)
        self.assertEqual(path.name,'agent-connection.json')
        self.assertEqual(len(path.parent.name),16)
        self.assertEqual(adapter.name,'TenkaChan.Mcp.dll')

    def test_explicit_descriptor_takes_precedence(self):
        with patch.dict(os.environ,{'TENKA_AGENT_CONNECTION_FILE':'override.json'},clear=True):
            path,_=default_paths(ROOT)
        self.assertEqual(path,Path('override.json').resolve())

    def test_no_unknown_system_default_on_nonwindows(self):
        with patch.dict(os.environ,{},clear=True),self.assertRaises(ProbeError):default_paths(ROOT)

    def test_catalogue_follows_cursors_and_preserves_schemas(self):
        calls=[]
        def send(method,args):
            calls.append((method,args))
            return {'tools':[{'name':'b'}]} if args else {'tools':[{'name':'a'}],'nextCursor':'next'}
        self.assertEqual(set(list_tools(send)),{'a','b'})
        self.assertEqual(calls,[('tools/list',{}),('tools/list',{'cursor':'next'})])

    def test_duplicate_names_fail(self):
        with self.assertRaises(ProbeError):list_tools(lambda *_:{'tools':[{'name':'x'},{'name':'x'}]})

    def test_cursor_loops_fail(self):
        with self.assertRaises(ProbeError):list_tools(lambda *_:{'tools':[],'nextCursor':'same'})

    def test_catalogue_is_bounded(self):
        i=0
        def send(*_):
            nonlocal i
            i+=1;return {'tools':[],'nextCursor':str(i)}
        with self.assertRaises(ProbeError):list_tools(send)
        self.assertEqual(i,40)

    def test_malformed_catalogues_fail(self):
        for data in [None,{}, {'tools':None}, {'tools':[{}]}, {'tools':[],'nextCursor':False}]:
            with self.subTest(data=data), self.assertRaises(ProbeError):list_tools(lambda *_:data)

    def test_names_come_from_registered_classes_only(self):
        names=expected_tool_names(ROOT)
        self.assertIn('tenka_mark_review',names);self.assertIn('tenka_prepare_experiment',names)
        self.assertNotIn('autonomous_review_start',names)
        self.assertGreater(len(names),0)
        self.assertIn('tenka_approve_session',names)
        self.assertIn('tenka_measure_target',names)

    def current_schema(self):
        tools={name:{'inputSchema':{'properties':{}}} for name in expected_tool_names(ROOT)}
        for name,fields in {'tenka_prepare_experiment':['projectId','targetFramework','runtimeFramework'],
                            'tenka_review_queue':['snapshotId','cursor','trackableOnly'],
                            'tenka_mark_review':['expectedRevision','sourceHash','contextHash']}.items():
            tools[name]['inputSchema']['properties']={f:{} for f in fields}
        return tools

    def test_matching_names_and_schemas_pass(self):
        tools=self.current_schema();validate_tools(tools,set(tools))

    def test_same_count_old_binary_is_detected(self):
        tools=self.current_schema();del tools['tenka_prepare_experiment']['inputSchema']['properties']['runtimeFramework']
        with self.assertRaises(ProbeError):validate_tools(tools,set(tools))

    def test_extra_legacy_and_missing_tools_are_detected(self):
        tools=self.current_schema();expected=set(tools)
        del tools['tenka_note'];tools['legacy_tool']={}
        with self.assertRaises(ProbeError):validate_tools(tools,expected)


if __name__=='__main__':unittest.main(verbosity=2)
