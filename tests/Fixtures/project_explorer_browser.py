"""Real Chromium + actual project UI files, with mocked APIs. No .NET or target execution.
Run: python tests/Fixtures/project_explorer_browser.py [--screenshots /mnt/data]
"""
import argparse
import json
import mimetypes
import re
from types import SimpleNamespace
from pathlib import Path
from urllib.parse import urlparse
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2] / 'src/TenkaChan.Web/wwwroot'
PROJECT = r'C:\Projects\GameLab\Tenka.Sample.GameLab.csproj'

class Fixture:
    def __init__(self, loaded=True):
        self.loaded=loaded; self.snapshot='s1'; self.calls=[]; self.artifacts={}
    def context(self):
        return dict(snapshotId=self.snapshot, path=PROJECT, projects=[dict(id='p1',name='Tenka.Sample.GameLab',path=PROJECT,documents=2)],
            assemblies=[dict(id='a1',projectId='p1',project='GameLab',assemblyPath=r'C:\Projects\GameLab\bin\Release\net8.0\GameLab.dll',targetFramework='net8.0',configuration='Release',runnable=True)],
            companions=[dict(id='test1',projectId='p1',name='GameLab.Tests',kind='test',projectPath='not-presented-to-actions')],
            capabilities=dict(build=True,localExecution=True,trace=True),scanned=True,summary=dict(totalFindings=4),
            history=[dict(id=a['id'],kind=a['kind'],label=a['label'],createdUtc=a['createdUtc']) for a in self.artifacts.values()])
    def method(self):
        return dict(id='m1',typeId='t1',projectId='p1',signature='InventorySearch.CountOwnedSlow(int[], int[])',file=r'C:\Projects\GameLab\GameWorkloads.cs',line=21,kind='Ordinary',metadataType='InventorySearch',metadataName='CountOwnedSlow',jitFilter='InventorySearch:CountOwnedSlow')
    def detail(self):
        return dict(type=dict(id='t1',projectId='p1',project='GameLab',name='InventorySearch',kind='class',methodCount=2,declarationCount=1),baseType='object',interfaces=[],members=[],methods=[self.method()],
            sources=[dict(file=r'C:\Projects\GameLab\GameWorkloads.cs',line=19,text='public static class InventorySearch\n{\n    // ベースラインと改善候補を比較する教材\n}',truncated=False)],notes=['読み込み時のソースです。静的候補は性能低下の証明ではありません。'])
    def artifact(self, kind, data):
        a=dict(id='r-'+kind,snapshotId=self.snapshot,kind=kind,label='InventorySearch / '+kind,createdUtc='2026-10-15T02:00:00+09:00',data=data)
        self.artifacts[a['id']]=a
        return a
    def handle(self, route):
        request=route.request; path=urlparse(request.url).path
        if not path.startswith('/api/'):
            file=ROOT/path.lstrip('/')
            if file.is_file(): route.fulfill(body=file.read_bytes(),content_type=mimetypes.guess_type(str(file))[0] or 'text/plain'); return
            route.fulfill(status=404,body='not found'); return
        body=request.post_data_json if request.method=='POST' else None
        self.calls.append((path,request.method,body))
        if path=='/api/session': data=dict(csrfToken='test-csrf',listening='loopback')
        elif path=='/api/overview': data=dict(loaded=self.loaded)
        elif path=='/api/samples/game-lab': data=dict(available=True,projectPath=PROJECT)
        elif path=='/api/workspace/open': self.loaded=True; data=dict(snapshotId=self.snapshot)
        elif path=='/api/explorer/context': data=self.context()
        elif path=='/api/explorer/types': data=dict(snapshotId=self.snapshot,total=1,offset=0,items=[self.detail()['type']],indexLimited=False)
        elif path=='/api/explorer/action':
            assert request.headers.get('x-tenka-csrf')=='test-csrf'
            assert body['snapshotId']==self.snapshot
            assert not set(body)&{'projectPath','assemblyPath','outputDirectory','reportPath','sourceFilePath','command'}
            action=body['action']
            if action=='type': data=dict(detail=self.detail(),findings=dict(scanned=True,limited=False,findings=[dict(ruleId='PERF1001',message='ループ内でのToList呼び出し。実測で確認してください。')]))
            elif action=='method': data=dict(method=self.method(),inspection=dict(source='public static int CountOwnedSlow(int[] items, int[] queries)\n{\n    int found = 0;\n    foreach (int id in queries)\n        if (items.ToList().Contains(id)) found++;\n    return found;\n}',callees=['Enumerable.ToList<int>()','List<int>.Contains(int)'],callers=['Program.Main(string[])'],reads=['items','queries'],writes=['found'],diagnostics=[],analysisNote='実行頻度や所要時間は静的解析だけでは分かりません。',blocks=[]))
            elif action.endswith('references'): data=dict(items=[dict(project='GameLab',file='Program.cs',line=20,preview='InventorySearch.CountOwnedSlow(items, queries);',methodId='m1')],limited=False,note='静的な参照です。')
            elif action=='test-plan': data=dict(signature='CountOwnedSlow',inputs=[dict(parameter='items',type='int[]',cSharpExpressions=['null','Array.Empty<int>()'],reason='境界入力候補')],notes=[],oracleStatus='unresolved')
            elif action=='binary-methods': data=dict(assemblySha256='a'*64,methods=[dict(metadataToken=100663297,signature='int InventorySearch::CountOwnedSlow(int[],int[])')],truncated=False,note='名称に基づく候補です。')
            elif action=='il':
                assert body['metadataToken']==100663297 and body['assemblySha256']=='a'*64
                data=dict(signature='InventorySearch::CountOwnedSlow',ilSizeBytes=40,pdbStatus='matched_fixture',instructions=[dict(offset=0,opCode='ldarg.0',operand='',sourceMappingStatus='fixture')],limitations=['テスト用の模擬API応答です。実測ではありません。'])
            elif action=='jit':
                assert body['approved'] is True
                data=self.artifact('jit',dict(status='jit_completed',nativeDisassembly=dict(methods=[dict(jitSignature='InventorySearch:CountOwnedSlow',codeSizeBytes=5,instructions=[{}])]),limitations=['模擬応答'],stderr=''))
            elif action=='trace':
                assert body['approved'] is True
                data=self.artifact('trace',dict(status='trace_completed',profiles=[dict(index=0,name='Main thread',observations=12),dict(index=1,name='Worker',observations=8)],netTrace=dict(name='workload.nettrace'),output='模擬API応答',limitations=[]))
            elif action=='native': data=dict(analysis=dict(status='decoded_fixture',architecture='x64',codeSizeBytes=5,instructions=[dict(offset=0,bytes='C3',assembly='ret',flowControl='Return')],investigationSites=[]))
            elif action=='runtime-costs': data=dict(summary=dict(totalEvents=12,measurementKind='fixture_not_actual_measurement'),groups=[])
            elif action=='import-thread': assert body['profileIndex']==1; data=dict(linkedFrames=2,totalWeight=20,note='fixture')
            elif action=='compare-native': data=dict(sameSelectionSignature=True,codeSizeDelta=0,instructionCountDelta=0)
            elif action=='result': data=self.artifacts[body['artifactId']]
            elif action=='run-companion':
                assert body['approved'] is True
                self.snapshot='s2';self.artifacts={};data=self.artifact('test',dict(status='tests_passed',tests=dict(total=2,passed=2,failed=0),benchmarks=[],note='模擬API応答',output='fixture',stderr=''))
            else: raise AssertionError(action)
        else: raise AssertionError(path)
        route.fulfill(json=data)

def mount(page, fixture):
    # Browser policy disallows local HTTP navigation in the authoring environment.
    # Exercise the real DOM/scripts with an isolated fetch binding instead. This is
    # intentionally not claimed as ASP.NET, real network navigation or dialog coverage.
    def fetch_binding(source, url, init):
        result = {}
        request = SimpleNamespace(url=url, method=init.get('method', 'GET'),
            post_data_json=json.loads(init['body']) if init.get('body') else None,
            headers={k.lower(): v for k,v in init.get('headers',{}).items()})
        def fulfill(**kwargs):
            result.update(dict(status=kwargs.get('status',200), data=kwargs.get('json')))
        fixture.handle(SimpleNamespace(request=request, fulfill=fulfill))
        return result
    page.expose_binding('mockProjectFetch', fetch_binding)
    html = ROOT.joinpath('project.html').read_text()
    html = re.sub(r'<script\b[^>]*>.*?</script>', '', html, flags=re.S)
    html = re.sub(r'<link\b[^>]*>', '', html)
    page.set_content(html)
    for name in ['common-shell.css', 'path-picker.css', 'project.css']:
        page.add_style_tag(content=ROOT.joinpath(name).read_text())
    page.evaluate("""window.fetch = async (url, init = {}) => {
      const result = await window.mockProjectFetch(String(url), {
        method: init.method || 'GET', headers: init.headers || {}, body: init.body || null
      });
      return {ok: result.status >= 200 && result.status < 300, status: result.status,
        json: async () => result.data};
    };""")
    page.add_script_tag(content=ROOT.joinpath('common-shell.js').read_text())
    page.evaluate("TenkaShell.install(document, {pathname: '/project.html', hash: ''})")
    for name in ['path-picker.js','project-client.js','project.js']:
        page.add_script_tag(content=ROOT.joinpath(name).read_text())
    page.evaluate("document.dispatchEvent(new Event('DOMContentLoaded'))")

def main():
    p=argparse.ArgumentParser();p.add_argument('--screenshots',type=Path);args=p.parse_args()
    assertions=0
    with sync_playwright() as play:
        browser=play.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        for width in [1440,768,390,320]:
            fixture=Fixture();page=browser.new_page(viewport=dict(width=width,height=1050))
            errors=[];page.on('pageerror',lambda e: errors.append(str(e)))
            mount(page, fixture)
            page.wait_for_function("document.querySelector('#class-list .choice') && !document.querySelector('#class-list .choice').disabled")
            assert not errors,errors
            page.locator('#class-list .choice').first.click()
            page.wait_for_selector('#method-list .choice')
            page.locator('#method-list .choice').first.click()
            page.wait_for_function("!document.querySelector('#method-section').hidden && !document.querySelector('#inspect-il').disabled")
            assert page.locator('#method-source').inner_text().startswith('public static int')
            assert page.locator('.nav-item[data-page="project"]').get_attribute('aria-current')=='page'
            assert page.locator('input[data-path-kind]').count()==1
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth + 1')
            assertions+=5
            if width==1440 and args.screenshots:
                args.screenshots.mkdir(parents=True,exist_ok=True)
                page.evaluate('scrollTo(0,0)')
                page.screenshot(path=str(args.screenshots/'tenka-project-explorer.png'),full_page=True)
            page.locator('#inspect-il').click()
            page.wait_for_function("document.querySelector('#result-body').textContent.includes('ldarg.0') && !document.querySelector('#inspect-il').disabled")
            assert [c[2]['action'] for c in fixture.calls if c[0]=='/api/explorer/action'][-2:]==['binary-methods','il'];assertions+=1
            if width==1440:
                page.once('dialog',lambda d:d.dismiss());before=len(fixture.calls)
                page.locator('#capture-jit').click();page.wait_for_timeout(80)
                assert not any(c[2] and c[2].get('action')=='jit' for c in fixture.calls[before:]);assertions+=1
                page.on('dialog',lambda d:d.accept())
                page.locator('#capture-jit').click();page.wait_for_function("!document.querySelector('#native-actions').hidden && !document.querySelector('#decode-native').disabled")
                page.locator('#decode-native').click();page.wait_for_function("document.querySelector('#result-body').textContent.includes('decoded_fixture') && !document.querySelector('#decode-native').disabled")
                assert page.locator('#native-actions').is_visible();assertions+=1
                page.locator('#pin-native').click();assert page.locator('#compare-native').is_enabled();assertions+=1
                page.locator('#capture-trace').click();page.wait_for_function("!document.querySelector('#trace-actions').hidden && !document.querySelector('#runtime-costs').disabled")
                assert page.locator('#import-thread').is_disabled();assertions+=1
                page.locator('#trace-thread').select_option('1');page.locator('#import-thread').click()
                page.wait_for_function("document.querySelector('#result-body').textContent.includes('反映しました') && !document.querySelector('#runtime-costs').disabled")
                assert any(c[2] and c[2].get('action')=='import-thread' and c[2].get('profileIndex')==1 for c in fixture.calls);assertions+=1
                page.locator('#runtime-costs').click();page.wait_for_function("document.querySelector('#result-body').textContent.includes('イベント') && !document.querySelector('#runtime-costs').disabled")
                page.locator('#run-companion').click();page.wait_for_function("document.querySelector('#result-title').textContent.includes('テスト成功') && !document.querySelector('#refresh-project').disabled")
                assert page.locator('#method-section').is_hidden();assertions+=1
            assert not errors,errors
            page.close()
        # The sample button only selects; opening is a separate explicit action.
        fixture=Fixture(loaded=False);page=browser.new_page();mount(page,fixture)
        page.wait_for_function("!document.querySelector('#use-sample').disabled")
        page.locator('#use-sample').click();page.wait_for_function("document.querySelector('#project-path').value.includes('GameLab') && !document.querySelector('#use-sample').disabled")
        assert not any(c[0]=='/api/workspace/open' for c in fixture.calls);assertions+=1
        page.locator('#project-open-button').click();page.wait_for_selector('#class-list .choice')
        assert any(c[0]=='/api/workspace/open' for c in fixture.calls);assertions+=1
        browser.close()
    print(json.dumps(dict(browser='Chromium',widths=[1440,768,390,320],assertions=assertions,api='mocked via binding; isolated DOM, not HTTP navigation',dotnet_executed=False),ensure_ascii=False))

if __name__=='__main__':main()
