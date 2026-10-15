"""Chromium regression on real shared/project DOM and scripts, with fake HTTP responses.
No .NET, actual builds, model inference or target execution. Legacy styles.css/app.js are omitted.
"""
import json
import re
import sys
from pathlib import Path
from types import SimpleNamespace
from urllib.parse import urlparse, parse_qs
from playwright.sync_api import sync_playwright
from project_explorer_browser import Fixture, ROOT

class Multi(Fixture):
    def __init__(self, loaded=True):
        super().__init__(loaded); self.selection='t2'
    def context(self):
        c=super().context()
        c['path']=r'C:\Game\Game.slnx'
        c['projects']=[dict(id='p1',name='Game.Client',path=r'C:\Game\Client\Client.csproj',documents=2,projectReferenceIds=['p2']),
            dict(id='p2',name='Shared',path=r'C:\Game\Engine\Shared.csproj',documents=3,projectReferenceIds=[]),
            dict(id='p3',name='Shared',path=r'C:\Game\Tools\Shared.csproj',documents=1,projectReferenceIds=[])]
        c['assemblies']=[dict(id='a1',projectId='p1',project='Game.Client',assemblyPath='Client.dll',targetFramework='net8.0',configuration='Release',runnable=True),
            dict(id='a2',projectId='p2',project='Shared',assemblyPath='Engine.dll',targetFramework='net8.0',configuration='Release',runnable=False),
            dict(id='a3',projectId='p3',project='Shared',assemblyPath='Tools.dll',targetFramework='net8.0',configuration='Release',runnable=False)]
        return c
    def method(self):
        n=self.selection[-1]
        m=super().method();m.update(id='m'+n,typeId='t'+n,projectId='p'+n)
        if n=='1':m.update(signature='Game.Client.Bootstrap.Run()',metadataType='Game.Client.Bootstrap',metadataName='Run')
        return m
    def detail(self):
        d=super().detail();n=self.selection[-1]
        d['type'].update(id='t'+n,projectId='p'+n,project='Game.Client' if n=='1' else 'Shared')
        if n=='1':d['type']['name']='Game.Client.Bootstrap'
        return d
    def handle(self,route):
        req=route.request;path=urlparse(req.url).path
        if path=='/api/explorer/types':
            args=parse_qs(urlparse(req.url).query);scope=args.get('projectId',[''])[0]
            prev=self.selection;rows=[]
            for t in ('t1','t2','t3'):
                self.selection=t
                if not scope or scope=='p'+t[-1]:rows.append(self.detail()['type'])
            self.selection=prev;self.calls.append((path,req.method,None))
            route.fulfill(json=dict(snapshotId='s1',items=rows,total=len(rows),indexLimited=False));return
        if path=='/api/explorer/action':
            a=req.post_data_json
            if a['action']=='type':self.selection=a['typeId']
            elif a['action']=='method':self.selection='t'+a['methodId'][-1]
        super().handle(route)

def mount(page, fixture, pagefile='project.html', route='/project.html'):
    def binding(_source,url,init):
        result={}
        req=SimpleNamespace(url=url,method=init.get('method','GET'),post_data_json=json.loads(init['body']) if init.get('body') else None,
            headers={k.lower():v for k,v in init.get('headers',{}).items()})
        def fulfill(**kwargs):result.update(dict(status=kwargs.get('status',200),data=kwargs.get('json')))
        fixture.handle(SimpleNamespace(request=req,fulfill=fulfill));return result
    page.expose_binding('mockNetwork',binding)
    html=ROOT.joinpath(pagefile).read_text()
    html=re.sub(r'<script\b[^>]*>.*?</script>','',html,flags=re.S)
    html=re.sub(r'<link\b[^>]*>','',html)
    page.set_content(html)
    for css in ['common-shell.css','path-picker.css','project.css']:
        page.add_style_tag(content=ROOT.joinpath(css).read_text())
    page.add_script_tag(content=ROOT.joinpath('common-shell.js').read_text())
    page.evaluate('u=>TenkaShell.install(document,{pathname:u,hash:""})',route)
    page.evaluate('''window.fetch=async(url,init={})=>{ const r=await mockNetwork(String(url),{method:init.method||'GET',headers:init.headers||{},body:init.body||null});return {ok:r.status===200,status:r.status,json:async()=>r.data};};''')
    if pagefile=='project.html':
        for name in ['path-picker.js','project-client.js','project-tabs.js','project.js']:
            page.add_script_tag(content=ROOT.joinpath(name).read_text())
        page.evaluate("document.dispatchEvent(new Event('DOMContentLoaded'))")

def main():
    checks=0;out=Path(sys.argv[1]) if len(sys.argv)>1 else None
    with sync_playwright() as p:
        browser=p.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        for width in [1440,1024,768,390,320]:
            page=browser.new_page(viewport=dict(width=width,height=1000));errors=[]
            page.on('pageerror',lambda e:errors.append(str(e)))
            f=Multi();mount(page,f)
            page.wait_for_function("document.querySelectorAll('#class-list .choice').length===3 && !document.querySelector('#class-list .choice').disabled")
            assert page.locator('.nav-primary > .nav-item').count()==4;checks+=1
            assert not page.locator('.nav-advanced').evaluate('(n)=>n.open');checks+=1
            assert page.locator('[data-workspace-panel]:visible').count()==1;checks+=1
            assert page.locator('[data-workspace-panel=code]').is_visible();checks+=1
            assert page.locator('#project-build').is_hidden();checks+=1
            before=len(f.calls);page.locator('#workspace-tab-run').click();page.locator('#workspace-tab-code').click()
            assert len(f.calls)==before;checks+=1
            # Class selection does not turn the all-project list into a single-project list.
            page.locator('#class-list .choice').nth(1).click()
            page.wait_for_function("document.querySelector('#type-name').textContent.includes('InventorySearch') && !document.querySelector('#class-scope').disabled")
            assert page.locator('#class-list .choice').count()==3;checks+=1
            assert page.locator('#class-scope').input_value()=='';checks+=1
            assert page.locator('#selected-project').input_value()=='p2';checks+=1
            assert page.locator('#selected-assembly').input_value()=='a2';checks+=1
            assert page.locator('#launch-assembly').input_value()=='a1';checks+=1
            page.locator('#method-list .choice').first.click()
            page.wait_for_function("!document.querySelector('#method-section').hidden && !document.querySelector('#inspect-il').disabled")
            assert page.locator('#method-source').inner_text().startswith('public static');checks+=1
            # Input survives switching phases; tabs have keyboard navigation.
            page.locator('#workspace-tab-run').click();page.locator('#workspace-panel-run details summary').click()
            page.locator('#runtime-arguments').fill('--scenario\ninventory')
            page.locator('#workspace-tab-code').click();page.locator('#workspace-tab-code').focus();page.keyboard.press('ArrowRight')
            assert page.locator('#workspace-panel-run').is_visible();checks+=1
            assert page.locator('#runtime-arguments').input_value()=='--scenario\ninventory';checks+=1
            page.locator('#workspace-tab-code').click()
            # IL goes to results without requiring any extra paths.
            page.locator('#inspect-il').click()
            page.wait_for_function("document.querySelector('#result-body').textContent.includes('ldarg.0') && !document.querySelector('#inspect-il').disabled")
            assert page.locator('#workspace-panel-results').is_visible();checks+=1
            page.get_by_role('button',name='コード調査へ戻る',exact=True).click()
            assert page.locator('#method-source').is_visible();checks+=1
            # Follow reference to another project while retaining all-project search.
            page.locator('#method-references').click()
            page.wait_for_function("document.querySelector('#workspace-panel-results:not([hidden])') && !document.querySelector('#method-references').disabled")
            page.get_by_role('button',name='このメソッドへ',exact=True).click()
            page.wait_for_function("document.querySelector('#method-title').textContent.includes('Bootstrap') && !document.querySelector('#method-references').disabled")
            assert page.locator('#selected-project').input_value()=='p1';checks+=1
            assert page.locator('#class-scope').input_value()=='';checks+=1
            assert page.locator('#class-list .choice').count()==3;checks+=1
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth+1');checks+=1
            ids=page.locator('[id]').evaluate_all('(ns)=>ns.map(n=>n.id)');assert len(ids)==len(set(ids));checks+=1
            if width==1440 and out:
                out.mkdir(parents=True,exist_ok=True)
                page.locator('#class-list .choice').nth(1).click()
                page.wait_for_function("document.querySelector('#type-name').textContent.includes('InventorySearch') && !document.querySelector('#class-scope').disabled")
                page.evaluate('scrollTo(0,0)');page.screenshot(path=str(out/'tenka_organized_workspace_preview.png'),full_page=True)
            assert not errors,errors;checks+=1
            page.close()
        # Direct visits still expose the active detail route.
        page=browser.new_page();mount(page,Multi(),pagefile='assembly.html',route='/assembly.html')
        assert page.locator('.nav-advanced').evaluate('(n)=>n.open');checks+=1
        assert page.locator('.nav-item[aria-current="page"]').get_attribute('data-page')=='assembly';checks+=1
        page.close()
        # No loaded workspace starts on prepare, not on an empty class list.
        page=browser.new_page();f=Multi(loaded=False);mount(page,f)
        page.wait_for_function("!document.querySelector('#use-sample').disabled")
        assert page.locator('#workspace-panel-prepare').is_visible();checks+=1
        assert not [c for c in f.calls if c[1]=='POST'];checks+=1
        browser.close()
    print(json.dumps(dict(checks=checks,widths=[1440,1024,768,390,320],mode='Chromium with actual new DOM/JS; APIs mocked, legacy styles.css/app.js omitted',csharpExecuted=False)))
if __name__=='__main__':main()
