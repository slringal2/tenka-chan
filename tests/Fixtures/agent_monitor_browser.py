"""Isolated Chromium DOM tests using actual new UI code, mocked read-only APIs.
Not an ASP.NET/MCP integration test. Legacy styles.css/application scripts are omitted.
"""
import json
import re
from pathlib import Path
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2] / 'src/TenkaChan.Web/wwwroot'

def main():
    assertions = 0
    session = 'a' * 32
    call = 'b' * 32
    fixture = dict(instanceId='server-test', enabled=True, allowBuild=True, allowExecution=False,
        lastSequence=4, note='このプレビューのAPI応答はテスト用です。実際の解析結果ではありません。',
        sessions=[dict(id=session, clientName='External agent', goal='GameLabの所持品検索を調査する',
            status='idle', lastSeen='2026-10-15T00:00:00Z', calls=1, activeTool=None)],
        events=[dict(sequence=1,sessionId=session,callId=None,kind='agent_plan',tool='note',
            message='まずクラスを調べ、割り当ての疑いをILと実測で確認します。',snapshotId=None,observedStatus='agent_reported_unverified',at='2026-10-15T00:00:00Z'),
            dict(sequence=2,sessionId=session,callId=call,kind='tool_started',tool='inspect_method',
            message='解析ツールの処理を開始しました。',snapshotId='snapshot',observedStatus=None,at='2026-10-15T00:00:01Z'),
            dict(sequence=3,sessionId=session,callId=call,kind='tool_result',tool='inspect_method',
            message='InventorySearch.CountOwnedSlow：結果が返りました。',snapshotId='snapshot',observedStatus=None,at='2026-10-15T00:00:02Z'),
            dict(sequence=4,sessionId=session,callId=None,kind='agent_hypothesis',tool='note',
            message='<img src=x onerror="window.injected=true"> この文字列はコードではなく未検証の報告です。',snapshotId=None,observedStatus='agent_reported_unverified',at='2026-10-15T00:00:03Z')])
    with sync_playwright() as p:
        browser=p.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        for width in (1440,1024,768,390,320):
            page=browser.new_page(viewport=dict(width=width,height=1000))
            calls=[]; errors=[]
            page.on('pageerror',lambda e:errors.append(str(e)))
            def api(_source,url,init):
                calls.append((url,init))
                assert init.get('method')=='GET'
                if url=='/api/agent/monitor':return fixture
                assert url=='/api/agent/result?callId='+call
                return dict(note='ツールが返したデータ',reply=dict(outcome='returned',data=dict(source='public static int CountOwnedSlow(...)',note='fixture only')))
            page.expose_binding('readMock',api)
            html=re.sub(r'<script\b[^>]*>.*?</script>','',ROOT.joinpath('agent.html').read_text(),flags=re.S)
            html=re.sub(r'<link\b[^>]*>','',html)
            page.set_content(html)
            for css in ('common-shell.css','agent-monitor.css'):page.add_style_tag(content=ROOT.joinpath(css).read_text())
            page.add_script_tag(content=ROOT.joinpath('common-shell.js').read_text())
            page.evaluate("TenkaShell.install(document,{pathname:'/agent.html',hash:''})")
            page.evaluate("window.fetch=async(url,init)=>({ok:true,status:200,json:async()=>window.readMock(String(url),{method:init.method})})")
            page.add_script_tag(content=ROOT.joinpath('agent-monitor.js').read_text())
            page.evaluate("document.dispatchEvent(new Event('DOMContentLoaded'))")
            page.wait_for_selector('.agent-event')
            assert page.locator('.agent-event').count()==4;assertions+=1
            assert page.locator('.nav-item[aria-current="page"]').inner_text().find('エージェントの活動')>=0;assertions+=1
            assert not page.evaluate('window.injected === true');assertions+=1
            assert page.locator('#agent-timeline img').count()==0;assertions+=1
            assert page.locator('form,input[type=password]').count()==0;assertions+=1
            assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1');assertions+=1
            page.locator('#agent-kind').select_option('notes')
            assert page.locator('.agent-event').count()==2;assertions+=1
            page.locator('#agent-kind').select_option('tools')
            assert page.locator('.agent-event').count()==2;assertions+=1
            page.get_by_role('button',name='結果を見る',exact=True).click()
            page.wait_for_selector('#agent-result-panel:not([hidden])')
            assert 'CountOwnedSlow' in page.locator('#agent-result').inner_text();assertions+=1
            assert all(init['method']=='GET' for _,init in calls);assertions+=1
            assert not errors,errors;assertions+=1
            if width==1440:
                page.locator('#agent-kind').select_option('all')
                page.evaluate('window.scrollTo(0,0)')
                page.screenshot(path='/mnt/data/tenka-agent-monitor-preview.png',full_page=True)
            page.close()
        # Old dashboard IDs are kept for compatibility, but its model/start controls are inaccessible.
        page=browser.new_page(viewport=dict(width=1440,height=1000))
        html=re.sub(r'<script\b[^>]*>.*?</script>','',ROOT.joinpath('index.html').read_text(),flags=re.S)
        html=re.sub(r'<link\b[^>]*>','',html)
        page.set_content(html);page.add_style_tag(content=ROOT.joinpath('common-shell.css').read_text())
        page.add_script_tag(content=ROOT.joinpath('common-shell.js').read_text())
        page.evaluate("TenkaShell.install(document,{pathname:'/',hash:''});document.getElementById('view-autoreview').hidden=false")
        assert page.locator('#auto-start').count()==1 and not page.locator('#auto-start').is_visible();assertions+=1
        assert page.locator('#auto-endpoint').count()==1 and not page.locator('#auto-endpoint').is_visible();assertions+=1
        assert page.locator('.nav-item[data-page=autoreview]').get_attribute('href')=='/agent.html';assertions+=1
        assert len(page.locator('[id]').evaluate_all('(ns)=>ns.map(n=>n.id)'))==len(set(page.locator('[id]').evaluate_all('(ns)=>ns.map(n=>n.id)')));assertions+=1
        browser.close()
    print(json.dumps(dict(assertions=assertions,widths=[1440,1024,768,390,320],apis='mocked read-only',dotnet=False)))

if __name__=='__main__':main()
