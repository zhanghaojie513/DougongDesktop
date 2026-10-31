from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.transformPen import TransformPen
import re,base64,json

root=Path(__file__).parent
font=TTFont('C:/Windows/Fonts/GOTHIC.TTF')
cmap=font.getBestCmap()
# 几何字形骨架：修改d的上伸笔端切和g的回钩端面，并做光学加重。
d=font['glyf'][cmap[ord('d')]]
d.coordinates[16]=(1104,1428)
# 插入点，保留二次曲线控制点；只改外轮廓顶端。
from fontTools.ttLib.tables._g_l_y_f import GlyphCoordinates
coords=list(d.coordinates);flags=list(d.flags)
coords.insert(17,(1184,1508));flags.insert(17,1)
d.coordinates=GlyphCoordinates(coords)
from array import array
d.flags=array('B',flags);d.endPtsOfContours=[v+1 for v in d.endPtsOfContours]
g=font['glyf'][cmap[ord('g')]]
g.coordinates[10]=(166,-196);g.coordinates[11]=(186,-166);g.coordinates[12]=(266,-86)
glyphset=font.getGlyphSet()
size=39;scale=size/font['head'].unitsPerEm
pen=SVGPathPen(glyphset);bounds=BoundsPen(glyphset);x=0
tracking=[-.55,-.65,-.25,-.55,-.65,-.2]
for i,ch in enumerate('dougong'):
 name=cmap[ord(ch)];transform=(scale,0,0,-scale,x,0)
 glyphset[name].draw(TransformPen(pen,transform));glyphset[name].draw(TransformPen(bounds,transform))
 x+=font['hmtx'][name][0]*scale+(tracking[i] if i<6 else 0)
path=pen.getCommands();b=bounds.bounds
symbol=(root.parents[1]/'svg/symbol.svg').read_text(encoding='utf-8');symbol_paths=re.findall(r'<path[^>]*d="([^"]+)"',symbol)
word_x=55-b[0];word_y=44
word_end=word_x+b[2];badge_x=word_end+12;width=round(badge_x+45+10)
net=[('M0 14V0H2.2L9 10.3V0H11.2V14H9L2.2 3.7V14Z',0),('M0 0H10V2H2V6H9V8H2V12H10V14H0Z',13.2),('M0 0H12V2H7V14H5V2H0Z',25.2)]
def word(fg):return f'<path transform="translate({word_x:.4f} {word_y})" d="{path}" fill="{fg}" stroke="{fg}" stroke-width=".72" stroke-linejoin="round"/>'
def svg(fg,bg):return f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="64" viewBox="0 0 {width} 64" role="img" aria-labelledby="title"><title id="title">dougong NET — optical wordmark</title><g fill="{fg}" transform="translate(10 17) scale({32/176}) translate(-40 -40)">'+''.join(f'<path d="{d}"/>' for d in symbol_paths)+'</g>'+word(fg)+f'<rect x="{badge_x:.4f}" y="22" width="45" height="22" rx="2.1" fill="{fg}"/><g fill="{bg}" transform="translate({badge_x+(45-37.2*.86)/2:.4f} {33-14*.86/2:.4f}) scale(.86)">'+''.join(f'<path transform="translate({pos} 0)" d="{d}"/>' for d,pos in net)+'</g></svg>'
black=svg('#121212','#fff');white=svg('#fff','#121212')
(root/'dougong-net.svg').write_text(black,encoding='utf-8');(root/'dougong-net-white.svg').write_text(white,encoding='utf-8')
def uri(s):return 'data:image/svg+xml;base64,'+base64.b64encode(s.encode()).decode()
html=(root.parent/'preview.html').read_text(encoding='utf-8')
assets=iter([black,black,white,black]);html=re.sub(r'src="data:image/svg\+xml;base64,[^"]+"',lambda m:'src="'+uri(next(assets))+'"',html)
html=html.replace('黑白品牌组合 / 2026.10.31','字标重构 / 光学修订').replace('合构图标，小写字标，NET 徽标。','让字母连成一个品牌名字。').replace('沿用已有产品图标，组合结构参考你提供的 dsh 截图。<br>字标已转为矢量轮廓，小尺寸使用不依赖本机字体。','圆形字腔与比例字宽，重新校准笔画和字距。<br>d 与 g 的小切面呼应合构图标，NET 退为辅助层级。')
(root/'preview.html').write_text(html,encoding='utf-8')
(root/'metrics.json').write_text(json.dumps({'size':[width,64],'wordmark':'modified geometric glyph structure, custom d/g terminals and optical weight','tracking':tracking,'font_basis':'Century Gothic regular, outlined and modified','stroke_expansion':.72},indent=2),encoding='utf-8')
(root/'hero.html').write_text('<!doctype html><meta charset="utf-8"><style>*{box-sizing:border-box}body{margin:0;background:#fafafa;width:1000px;height:230px;display:flex;align-items:center;justify-content:center}img{width:650px;height:auto}</style><img alt="dougong NET" src="'+uri(black)+'">',encoding='utf-8')
print('Built optical revision:',width,'x 64')
