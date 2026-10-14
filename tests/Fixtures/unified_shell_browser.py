"""Chromium DOM/layout regression, with set_content and local scripts (no web server).
Unchanged application scripts/styles.css are not executed in this isolated shell
fixture. It tests the actual shared frame, tutorial and preserved HTML forms.
Links are inspected/activated without navigation; API replies are mocks. This is
NOT an ASP.NET, real routing, Windows dialog, or code-analysis integration test.
"""
import json
import re
from pathlib import Path
from playwright.sync_api import sync_playwright
ROOT = Path(__file__).resolve().parents[2] / 'src' / 'TenkaChan.Web' / 'wwwroot'
PAGES = {'index': 'overview', 'workspace-build': 'build', 'assembly': 'assembly', 'trace': 'trace', 'priorities': 'priorities', 'guide': 'guide'}

def main():
    results=[]
    with sync_playwright() as pw:
        browser=pw.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox','--disable-dev-shm-usage'])
        for width,height in [(1440,1000),(390,844),(320,720)]:
            dims=[]
            for name,key in PAGES.items():
                page=browser.new_page(viewport={'width':width,'height':height});errors=[]
                page.on('pageerror',lambda e:errors.append(str(e)))
                html=(ROOT/(name+'.html')).read_text()
                # Set only the source DOM, then load the code/style under test locally.
                html=re.sub(r'<script\b[^>]*>.*?</script>','',html,flags=re.S)
                html=re.sub(r'<link\b[^>]*>','',html)
                page.set_content(html)
                page.add_style_tag(path=str(ROOT/'common-shell.css'))
                page.add_script_tag(path=str(ROOT/'common-shell.js'))
                page.evaluate('(path)=>TenkaShell.install(document,{pathname:path,hash:""})','/'+name+'.html')
                assert page.locator('.shell').count()==1
                assert page.locator('.sidebar').count()==1
                assert page.locator('.topbar').count()==1
                assert page.locator('.nav-item').count()==13
                assert page.locator('iframe').count()==0
                assert page.locator('.nav-item.active').count()==1
                assert page.locator('.nav-item.active').get_attribute('data-page')==key
                assert '↗' not in page.locator('.nav-list').inner_text()
                ids=page.locator('[id]').evaluate_all('(nodes)=>nodes.map(n=>n.id)')
                assert len(ids)==len(set(ids)),(name,'duplicate ID')
                # Main dashboard-specific content is outside this isolated style test;
                # four workbenches and the tutorial must have no page-level overflow.
                if name!='index': assert page.evaluate('document.documentElement.scrollWidth <= innerWidth + 1'),(name,width,'overflow')
                inputs=page.locator('input:not([type=checkbox]):not([type=number])')
                if inputs.count():
                    original=inputs.first.input_value()
                    inputs.first.fill('C:\\layout-test\\入力.csproj')
                    page.evaluate('(path)=>TenkaShell.install(document,{pathname:path,hash:""})','/'+name+'.html')
                    assert inputs.first.input_value()=='C:\\layout-test\\入力.csproj'
                    inputs.first.fill(original)
                dim=page.locator('.sidebar').evaluate('(n)=>({width:n.getBoundingClientRect().width,bg:getComputedStyle(n).backgroundColor,font:getComputedStyle(n).fontFamily})')
                dims.append(dim)
                if name=='guide':
                    page.evaluate('''() => { window.calls=[]; window.fetch=async (path,init)=>{calls.push({path,init});return {ok:true,json:async()=>({available:true,projectPath:'C:\\\\Projects\\\\GameLab.csproj',sampleDirectory:'C:\\\\Projects',assemblyPath:'C:\\\\Projects\\\\GameLab.dll',traceOutputDirectory:'C:\\\\Projects\\\\profiles',assemblyExists:false})}}; }''')
                    page.add_script_tag(path=str(ROOT/'guide.js'))
                    page.wait_for_function("document.getElementById('guide-start').getAttribute('href').includes('?project=')")
                    assert page.evaluate('calls.length===1 && calls[0].init.method==="GET"')
                    # Exercise real anchor activation/keyboard events, but don't navigate.
                    page.evaluate('document.addEventListener("click",e=>{const a=e.target.closest("a");if(a){e.preventDefault();window.lastLink=a.getAttribute("href");}})')
                    page.locator('.nav-item[data-page=assembly]').click();assert page.evaluate('lastLink')=='/assembly.html'
                    page.locator('.nav-item[data-page=priorities]').focus();page.keyboard.press('Enter');assert page.evaluate('lastLink')=='/priorities.html'
                assert not errors,errors
                if width==1440 and name in ('guide','workspace-build','assembly'):
                    page.evaluate('document.activeElement.blur()')
                    page.mouse.move(1400,90)
                    page.screenshot(path=f'/mnt/data/tenka-preview-{name}.png',full_page=False)
                results.append({'page':name,'width':width});page.close()
            assert all(d==dims[0] for d in dims),(width,dims)
        browser.close()
    print(json.dumps({'render_cases':len(results),'widths':[1440,390,320],'javascript_errors':0,'scope':'Chromium DOM/layout fixtures; mocked API; unchanged application scripts not run; links activated without navigation'},indent=2))
if __name__=='__main__':main()
