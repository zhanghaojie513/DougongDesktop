from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.transformPen import TransformPen
import base64,re
root=Path(__file__).parent
sym=(root.parents[2]/'svg/symbol.svg').read_text(encoding='utf-8')
uri=lambda s:'data:image/svg+xml;base64,'+base64.b64encode(s.encode()).decode()
def sketch(font_name,xscale=1,shear=0,stroke=0):
 f=TTFont('C:/Windows/Fonts/'+font_name);g=f.getGlyphSet();cm=f.getBestCmap();p=SVGPathPen(g);b=BoundsPen(g);x=0;scale=55/f['head'].unitsPerEm
 for ch in 'DOUGONG':
  name=cm[ord(ch)];t=(scale*xscale,0,shear*scale,-scale,x,0);g[name].draw(TransformPen(p,t));g[name].draw(TransformPen(b,t));x+=f['hmtx'][name][0]*scale*xscale
 xmin,ymin,xmax,ymax=b.bounds
 return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{xmin-2} {ymin-2} {xmax-xmin+4} {ymax-ymin+4}"><path d="{p.getCommands()}" stroke="#111" stroke-width="{stroke}" fill="#111" stroke-linejoin="round"/></svg>'
cards=[('A','构造','宽体与结构感','GOTHICB.TTF',1.1,0,0),('B','进势','前倾与推进节奏','bahnschrift.ttf',.83,.2,.5),('C','连构','线条与连接关系','GOTHIC.TTF',1,0,.65),('D','旅行线','柔性与留白','GOTHICI.TTF',1.05,0,0),('E','厚块','体量与压缩','arialbd.ttf',.9,0,1.5),('F','镌字','窄长与切面','bahnschrift.ttf',.72,0,.1)]
html='''<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>DOUGONG 六个方向草图</title><style>*{box-sizing:border-box}body{margin:0;padding:32px;background:#edf0f2;color:#16191d;font:14px/1.6 'Segoe UI','Microsoft YaHei',sans-serif}h1{font-size:24px;margin:0}header p{margin:5px 0 25px;color:#5f6871}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:20px}.card{background:#fff;border:1px solid #d9dfe5;border-radius:8px;overflow:hidden}.stage{height:190px;display:flex;align-items:center;padding:30px 22px;gap:11px}.symbol{width:40px;height:40px;flex:none}.word{flex:1;min-width:0}.badge{background:#111;color:white;font:12px Consolas;padding:1px 5px;border-radius:2px}.caption{padding:15px 20px;border-top:1px solid #e2e6ea}.caption strong{font-size:16px}.caption span{float:right;font-size:12px;color:#5c6670}.caption p{margin:4px 0 0;color:#5c6670}footer{margin-top:23px;color:#5c6670;font-size:12px}@media(max-width:900px){.grid{grid-template-columns:1fr 1fr}}@media(max-width:550px){.grid{grid-template-columns:1fr}}</style><header><h1>DOUGONG / 六个字形方向</h1><p>先比较整体轮廓，再深化字母。下图为方向草图，A、B、C另有完整概念稿。</p></header><div class="grid">'''
for letter,name,desc,font,xscale,shear,stroke in cards:
 svg=sketch(font,xscale,shear,stroke)
 html+=f'<section class="card"><div class="stage"><img class="symbol" src="{uri(sym)}" alt="合构图标"><img class="word" src="{uri(svg)}" alt="DOUGONG轮廓草图"><span class="badge">NET</span></div><div class="caption"><strong>{letter}　{name}</strong><span>{"入选" if letter in "ABC" else "备选"}</span><p>{desc}</p></div></section>'
html+='</div><footer>草图使用不同字形骨架模拟方向，不作为最终定制字标交付。完整概念稿将重新设计字形。</footer>'
(root/'thumbs.html').write_text(html,encoding='utf-8')
print('Created six thumbnail concepts')
