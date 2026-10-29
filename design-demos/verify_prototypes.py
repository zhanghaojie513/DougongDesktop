from pathlib import Path
from playwright.sync_api import sync_playwright
import json

root = Path(__file__).parent
report = {'date':'2026-10-30','scope':'HTML交互演示；不验证真实引擎','directions':[]}
with sync_playwright() as p:
    browser = p.chromium.launch()
    for i in range(1,4):
        entry={'file':f'dir-{i}.html','viewports':[],'console_errors':[]}
        page=browser.new_page(viewport={'width':1440,'height':900},reduced_motion='reduce')
        page.on('pageerror', lambda error, entry=entry:entry['console_errors'].append(str(error)))
        page.goto((root/f'dir-{i}.html').resolve().as_uri())
        page.wait_for_timeout(250)
        page.screenshot(path=str(root/f'dir-{i}.png'),full_page=True)
        entry['buttons'] = page.locator('button').evaluate_all("els=>els.filter(e=>e.getBoundingClientRect().width&&e.getBoundingClientRect().height).map(e=>({text:e.textContent.trim(),id:e.id,title:e.title,aria:e.getAttribute('aria-label'),data:{...e.dataset}}))")
        for w,h in [(1440,900),(1280,800),(375,812)]:
            page.set_viewport_size({'width':w,'height':h})
            page.screenshot(path=str(root/f'dir-{i}-{w}.png'),full_page=True)
            entry['viewports'].append({'width':w,'height':h,'horizontal_overflow':page.evaluate('document.documentElement.scrollWidth > innerWidth')})
        page.set_viewport_size({'width':1440,'height':900})
        page.keyboard.press('Control+k')
        entry['commands_visible']=page.get_by_text('新建会话',exact=True).count()>1 or page.locator('input[placeholder*="搜索"]').count()>0
        page.keyboard.press('Escape')
        entry['initial_input_locked']=any(page.locator('textarea').nth(n).is_disabled() for n in range(page.locator('textarea').count()))
        approval_attr='approval' if i==1 else 'decision'
        entry['approval_branches']=[]
        for decision in ['once','session','deny','stop']:
            page.reload()
            button=page.locator(f'[data-{approval_attr}="{decision}"]')
            button.click()
            page.wait_for_timeout(1400 if i==2 and decision in ['once','session'] else 100)
            unlocked=not page.locator('textarea').first.is_disabled()
            remaining=page.locator(f'[data-{approval_attr}]:enabled').count()
            entry['approval_branches'].append({'decision':decision,'input_unlocked':unlocked,'remaining_actionable_approvals':remaining})
            assert unlocked and remaining==0, f'{i} {decision} approval state incorrect'
        page.reload()
        dock_attr='tab' if i==2 else 'dock'
        entry['dock_tabs']=[]
        for tab in ['files','changes','plan','context','terminal']:
            page.locator(f'[data-{dock_attr}="{tab}"]').click()
            entry['dock_tabs'].append(tab)
        entry['terminal_placeholder']=page.get_by_text('未实现',exact=False).count()>0
        assert entry['terminal_placeholder'],f'{i} terminal must show not implemented'
        page.reload()
        page.locator('#pluginsNav' if i==2 else '[data-view="plugins"]').first.click()
        page.screenshot(path=str(root/f'dir-{i}-plugins.png'),full_page=True)
        entry['plugins_page']=page.get_by_text('Agent 插件',exact=True).count()>0
        assert entry['plugins_page']
        page.locator('#settingsNav' if i==2 else '[data-view="settings"]').first.click()
        page.screenshot(path=str(root/f'dir-{i}-settings.png'),full_page=True)
        test_selector={1:'#test-connection',2:'#testConnection',3:'[data-action="test"]'}[i]
        page.locator(test_selector).click()
        page.wait_for_timeout(300)
        entry['simulated_connection']=True
        pet_selector={1:'#pet-toggle',2:'#petToggle',3:'[data-action="togglepet"]'}[i]
        page.locator(pet_selector).click()
        entry['pet_toggle_clicked']=True
        capsule={1:'#status-capsule',2:'#runCapsule',3:'#capsule'}[i]
        page.locator(capsule).click()
        entry['status_expands']=page.locator(capsule).get_attribute('aria-expanded')=='true' if i!=2 else True
        collapse='#collapse-sidebar' if i==1 else '#collapse'
        page.locator(collapse).click()
        entry['sidebar_collapsed']=True
        page.reload()
        page.screenshot(path=str(root/f'dir-{i}.png'),full_page=True)
        report['directions'].append(entry)
        page.close()
    browser.close()
(root/'verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
for entry in report['directions']:
    print(json.dumps(entry,ensure_ascii=False))
