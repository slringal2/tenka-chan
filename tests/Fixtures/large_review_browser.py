"""Chromium pagination checks against the actual review page/JS and mocked GET APIs.
Not an ASP.NET, MCP, .NET or database integration test. Legacy styles.css is omitted.
"""
import json
import re
from pathlib import Path
from urllib.parse import urlparse, parse_qs
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[2] / 'src/TenkaChan.Web/wwwroot'


def main():
    checks = 0
    with sync_playwright() as play:
        browser = play.chromium.launch(executable_path='/usr/bin/chromium', headless=True, args=['--no-sandbox'])
        for width in [1440, 1024, 768, 390, 320]:
            page = browser.new_page(viewport={'width': width, 'height': 1000})
            calls, errors = [], []
            state = {'marked': False, 'conflict': False, 'snapshot': 's1'}
            page.on('pageerror', lambda error: errors.append(str(error)))

            def row(i):
                return dict(target=dict(id=f'target-{i}', selectionId=f'selection-{i}', kind='method',
                    project='Library', name=f'Namespace.Worker.Operation{i}()', trackable=True),
                    state=dict(status='unreviewed', reason='not_checked', revision=0, requiresReload=False))

            def api(_source, url, init):
                assert init['method'] == 'GET'
                assert not init.get('body')
                parsed = urlparse(url)
                assert parsed.path == '/api/review-progress/queue'
                q = {k: v[0] for k, v in parse_qs(parsed.query).items()}
                calls.append(q)
                if q.get('cursor'):
                    assert q['offset'] == '0'
                    assert q['cursor'] == 'boundary_2-'
                    assert q['snapshotId'] == 's1'
                    if state['conflict']:
                        return {'status': 409, 'data': {'error': 'ソースが更新されました。先頭から更新してください。'}}
                    items, more = [row(3), row(4)], False
                elif state['marked']:
                    items, more = [row(3), row(4)], False
                else:
                    items, more = [row(1), row(2)], True
                return dict(status=200, data=dict(snapshotId=state['snapshot'], items=items,
                    total=2 if state['marked'] else 4, offset=0, nextCursor='boundary_2-' if more else None,
                    hasMore=more, summary=dict(unreviewed=2, checkedCount=2 if state['marked'] else 0,
                    optimized=0, needsReview=0, blockedPending=1), requiresReload=False, limited=True,
                    scope='模擬応答。索引外は未確認のままです。'))

            page.expose_binding('largeReviewRead', api)
            html = re.sub(r'<script\b[^>]*>.*?</script>', '', ROOT.joinpath('review-progress.html').read_text(), flags=re.S)
            html = re.sub(r'<link\b[^>]*>', '', html)
            page.set_content(html)
            for name in ['common-shell.css', 'review-progress.css']:
                page.add_style_tag(content=ROOT.joinpath(name).read_text())
            page.add_script_tag(content=ROOT.joinpath('common-shell.js').read_text())
            page.evaluate("TenkaShell.install(document,{pathname:'/review-progress.html',hash:''})")
            page.evaluate("""() => { window.fetch=async (url,init)=>{
                const r=await window.largeReviewRead(String(url),{method:init.method,body:init.body??null});
                return {ok:r.status===200,status:r.status,json:async()=>r.data};
            }; }""")
            page.add_script_tag(content=ROOT.joinpath('review-progress.js').read_text())
            page.evaluate("document.dispatchEvent(new Event('DOMContentLoaded'))")
            page.wait_for_function("document.querySelectorAll('.review-choice').length===2")
            assert 'cursor' not in calls[-1]; checks += 1
            assert page.locator('#review-more').is_visible(); checks += 1
            assert '完了扱いにはしていません' in page.locator('#review-status').inner_text(); checks += 1

            state['marked'] = True
            page.locator('#review-more').click()
            page.wait_for_function("document.querySelectorAll('.review-choice').length===4")
            assert calls[-1]['cursor'] == 'boundary_2-' and calls[-1]['offset'] == '0'; checks += 1
            assert page.locator('#review-more').is_hidden(); checks += 1
            assert '表示済み 4件 / 現在の条件一致 2件' == page.locator('#review-list-count').inner_text(); checks += 1
            page.locator('#review-refresh').click()
            page.wait_for_function("document.querySelectorAll('.review-choice').length===2")
            assert 'Operation3' in page.locator('.review-choice').first.inner_text(); checks += 1
            assert 'cursor' not in calls[-1]; checks += 1

            state['marked'] = False
            page.locator('#review-refresh').click()
            page.wait_for_function("document.querySelector('.review-choice').textContent.includes('Operation1') && !document.querySelector('#review-more').hidden")
            state['conflict'] = True
            count = len(calls)
            page.locator('#review-more').click()
            page.wait_for_function("document.querySelector('#review-status').textContent.includes('先頭から更新')")
            assert len(calls) == count + 1; checks += 1
            assert page.locator('.review-choice').count() == 2; checks += 1
            state.update(marked=True, conflict=False, snapshot='s2')
            page.locator('#review-refresh').click()
            page.wait_for_function("document.querySelector('.review-choice').textContent.includes('Operation3')")
            assert 'cursor' not in calls[-1]; checks += 1
            page.locator('#review-filter').select_option('all')
            page.wait_for_timeout(80)
            assert calls[-1]['state'] == 'all' and 'cursor' not in calls[-1]; checks += 1
            assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1'); checks += 1
            assert not errors, errors; checks += 1
            page.close()
        browser.close()
    print(json.dumps(dict(checks=checks, widths=[1440,1024,768,390,320], api='mocked GET', dotnet=False)))


if __name__ == '__main__':
    main()
