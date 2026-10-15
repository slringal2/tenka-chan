"""Actual project UI/JS with mocked APIs. No Git, .NET or target application execution."""
import json
from playwright.sync_api import sync_playwright
from organized_ui_browser import mount, Multi

SOLUTION=r'C:\Tenka\samples\External\ILSpy-10.0\ILSpy.Analysis.slnx'
class Oss(Multi):
    def __init__(self): super().__init__(False); self.available=True
    def handle(self, route):
        req=route.request
        if req.url=='/api/samples/ilspy':
            self.calls.append((req.url,req.method,None))
            route.fulfill(json=dict(available=self.available,projectPath=SOLUTION if self.available else None,
                message='run-large-oss.batで1を実行してください。'));return
        super().handle(route)

def main():
    checks=0
    with sync_playwright() as p:
        browser=p.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        for width in [1440,768,390]:
            page=browser.new_page(viewport=dict(width=width,height=1000));errors=[]
            page.on('pageerror',lambda e:errors.append(str(e)))
            fixture=Oss();mount(page,fixture)
            page.wait_for_function('!document.getElementById("use-sample").disabled')
            page.locator('#sample-kind').select_option('ilspy');page.locator('#use-sample').click()
            page.wait_for_function('document.getElementById("project-path").value.includes("ILSpy") && !document.getElementById("use-sample").disabled')
            assert page.locator('#project-path').input_value()==SOLUTION;checks+=1
            assert not any(c[1]=='POST' for c in fixture.calls);checks+=1
            assert page.locator('[data-path-kind]').count()==1;checks+=1
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth + 1');checks+=1
            fixture.available=False
            page.locator('#use-sample').click()
            page.wait_for_function('document.getElementById("project-status").textContent.includes("run-large-oss.bat")')
            assert page.locator('#project-path').input_value()==SOLUTION;checks+=1
            assert not any(c[1]=='POST' for c in fixture.calls);checks+=1
            assert not errors,errors;checks+=1
            page.close()
        browser.close()
    print(json.dumps(dict(checks=checks,widths=[1440,768,390],api='mocked; legacy styles/app omitted',csharpExecuted=False)))
if __name__=='__main__':main()
