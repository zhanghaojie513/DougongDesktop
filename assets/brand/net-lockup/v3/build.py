from pathlib import Path
import re,base64,json

root=Path(__file__).parent
symbol=(root.parents[1]/'svg/symbol.svg').read_text(encoding='utf-8')
symbol_paths=re.findall(r'<path[^>]*d="([^"]+)"',symbol)
# 比例字宽；cubic轮廓，避免上一轮所有字母等宽、平方圆角的机械感。
glyphs={
 'd':('M22.2 2.2L24.4 0H27V46H13.5C4.8 46 0 40.2 0 30C0 19.8 4.8 14 13.5 14C17.2 14 20.1 15.2 22.2 17.6Z M13.5 18.8C7.6 18.8 4.8 22.6 4.8 30C4.8 37.4 7.6 41.2 13.5 41.2C19.4 41.2 22.2 37.4 22.2 30C22.2 22.6 19.4 18.8 13.5 18.8Z',27),
 'o':('M13.5 13.6C22.2 13.6 27 19.4 27 30C27 40.6 22.2 46.4 13.5 46.4C4.8 46.4 0 40.6 0 30C0 19.4 4.8 13.6 13.5 13.6Z M13.5 18.4C7.6 18.4 4.8 22.3 4.8 30C4.8 37.7 7.6 41.6 13.5 41.6C19.4 41.6 22.2 37.7 22.2 30C22.2 22.3 19.4 18.4 13.5 18.4Z',27),
 'u':('M0 14H4.8V32.5C4.8 38.7 7.5 41.6 13.2 41.6C18.9 41.6 21.6 38.7 21.6 32.5V14H26.4V32.5C26.4 41.8 22 46.4 13.2 46.4C4.4 46.4 0 41.8 0 32.5Z',26.4),
 'g':('M25.8 20.1L22.4 23.5C20.4 20.1 17.9 18.6 13.7 18.6C7.7 18.6 4.8 22.5 4.8 30C4.8 37.5 7.7 41.4 13.7 41.4C19.7 41.4 22.6 37.5 22.6 30V27H27.4V43.8C27.4 52.2 22.7 56.2 13.6 56.2H3.4L8.2 51.4H13.6C19.7 51.4 22.6 49.1 22.6 43.8V42.2C20.3 44.9 17.3 46.2 13.5 46.2C4.7 46.2 0 40.2 0 30C0 19.8 4.7 13.8 13.5 13.8C18.8 13.8 22.8 15.9 25.8 20.1Z',27.4),
 'n':('M0 46V27.7C0 18.4 4.6 13.6 13.4 13.6C22.2 13.6 26.8 18.4 26.8 27.7V46H22V27.7C22 21.5 19.1 18.4 13.4 18.4C7.7 18.4 4.8 21.5 4.8 27.7V46Z',26.8),
}
spacing=[3.1,3.5,3.6,3.1,3.5,3.7]
x=0;positions=[]
for i,ch in enumerate('dougong'):
 positions.append((ch,x));x+=glyphs[ch][1]+(spacing[i] if i<6 else 0)
word_width=x;scale=.86;word_x=55;word_y=10.2
badge_x=word_x+word_width*scale+12;badge_w=46
width=round(badge_x+badge_w+10)
def word(fg):return f'<g fill="{fg}" fill-rule="evenodd" transform="translate({word_x} {word_y}) scale({scale})">'+''.join(f'<path data-letter="{ch}" transform="translate({pos:.3f} 0)" d="{glyphs[ch][0]}"/>' for ch,pos in positions)+'</g>'
net=[('M0 14V0H2.2L9 10.3V0H11.2V14H9L2.2 3.7V14Z',0),('M0 0H10V2H2V6H9V8H2V12H10V14H0Z',13.2),('M0 0H12V2H7V14H5V2H0Z',25.2)]
net_scale=.88;net_width=37.2*net_scale
def svg(fg,bg):
 return f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="72" viewBox="0 0 {width} 72" role="img" aria-labelledby="title"><title id="title">dougong NET — refined custom wordmark</title><g fill="{fg}" transform="translate(10 20) scale({32/176}) translate(-40 -40)">'+''.join(f'<path d="{d}"/>' for d in symbol_paths)+'</g>'+word(fg)+f'<rect x="{badge_x:.4f}" y="25" width="46" height="22" rx="2.2" fill="{fg}"/><g fill="{bg}" transform="translate({badge_x+(badge_w-net_width)/2:.4f} {36-14*net_scale/2:.4f}) scale({net_scale})">'+''.join(f'<path transform="translate({pos} 0)" d="{d}"/>' for d,pos in net)+'</g></svg>'
black=svg('#121212','#fff');white=svg('#fff','#121212')
(root/'dougong-net.svg').write_text(black,encoding='utf-8');(root/'dougong-net-white.svg').write_text(white,encoding='utf-8')
(root/'wordmark.svg').write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="52 7 {word_width*scale+6:.3f} 56" width="{word_width*scale+6:.3f}" height="56"><title>dougong custom wordmark</title>{word("#121212")}</svg>',encoding='utf-8')
def uri(s):return 'data:image/svg+xml;base64,'+base64.b64encode(s.encode()).decode()
html=(root.parent/'preview.html').read_text(encoding='utf-8')
assets=iter([black,black,white,black])
html=re.sub(r'src="data:image/svg\+xml;base64,[^"]+"',lambda m:'src="'+uri(next(assets))+'"',html)
html=html.replace('黑白品牌组合 / 2026.10.31','字标精修 / 第三版').replace('合构图标，小写字标，NET 徽标。','更紧凑的比例，更清楚的字形。').replace('沿用已有产品图标，组合结构参考你提供的 dsh 截图。<br>字标已转为矢量轮廓，小尺寸使用不依赖本机字体。','收紧字宽与字距，重绘曲线，减轻图标的视觉重量。<br>开口 g 和短回钩作为字标记忆点，NET 也改为定制路径。')
html=html.replace('background:#111315','background:#121212')
(root/'preview.html').write_text(html,encoding='utf-8')
(root/'metrics.json').write_text(json.dumps({'size':[width,72],'wordmark':'custom cubic outlines','glyph_widths':{ch:glyphs[ch][1] for ch in glyphs},'stroke':4.8,'x_height':32,'spacing':spacing,'symbol_visible':32,'badge_size':[46,22]},indent=2),encoding='utf-8')
# 用于直接看字标的干净展示，不附设计说明。
(root/'hero.html').write_text('<!doctype html><meta charset="utf-8"><style>*{box-sizing:border-box}body{margin:0;background:#fafafa;width:1000px;height:240px;display:flex;align-items:center;justify-content:center}img{width:650px;height:auto}</style><img alt="dougong NET" src="'+uri(black)+'">',encoding='utf-8')
print('Built refinement:',width,'x 72')
