from pathlib import Path
from PIL import Image
from playwright.sync_api import sync_playwright
import base64,json,re

root=Path(__file__).parent
symbol=(root.parents[2]/'svg/symbol.svg').read_text(encoding='utf-8')
symbol=re.sub(r'viewBox="[^"]+"','viewBox="40 40 176 176"',symbol)
def uri(path):return 'data:image/png;base64,'+base64.b64encode(path.read_bytes()).decode()
def svg_uri(s):return 'data:image/svg+xml;base64,'+base64.b64encode(s.encode()).decode()
info={
 'A':('几何','直立、紧凑、清楚的轮廓。','O采用圆形字腔，D/U保持同一笔画体系；G以斜切开口形成识别。','适合偏理性、明确的工具品牌；G仍然较硬朗。'),
 'B':('微倾','圆角、前倾、有运动感。','缩短字母之间的距离，统一曲线转角，让名字产生连续的阅读节奏。','动势较强，比参考更偏运动气质。'),
 'C':('柔弧 · 推荐','直立圆弧，细节克制。','D/O/U/G采用统一的圆弧语言，G保留短横与清晰开口；字母独立、紧凑排列。','三个方向中更接近参考的温和与耐看，建议沿这一版继续精修。')
}
meta={}
for key in info:
 im=Image.open(root/f'concept-{key}.png').convert('L');iw,ih=im.size;p=im.load()
 cols=[x for x in range(iw) if sum(p[x,y]<70 for y in range(ih))>=4]
 runs=[];start=prev=cols[0]
 for x in cols[1:]:
  if x>prev+1:runs.append((start,prev));start=x
  prev=x
 runs.append((start,prev))
 # 只分析像素范围；源PNG不裁切或修改，排版以HTML溢出裁切显示字标区域。
 gaps=sorted([(runs[i+1][0]-runs[i][1]-1,i) for i in range(len(runs)-1)],reverse=True)
 boundaries=sorted(i for gap,i in gaps[:2]);left=runs[boundaries[0]+1][0];right=runs[boundaries[1]][1]
 rows=[y for y in range(ih) if any(p[x,y]<70 for x in range(left,right+1))]
 box=[left-3,min(rows)-3,right+4,max(rows)+4];cw,ch=box[2]-box[0],box[3]-box[1]
 meta[key]={'image':[iw,ih],'box':box,'ratio':cw/ch}
meta_path=root/'image-analysis.json';meta_path.write_text(json.dumps(meta,indent=2),encoding='utf-8')

def word(key):
 m=meta[key];iw,ih=m['image'];x,y,r,b=m['box'];cw,ch=r-x,b-y
 return f'<div class="word" style="aspect-ratio:{cw}/{ch}"><img alt="DOUGONG 大写字标概念 {key}" src="{uri(root/f"concept-{key}.png")}" style="width:{iw/cw*100:.8f}%;left:{-x/cw*100:.8f}%;top:{-y/ch*100:.8f}%"></div>'
def lockup(key,kind=''):
 return f'<div class="lockup {kind}"><img class="symbol" src="{svg_uri(symbol)}" alt="真实合构图标">{word(key)}<span class="net">NET</span></div>'
css='''*{box-sizing:border-box}body{margin:0;background:#edf0f2;color:#17191b;font:14px/1.65 'Segoe UI','Microsoft YaHei',sans-serif}button,a{font:inherit}button{cursor:pointer}main{max-width:1440px;margin:auto;padding:30px 42px}header{display:flex;justify-content:space-between;gap:20px;align-items:center;margin-bottom:22px}h1{margin:0;font-size:25px;font-weight:600;letter-spacing:-.6px}header p{color:#59636d;margin:5px 0 0}header a{color:#405b71;text-decoration:none;border:1px solid #bbc5ce;border-radius:6px;min-height:44px;display:inline-flex;align-items:center;padding:0 15px;font-size:12px}.hero{height:350px;background:white;border:1px solid #d8dee4;border-radius:9px;display:flex;align-items:center;justify-content:center}.lockup{display:flex;align-items:center;justify-content:center;gap:27px;max-width:100%}.symbol{width:84px;height:84px;flex:none}.word{position:relative;width:800px;max-width:100%;overflow:hidden;flex:none}.word img{position:absolute;display:block;max-width:none;height:auto;mix-blend-mode:multiply}.net{font:32px/1.15 Consolas,monospace;background:#111;color:white;padding:6px 9px;border-radius:3px;flex:none}.support{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-top:18px}.reverse,.note{height:208px;border:1px solid #d8dee4;border-radius:9px;padding:18px 22px}.reverse{background:#111;border-color:#111;color:white;display:flex;flex-direction:column;justify-content:space-between}.reverse>p{margin:0;color:#c5c9cd;font-size:12px}.reverse .lockup{gap:13px;margin:auto}.reverse .symbol{width:43px;height:43px;filter:invert(1)}.reverse .word{width:380px}.reverse .word img{filter:invert(1);mix-blend-mode:screen}.reverse .net{font-size:15px;background:white;color:#111;padding:4px 5px;border-radius:2px}.note{background:white}.note h2{font-size:19px;margin:0 0 12px}.note p{font-size:14px;color:#58636e;margin:9px 0}.application{margin-top:18px;border:1px solid #d8dee4;border-radius:8px;overflow:hidden;background:white}.appbar{height:32px;display:flex;justify-content:space-between;padding:5px 17px;font-size:12px;background:#f6f7f9;border-bottom:1px solid #e0e5e9;color:#59636d}.appbody{height:104px;display:flex;align-items:center;gap:30px;padding:0 22px}.appbody .lockup{width:320px;gap:8px;justify-content:flex-start}.appbody .symbol{width:25px;height:25px}.appbody .word{width:210px}.appbody .net{font-size:10px;padding:2px 3px;border-radius:1px}.appbody p{font-size:14px;color:#59636d}.footer{font-size:12px;color:#58636e;display:flex;justify-content:space-between;gap:20px;margin-top:17px}.footer a{color:#405b71}.compare{margin-top:15px;display:flex;flex-direction:column;gap:15px}.compare-row{background:white;border:1px solid #d8dee4;border-radius:8px;display:grid;grid-template-columns:190px 1fr;min-height:212px;align-items:center;padding:22px 27px;gap:20px;color:inherit;text-decoration:none}.compare-row h2{font-size:21px;margin:0 0 5px}.compare-row p{color:#59636d;font-size:14px;margin:0}.compare-row .lockup{gap:20px}.compare-row .word{width:710px}.compare-row .symbol{width:68px;height:68px}.compare-row .net{font-size:25px;padding:5px 7px}.darkmode{background:#1d2023;color:#eee}.darkmode .compare-row{background:#111;border-color:#343a40}.darkmode .compare-row p{color:#bdc5ce}.darkmode .symbol{filter:invert(1)}.darkmode .word img{filter:invert(1);mix-blend-mode:screen}.darkmode .net{background:white;color:#111}.toggle{height:44px;border:1px solid #b8c3cc;border-radius:6px;background:transparent;color:inherit;padding:0 15px;margin-right:10px;font-size:12px}.compare-row:hover{border-color:#657b8c}.compare-row:focus-visible{outline:2px solid #405b71;outline-offset:3px}@media(max-width:1250px){.hero .word{width:65vw}.hero .symbol{width:6.6vw;height:6.6vw}.hero .net{font-size:2.5vw}.hero .lockup{gap:2vw}.compare-row .word{width:49vw}.compare-row .symbol{width:5vw;height:5vw}.compare-row .net{font-size:1.8vw}.compare-row .lockup{gap:1.6vw}.reverse .word{width:27vw}.reverse .symbol{width:3vw;height:3vw}.reverse .net{font-size:1.2vw}}@media(max-width:700px){main{padding:20px 16px}header{display:block}header>div:last-child{margin-top:14px}.hero{height:220px}.support{grid-template-columns:1fr}.reverse .word{width:62vw}.reverse .symbol{width:7vw;height:7vw}.reverse .net{font-size:2.5vw}.compare-row{grid-template-columns:1fr;padding:20px;min-height:180px}.compare-row h2{font-size:19px}.compare-row .word{width:61vw}.compare-row .symbol{width:6vw;height:6vw}.compare-row .net{font-size:2.3vw}.compare-row .lockup{gap:1.8vw}.appbody{display:block;height:auto;padding:24px 18px}.appbody p{margin:20px 0 0}.footer{display:block}.note{height:auto;min-height:180px}}'''
css+=' .hero{height:330px}.symbol{width:100px;height:100px}.compare-row .symbol{width:82px;height:82px}.reverse,.note{height:200px}.appbody{height:92px}.footer{font-size:14px;margin-top:15px}main{padding-top:28px;padding-bottom:28px}header{margin-bottom:20px}@media(max-width:1250px){.hero .symbol{width:8vw;height:8vw}.compare-row .symbol{width:6vw;height:6vw}}@media(max-width:700px){.hero{height:220px}.hero .symbol{width:8vw;height:8vw}.compare-row .symbol{width:7vw;height:7vw}.appbody{height:auto}.note{height:auto}}'
for i,key in enumerate('ABC',1):
 name,tagline,idea,fit=info[key]
 html=f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>DOUGONG {key} {name}</title><style>{css}</style><main><header><div><h1>{key}　{name}</h1><p>{tagline}</p></div><a href="index.html">返回三个方向对照</a></header><section class="hero">{lockup(key)}</section><div class="support"><section class="reverse"><p>反白组合</p>{lockup(key)}</section><section class="note"><h2>字形思路</h2><p>{idea}</p><p>{fit}</p></section></div><section class="application"><div class="appbar"><span>Dougong Desktop</span><span>—　□　×</span></div><div class="appbody">{lockup(key)}<p>同一组真实图标与NET，比较重点是DOUGONG字标。</p></div></section><div class="footer"><span>原图标SVG / imagegen字标概念 / 统一NET排版；尚未制作最终字标SVG。</span><a href="concept-{key}.png">查看原始概念图</a></div></main></html>'''
 (root/f'dir-{i}.html').write_text(html,encoding='utf-8')
html=f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>DOUGONG / DS参考研究</title><style>{css}</style><main><header><div><h1>DOUGONG / 学习参考的字形秩序</h1><p>从圆弧、字距与主次比例出发；保持大写 DOUGONG。点击查看黑白与桌面应用稿。</p></div><div><button class="toggle" id="theme">切换深色对照</button><a href="reference-notes.md">参考分析</a></div></header><div class="compare">'''
for i,key in enumerate('ABC',1):
 name,tagline,idea,fit=info[key]
 html+=f'<a class="compare-row" href="dir-{i}.html"><div><h2>{key}　{name}</h2><p>{tagline}</p></div>{lockup(key)}</a>'
html+='</div><div class="footer"><span>完整组合初稿：原图标 + 大写字标 + NET。字标为栅格概念，待选定后制作矢量。</span><span>A 几何 / B 微倾 / C 柔弧</span></div></main><script>document.querySelector("#theme").onclick=()=>document.body.classList.toggle("darkmode")</script></html>'
(root/'index.html').write_text(html,encoding='utf-8')
with sync_playwright() as p:
 browser=p.chromium.launch()
 for name in ['dir-1','dir-2','dir-3','index']:
  page=browser.new_page(viewport={'width':1440,'height':900},device_scale_factor=1);page.goto((root/(name+'.html')).resolve().as_uri());page.screenshot(path=str(root/(name+'.png')),full_page=True)
  assert page.locator('img').evaluate_all('els=>els.every(e=>e.complete&&e.naturalWidth>0)')
  page.close()
 browser.close()
print('Created three complete boards and comparison; all images loaded.')
