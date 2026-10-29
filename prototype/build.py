from pathlib import Path
import base64, re

root=Path(__file__).parent
base=(root.parent/'design-demos/dir-1.html').read_text(encoding='utf-8')
style=re.search(r'<style>(.*?)</style>',base,re.S).group(1)
shell=base.split('<body>',1)[1].split('<script>',1)[0]
paths=re.search(r'const paths=(.*?);\s*function icon',base,re.S).group(1)
petroot=Path('C:/work/myProject/dougong/artifacts/brand-character/sunling-pet-colored/svg')
assets={name:'data:image/svg+xml;base64,'+base64.b64encode((petroot/f'sunling-{name}.svg').read_bytes()).decode() for name in ['idle','thinking','working','success','sleeping','dragged']}
logo='data:image/svg+xml;base64,'+base64.b64encode(Path('C:/work/myProject/dougong/assets/brand/svg/symbol.svg').read_bytes()).decode()
import json
html='''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Dougong Desktop — 清晰工作台</title>
<!-- 已确认方向A。全部运行、文件、连接和插件数据为模拟；会话在内存中，布局偏好存浏览器。form：先理解工具证据，再作出授权。 -->
<style>'''+style+'\n'+(root/'extra.css').read_text(encoding='utf-8')+'</style></head><body>'+shell
html=html.replace('交互原型（演示数据）','交互设计稿 / 模拟数据').replace('原型 A / 清晰工作台','清晰工作台').replace('本地引擎就绪','本地引擎（演示）')
html+='<script>const logo='+json.dumps(logo)+';const petAssets='+json.dumps(assets)+';const paths='+paths+';\n'+(root/'app.js').read_text(encoding='utf-8')+'</script></body></html>'
(root/'index.html').write_text(html,encoding='utf-8')
print('Built prototype/index.html')
