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

# 每个字形单独构造。保留已选V2骨架，主笔画6；圆体光学越界0.4。
# d的45度顶切与g的回钩端切，回应已有合构符号的接合结构。
glyphs={
 'd':'M24 5.8L30 0V45H14.8C5.4 45 0 39.6 0 30C0 20.4 5.4 15 14.8 15H24Z M14.8 21C9 21 6 24 6 30C6 36 9 39 14.8 39H21.3L24 36.3V23.7L21.3 21Z',
 'o':'M14 14.6H16C25.2 14.6 30 19.8 30 29V31C30 40.2 25.2 45.4 16 45.4H14C4.8 45.4 0 40.2 0 31V29C0 19.8 4.8 14.6 14 14.6Z M14 20.6C8.3 20.6 6 23.3 6 29V31C6 36.7 8.3 39.4 14 39.4H16C21.7 39.4 24 36.7 24 31V29C24 23.3 21.7 20.6 16 20.6Z',
 'u':'M0 15H6V31.6C6 36.9 8.8 39.4 15 39.4C21.2 39.4 24 36.9 24 31.6V15H30V31.6C30 40.6 25 45.4 15 45.4C5 45.4 0 40.6 0 31.6Z',
 'g':'M30 15V43C30 52.4 24.8 56 15 56H3.5L9.5 50H15C21.2 50 24 47.8 24 43V41.7C21.8 44.1 18.7 45.4 14.6 45.4C5.4 45.4 0 39.7 0 30C0 20.3 5.4 14.6 14.6 14.6C18.7 14.6 21.8 15.9 24 18.3V15Z M15 20.6C9.1 20.6 6 23.7 6 30C6 36.3 9.1 39.4 15 39.4H20.4L24 35.8V24.2L20.4 20.6Z',
 'n':'M0 45V15H6V18.9C8.7 16.1 12.1 14.6 16.4 14.6C25.4 14.6 30 19.5 30 28.6V45H24V28.8C24 23.2 21.4 20.6 16.1 20.6C9.7 20.6 6 24 6 30V45Z',
}
# 光学校正：圆体相邻较近；u/g、n/g为开口/竖笔组合，留出稍多呼吸。
spacing=[3.9,4.5,4.0,3.9,4.5,4.2]
positions=[]; x=0
for i,ch in enumerate('dougong'):
    positions.append((ch,x))
    x+=30+(spacing[i] if i<6 else 0)
word_width=x
scale=.86; word_x=60; word_y=10.2
badge_x=word_x+word_width*scale+12
badge_w=44;badge_h=22
width=round(badge_x+badge_w+10)

def badge_outline():
    font=TTFont('C:/Windows/Fonts/consola.ttf'); glyphset=font.getGlyphSet(); cmap=font.getBestCmap(); units=font['head'].unitsPerEm
    pen=SVGPathPen(glyphset); bounds=BoundsPen(glyphset); x=0; s=17/units
    for ch in 'NET':
        name=cmap[ord(ch)]; transform=(s,0,0,-s,x,0)
        glyphset[name].draw(TransformPen(pen,transform));glyphset[name].draw(TransformPen(bounds,transform));x+=font['hmtx'][name][0]*s
    return pen.getCommands(),bounds.bounds
badge,bounds=badge_outline()
bx=badge_x+(badge_w-(bounds[2]-bounds[0]))/2-bounds[0]
by=36-(bounds[1]+bounds[3])/2
def word(fg):
    return f'<g fill="{fg}" fill-rule="evenodd" transform="translate({word_x} {word_y}) scale({scale})">'+''.join(f'<path data-letter="{ch}" transform="translate({pos:.3f} 0)" d="{glyphs[ch]}"/>' for ch,pos in positions)+'</g>'
def svg(fg,bg):
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="72" viewBox="0 0 {width} 72" role="img" aria-labelledby="title"><title id="title">dougong NET — custom wordmark</title><g fill="{fg}" transform="translate(10 19) scale({34/176}) translate(-40 -40)">'+''.join(f'<path d="{d}"/>' for d in symbol_paths)+'</g>'+word(fg)+f'<rect x="{badge_x:.4f}" y="25" width="{badge_w}" height="{badge_h}" rx="2.3" fill="{fg}"/><path d="{badge}" transform="translate({bx:.4f} {by:.4f})" fill="{bg}"/></svg>'
black=svg('#121212','#fff');white=svg('#fff','#121212')
(root/'dougong-net.svg').write_text(black,encoding='utf-8');(root/'dougong-net-white.svg').write_text(white,encoding='utf-8')
def uri(v):return 'data:image/svg+xml;base64,'+base64.b64encode(v.encode()).decode()
html=(root/'presentation-template.html').read_text(encoding='utf-8')
html=html.replace('__BLACK__',uri(black)).replace('__WHITE__',uri(white))
for ch in ['d','o','g']:
    detail=f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="-8 -5 46 67" role="img" aria-label="{ch}字形细节"><path fill="#121212" fill-rule="evenodd" d="{glyphs[ch]}"/></svg>'
    html=html.replace('__GLYPH_'+ch.upper()+'__',detail)
(root/'preview.html').write_text(html,encoding='utf-8')
(root/'metrics.json').write_text(json.dumps({'size':[width,72],'wordmark':'custom paths, not a font outline','glyphs':list(glyphs),'stroke':6,'x_height':30,'round_overshoot':.4,'spacing':spacing,'symbol_visible':34,'badge':[badge_w,badge_h]},indent=2),encoding='utf-8')
(root/'wordmark.svg').write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="57 7 {word_width*scale+6:.3f} 56" width="{word_width*scale+6:.3f}" height="56"><title>dougong custom wordmark</title>{word("#121212")}</svg>',encoding='utf-8')
print('Built custom wordmark:',width,'x 72')
