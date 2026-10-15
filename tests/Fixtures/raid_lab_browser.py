"""Test actual UI selection/presets with mocked API; no C#, target or real HTTP execution."""
import json
from playwright.sync_api import sync_playwright
from organized_ui_browser import mount, Multi

SOLUTION=r'C:\Tenka\samples\Tenka.Sample.RaidLab\Tenka.Sample.RaidLab.sln'
class Raid(Multi):
    def handle(self,route):
        req=route.request
        if req.url=='/api/samples/raid-lab':
            self.calls.append((req.url,req.method,None))
            assert req.method=='GET'
            route.fulfill(json=dict(available=True,projectPath=SOLUTION,projectCount=6));return
        super().handle(route)

def main():
    checks=0
    with sync_playwright() as p:
        browser=p.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        for width in [1440,768,390]:
            page=browser.new_page(viewport=dict(width=width,height=1000));errors=[]
            page.on('pageerror',lambda e:errors.append(str(e)))
            f=Raid(False);mount(page,f)
            page.wait_for_function('!document.getElementById("use-sample").disabled')
            page.locator('#sample-kind').select_option('raid-lab');page.locator('#use-sample').click()
            page.wait_for_function('document.getElementById("project-path").value.includes("RaidLab") && !document.getElementById("use-sample").disabled')
            assert page.locator('#project-path').input_value()==SOLUTION;checks+=1
            assert not any(c[1]=='POST' for c in f.calls);checks+=1
            assert page.locator('[data-path-kind]').count()==1;checks+=1
            assert page.locator('#workspace-panel-prepare').is_visible();checks+=1
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth + 1');checks+=1
            page.locator('#project-open-button').click()
            page.wait_for_function('!document.getElementById("selected-project").disabled')
            assert any(c[0]=='/api/workspace/open' and c[2]['path']==SOLUTION for c in f.calls);checks+=1
            page.locator('#workspace-tab-run').click();page.locator('#workspace-panel-run details summary').click()
            for name,concurrency,scope in [('raid-run','1','pipeline'),('raid-batch','4','batch')]:
                page.locator('#argument-preset').select_option(name)
                actual=page.locator('#runtime-arguments').input_value().splitlines()
                assert actual==['--scope',scope,'--players','200','--events','6000','--concurrency',concurrency,'--seconds','4'];checks+=1
            page.locator('#workspace-tab-code').click();page.locator('#workspace-tab-run').click()
            assert page.locator('#runtime-arguments').input_value().splitlines()[1]=='batch';checks+=1
            assert not errors,errors;checks+=1
            page.close()
        browser.close()
    print(json.dumps({'checks':checks,'widths':[1440,768,390],'api':'mocked; base styles/app.js omitted','csharpExecuted':False}))
if __name__=='__main__':main()
