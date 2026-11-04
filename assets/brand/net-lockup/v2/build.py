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

# 明确使用大写DOUGONG。保留V2圆角与接合切角，cap-height=44，笔画6.6。
glyphs={
 'D':'M0 0H15.5C28 0 34 7.6 34 22C34 36.4 28 44 15.5 44H0Z M10.6 6.6L6.6 10.6V37.4H15.5C23.8 37.4 27.4 32.3 27.4 22C27.4 11.7 23.8 6.6 15.5 6.6Z',
 'O':'M16 -.4H19C30.2 -.4 35 7 35 20V24C35 37 30.2 44.4 19 44.4H16C4.8 44.4 0 37 0 24V20C0 7 4.8 -.4 16 -.4Z M16 6.2C9 6.2 6.6 10.7 6.6 20V24C6.6 33.3 9 37.8 16 37.8H19C26 37.8 28.4 33.3 28.4 24V20C28.4 10.7 26 6.2 19 6.2Z',
 'U':'M0 0H6.6V28.2C6.6 35 10 37.8 17 37.8C24 37.8 27.4 35 27.4 28.2V0H34V28.2C34 39 28.5 44.4 17 44.4C5.5 44.4 0 39 0 28.2Z',
 'G':'M33.3 7.6L28.3 12.6C26.1 8.2 23 6.2 17 6.2C9.5 6.2 6.6 11 6.6 21.9C6.6 32.8 9.5 37.8 17.3 37.8C21.3 37.8 24.7 37 27.4 35.1V28.1H18.5V21.5H34V38.5C29 42.6 24 44.4 17.3 44.4C5.2 44.4 0 36.7 0 21.9C0 7.1 5.2 -.4 17 -.4C25 -.4 30 2.4 33.3 7.6Z',
 'N':'M0 44V0H6.6L28.4 31.8V0H35V44H28.4L6.6 12.2V44Z',
}
glyph_widths={'D':34,'O':35,'U':34,'G':34,'N':35}
spacing=[4.2,4.4,4.5,4.1,4.5,4.4]
positions=[]; x=0
for i,ch in enumerate('DOUGONG'):
    positions.append((ch,x))
    x+=glyph_widths[ch]+(spacing[i] if i<6 else 0)
word_width=x
scale=.78; word_x=60; word_y=18.84
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
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="72" viewBox="0 0 {width} 72" role="img" aria-labelledby="title"><title id="title">DOUGONG NET — custom uppercase wordmark</title><g fill="{fg}" transform="translate(10 19) scale({34/176}) translate(-40 -40)">'+''.join(f'<path d="{d}"/>' for d in symbol_paths)+'</g>'+word(fg)+f'<rect x="{badge_x:.4f}" y="25" width="{badge_w}" height="{badge_h}" rx="2.3" fill="{fg}"/><path d="{badge}" transform="translate({bx:.4f} {by:.4f})" fill="{bg}"/></svg>'
black=svg('#121212','#fff');white=svg('#fff','#121212')
(root/'dougong-net.svg').write_text(black,encoding='utf-8');(root/'dougong-net-white.svg').write_text(white,encoding='utf-8')
def uri(v):return 'data:image/svg+xml;base64,'+base64.b64encode(v.encode()).decode()
html=(root/'presentation-template.html').read_text(encoding='utf-8')
html=html.replace('__BLACK__',uri(black)).replace('__WHITE__',uri(white))
for ch in ['D','O','G']:
    detail=f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="-7 -5 49 54" role="img" aria-label="{ch}字形细节"><path fill="#121212" fill-rule="evenodd" d="{glyphs[ch]}"/></svg>'
    html=html.replace('__GLYPH_'+ch.upper()+'__',detail)
(root/'preview.html').write_text(html,encoding='utf-8')
(root/'metrics.json').write_text(json.dumps({'size':[width,72],'text':'DOUGONG','wordmark':'custom uppercase paths, not a font outline','glyphs':list(glyphs),'stroke':6.6,'cap_height':44,'round_overshoot':.4,'spacing':spacing,'symbol_visible':34,'badge':[badge_w,badge_h]},indent=2),encoding='utf-8')
(root/'wordmark.svg').write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="57 15 {word_width*scale+6:.3f} 42" width="{word_width*scale+6:.3f}" height="42"><title>DOUGONG custom uppercase wordmark</title>{word("#121212")}</svg>',encoding='utf-8')
print('Built custom wordmark:',width,'x 72')
