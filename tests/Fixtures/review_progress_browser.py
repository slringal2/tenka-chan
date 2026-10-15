"""Actual new DOM/client/CSS with a mock API. No .NET, SQLite or real MCP execution."""
import json
import re
from pathlib import Path
from urllib.parse import urlparse, parse_qs
from playwright.sync_api import sync_playwright
ROOT=Path(__file__).resolve().parents[2]/'src/TenkaChan.Web/wwwroot'

def main():
    assertions=0
    with sync_playwright() as p:
        browser=p.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        for width in [1440,1024,768,390,320]:
            page=browser.new_page(viewport={'width':width,'height':1050});errors=[];calls=[];state={'stale':False}
            page.on('pageerror',lambda e:errors.append(str(e)))
            def checkpoint(rev,status):
                return dict(revision=rev,status=status,recordedUtc='2026-10-15T03:00:00Z',agent='外部エージェント',summary='<img src=x onerror="window.injected=true"> 入力条件を維持して検索処理を変更。実測は未実施。',evidence=[dict(tool='inspect_method',outcome='returned',observedStatus=None)])
            def item(id,name,mark):
                return dict(target=dict(id=id,selectionId=id,kind='method',projectId='p',project='GameLab',name=name,trackable=True,sourceHash='A'*64,contextHash='B'*64),
                    state=dict(status='needs_review' if state['stale'] else mark,revision=2 if mark=='optimized' else 0,reason='snapshot_outdated' if state['stale'] else 'current',requiresReload=state['stale'],checkpoint=checkpoint(2,'optimized') if mark=='optimized' else None))
            def request(source,url,init):
                assert init['method']=='GET';calls.append(url);parsed=urlparse(url);args={k:v[0] for k,v in parse_qs(parsed.query).items()};code=200
                if parsed.path.endswith('/queue'):
                    rows=[item('target','InventorySearch.CountOwnedFast','optimized'),item('pending','InventorySearch.CountOwnedSlow','unreviewed')]
                    if args.get('state')=='pending' and not state['stale']:rows=[rows[1]]
                    data=dict(snapshotId='snapshot',total=len(rows),offset=0,items=rows,summary=dict(total=2,unreviewed=1,checkedCount=0,optimized=0 if state['stale'] else 1,needsReview=2 if state['stale'] else 0),requiresReload=state['stale'],limited=False,scope='プレビュー用の模擬APIです。実測・実MCPの結果ではありません。')
                elif parsed.path.endswith('/target'):
                    row=item('target','InventorySearch.CountOwnedFast','optimized');data=dict(snapshotId='snapshot',**row,history=[checkpoint(2,'optimized'),checkpoint(1,'checked')])
                elif parsed.path.endswith('/diff'):
                    if state['stale'] and not args.get('toRevision'):code=409;data=dict(error='プロジェクトを再読み込みしてください。')
                    else:
                        changed=args.get('fromRevision')=='1'
                        rows=[dict(kind='remove',oldLine=10,newLine=None,text='var usable = owned.Where(id => id >= 0).ToList();'),dict(kind='add',oldLine=None,newLine=9,text='var usable = new HashSet<int>(owned.Where(id => id >= 0));'),dict(kind='context',oldLine=11,newLine=10,text='// <script>window.injected = true</script>')] if changed else []
                        data=dict(snapshotId='snapshot',targetId='target',hasBaseline=True,diff=dict(mode='line_lcs',added=1 if changed else 0,removed=1 if changed else 0,rows=rows,totalRows=len(rows),hasMore=False,note='保存された確認時点の差分（模擬データ）'))
                else:raise AssertionError(url)
                return dict(status=code,data=data)
            page.expose_binding('reviewMock',request)
            html=ROOT.joinpath('review-progress.html').read_text();html=re.sub(r'<script\b[^>]*>.*?</script>','',html,flags=re.S);html=re.sub(r'<link\b[^>]*>','',html)
            page.set_content(html)
            for name in ['common-shell.css','review-progress.css']:page.add_style_tag(content=ROOT.joinpath(name).read_text())
            page.add_script_tag(content=ROOT.joinpath('common-shell.js').read_text());page.evaluate("TenkaShell.install(document,{pathname:'/review-progress.html',hash:''})")
            page.evaluate("() => { window.fetch=async(url,init)=>{const x=await window.reviewMock(String(url),{method:init.method});return {ok:x.status===200,json:async()=>x.data};}; }")
            page.add_script_tag(content=ROOT.joinpath('review-progress.js').read_text());page.evaluate("document.dispatchEvent(new Event('DOMContentLoaded'))")
            page.wait_for_selector('.review-choice');assert page.locator('.review-choice').count()==1;assertions+=1
            page.locator('#review-filter').select_option('all');page.wait_for_function("document.querySelectorAll('.review-choice').length===2")
            page.locator('.review-choice').first.click();page.wait_for_function("document.querySelector('#review-detail').hidden===false && !document.querySelector('#review-compare').disabled")
            assert '最適化済み' in page.locator('#review-target-status').inner_text();assertions+=1
            assert not page.evaluate('window.injected===true');assertions+=1
            assert page.locator('#review-summary img').count()==0;assertions+=1
            page.locator('#review-before').select_option('1');page.wait_for_selector('#review-diff-body tr[data-kind=add]')
            assert 'HashSet' in page.locator('#review-diff-body').inner_text();assertions+=1
            assert page.locator('#review-diff-body tr[data-kind=remove]').count()==1;assertions+=1
            assert not page.evaluate('window.injected===true');assertions+=1
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth + 1'),width;assertions+=1
            assert page.locator('.nav-item[aria-current=page]').inner_text().find('確認状況・差分')>=0;assertions+=1
            if width==1440:
                page.evaluate('window.scrollTo(0,0)');page.screenshot(path='/mnt/data/tenka-review-progress-preview.png',full_page=True)
            state['stale']=True;page.locator('#review-refresh').click();page.wait_for_function("!document.querySelector('#review-reload').hidden")
            assert page.locator('#review-reload').is_visible();assertions+=1
            page.locator('#review-compare').click();page.wait_for_function("document.querySelector('#review-diff-status').textContent.includes('再読み込み')")
            assert page.locator('#review-diff-body tr').count()==0;assertions+=1
            assert not errors,errors;assertions+=1
            assert all('/api/review-progress/' in url for url in calls);assertions+=1
            page.close()
        browser.close()
    print(json.dumps(dict(assertions=assertions,widths=[1440,1024,768,390,320],api='mocked',dotnet=False)))
if __name__=='__main__':main()
