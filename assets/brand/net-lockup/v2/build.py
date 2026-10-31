from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.transformPen import TransformPen
import re, base64, json

root=Path(__file__).parent
root.mkdir(exist_ok=True)
symbol=(root.parents[1]/'svg/symbol.svg').read_text(encoding='utf-8')
symbol_paths=re.findall(r'<path[^>]*d="([^"]+)"',symbol)

# 每个字形单独构造。x-height=30，主笔画=5.5；共享圆角与内白。
# d的45度顶切与g的回钩端切，回应已有合构符号的接合结构。
glyphs={
 'd':'M25.5 4.5L31 0V45H15.5Q0 45 0 30Q0 15 15.5 15H25.5Z M15.5 20.5Q5.5 20.5 5.5 30Q5.5 39.5 15.5 39.5H25.5V20.5Z',
 'o':'M14 15H17Q31 15 31 29V31Q31 45 17 45H14Q0 45 0 31V29Q0 15 14 15Z M14 20.5Q5.5 20.5 5.5 29V31Q5.5 39.5 14 39.5H17Q25.5 39.5 25.5 31V29Q25.5 20.5 17 20.5Z',
 'u':'M0 15H5.5V31Q5.5 39.5 15.5 39.5Q25.5 39.5 25.5 31V15H31V31Q31 45 15.5 45Q0 45 0 31Z',
 'g':'M31 15V44Q31 58 16 58H3L8.5 52.5H16Q25.5 52.5 25.5 44V41.5Q21 45 15 45Q0 45 0 30Q0 15 15 15Q21 15 25.5 18.5V15Z M15.5 20.5Q5.5 20.5 5.5 30Q5.5 39.5 15.5 39.5Q25.5 39.5 25.5 30Q25.5 20.5 15.5 20.5Z',
 'n':'M0 45V15H5.5V19Q10 15 17 15Q31 15 31 29V45H25.5V29Q25.5 20.5 17 20.5Q5.5 20.5 5.5 29V45Z',
}
# 光学校正：圆体相邻较近；u/g、n/g为开口/竖笔组合，留出稍多呼吸。
spacing=[5.0,5.6,6.2,5.0,5.4,6.0]
positions=[]; x=0
for i,ch in enumerate('dougong'):
    positions.append((ch,x))
    x+=31+(spacing[i] if i<6 else 0)
word_width=x
scale=.8; word_x=63; word_y=13
badge_x=word_x+word_width*scale+14
width=round(badge_x+48+10)

def badge_outline():
    font=TTFont('C:/Windows/Fonts/consola.ttf'); glyphset=font.getGlyphSet(); cmap=font.getBestCmap(); units=font['head'].unitsPerEm
    pen=SVGPathPen(glyphset); bounds=BoundsPen(glyphset); x=0; s=18/units
    for ch in 'NET':
        name=cmap[ord(ch)]; transform=(s,0,0,-s,x,0)
        glyphset[name].draw(TransformPen(pen,transform));glyphset[name].draw(TransformPen(bounds,transform));x+=font['hmtx'][name][0]*s
    return pen.getCommands(),bounds.bounds
badge,bounds=badge_outline()
bx=badge_x+(48-(bounds[2]-bounds[0]))/2-bounds[0]
by=36-(bounds[1]+bounds[3])/2
def word(fg):
    return f'<g fill="{fg}" fill-rule="evenodd" transform="translate({word_x} {word_y}) scale({scale})">'+''.join(f'<path data-letter="{ch}" transform="translate({pos:.3f} 0)" d="{glyphs[ch]}"/>' for ch,pos in positions)+'</g>'
def svg(fg,bg):
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="72" viewBox="0 0 {width} 72" role="img" aria-labelledby="title"><title id="title">dougong NET — custom wordmark</title><g fill="{fg}" transform="translate(10 18) scale({36/176}) translate(-40 -40)">'+''.join(f'<path d="{d}"/>' for d in symbol_paths)+'</g>'+word(fg)+f'<rect x="{badge_x:.4f}" y="24" width="48" height="24" rx="2.5" fill="{fg}"/><path d="{badge}" transform="translate({bx:.4f} {by:.4f})" fill="{bg}"/></svg>'
black=svg('#111315','#fff');white=svg('#fff','#111315')
(root/'dougong-net.svg').write_text(black,encoding='utf-8');(root/'dougong-net-white.svg').write_text(white,encoding='utf-8')
def uri(v):return 'data:image/svg+xml;base64,'+base64.b64encode(v.encode()).decode()
html=(root.parent/'preview.html').read_text(encoding='utf-8')
assets=iter([black,black,white,black])
html=re.sub(r'src="data:image/svg\+xml;base64,[^"]+"',lambda m:'src="'+uri(next(assets))+'"',html)
html=html.replace('黑白品牌组合 / 2026.10.31','定制字标 / 修订版').replace('合构图标，小写字标，NET 徽标。','圆角骨架，接合切角，单层 g。').replace('沿用已有产品图标，组合结构参考你提供的 dsh 截图。<br>字标已转为矢量轮廓，小尺寸使用不依赖本机字体。','dougong 七个字母逐字构造，统一笔画、圆角和内白。<br>d 的顶切与 g 的回钩呼应合构图标，字距单独校正。')
(root/'preview.html').write_text(html,encoding='utf-8')
(root/'metrics.json').write_text(json.dumps({'size':[width,72],'wordmark':'custom paths, not a font outline','glyphs':list(glyphs),'stroke':5.5,'x_height':30,'spacing':spacing},indent=2),encoding='utf-8')
(root/'wordmark.svg').write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="60 9 {word_width*.8+6:.3f} 54" width="{word_width*.8+6:.3f}" height="54"><title>dougong custom wordmark</title>{word("#111315")}</svg>',encoding='utf-8')
print('Built custom wordmark:',width,'x 72')
