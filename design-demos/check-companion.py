from pathlib import Path
from playwright.sync_api import sync_playwright
import json

url=(Path(__file__).parent/'dir-3.html').resolve().as_uri()
errors=[]
results=[]
with sync_playwright() as p:
    b=p.chromium.launch()
    page=b.new_page(viewport={'width':1440,'height':900})
    page.on('pageerror',lambda e:errors.append(str(e)))
    page.goto(url)
    assert page.locator('#composerInput').is_disabled()
    assert page.locator('[data-decision]').count()==4
    box=page.locator('[data-decision=stop]').bounding_box()
    transcript=page.locator('#transcript').bounding_box()
    assert box['y']+box['height'] < transcript['y']+transcript['height']
    for dock in ['files','changes','plan','context','terminal']:
        page.locator('[data-dock='+dock+']').click()
        assert page.locator('#dockContent').inner_text()
    assert '未实现' in page.locator('#dockContent').inner_text()
    page.locator('[data-view=settings]').click()
    assert page.get_by_label('活动提供商').is_disabled()
    page.locator('[data-action=test]').click()
    assert '未发送' in page.locator('#connectionResult').inner_text()
    page.get_by_label('显示榫灵').click()
    assert page.locator('#petZone').is_hidden()
    page.get_by_label('显示榫灵').click()
    assert page.locator('#petZone').is_visible()
    page.locator('[data-view=plugins]').click()
    page.locator('[data-action=load]').click()
    page.locator('[data-action=confirmload]').click()
    assert page.locator('#overlay').is_visible()
    page.locator('#trust').check()
    page.locator('[data-action=confirmload]').click()
    assert '本地示例扩展' in page.locator('#plugins').inner_text()
    page.locator('[data-toggle-plugin=sample]').click()
    assert page.locator('[data-toggle-plugin=sample]').get_attribute('aria-checked')=='false'
    page.locator('[data-unload=sample]').click()
    page.locator('[data-confirm-unload=sample]').click()
    assert page.locator('[data-unload=sample]').count()==0
    page.keyboard.press('Control+k')
    assert page.locator('#commandSearch').is_visible()
    page.keyboard.press('Escape')
    assert page.locator('#overlay').is_hidden()
    page.locator('[data-view=conversation]').first.click()
    page.locator('[data-decision=once]').click()
    assert not page.locator('#composerInput').is_disabled()
    assert page.locator('[data-decision]').count()==0
    assert '已允许本次读取' in page.locator('#approval').inner_text()
    page.locator('#composerInput').fill('继续说明一下职责边界')
    page.locator('#send').click()
    assert '继续说明一下职责边界' in page.locator('#transcript').inner_text()
    page.goto(url)
    page.locator('[data-decision=stop]').click()
    assert '本轮已停止' in page.locator('#followup').inner_text()
    assert '文件读取已取消' in page.locator('#dockContent').inner_text()
    for width,height in [(1440,900),(1280,800),(375,812)]:
        page.set_viewport_size({'width':width,'height':height})
        page.goto(url)
        assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
        results.append(f'{width}x{height} 无横向溢出')
    b.close()
assert not errors,errors
print(json.dumps({'functional_checks':'通过审批、待决锁、右栏5页、设置、宠物开关、插件启停卸载与信任确认、命令面板、输入提交、拒绝停止状态','viewports':results,'page_errors':errors},ensure_ascii=False))
