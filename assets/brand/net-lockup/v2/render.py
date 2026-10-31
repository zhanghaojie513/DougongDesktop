from pathlib import Path
from playwright.sync_api import sync_playwright
import json

root=Path(__file__).parent
width,height=json.loads((root/'metrics.json').read_text())['size']
with sync_playwright() as p:
    browser=p.chromium.launch()
    for file in ['dougong-net','dougong-net-white']:
        page=browser.new_page(viewport={'width':width,'height':height},device_scale_factor=4)
        svg=(root/(file+'.svg')).read_text(encoding='utf-8')
        page.set_content('<html><style>html,body{margin:0;background:transparent}svg{display:block}</style><body>'+svg+'</body></html>')
        page.screenshot(path=str(root/(file+'.png')),omit_background=True)
        page.close()
    page=browser.new_page(viewport={'width':1100,'height':850},device_scale_factor=1)
    errors=[]
    page.on('pageerror',lambda e:errors.append(str(e)))
    page.goto((root/'preview.html').resolve().as_uri())
    page.screenshot(path=str(root/'preview.png'),full_page=True)
    assert page.locator('img').evaluate_all('els=>els.every(e=>e.complete&&e.naturalWidth>0)')
    assert not errors
    browser.close()
print('Rendered transparent PNGs and preview; all images loaded.')
