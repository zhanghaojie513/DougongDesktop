from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.transformPen import TransformPen
import base64, re, json

root=Path(__file__).parent
symbol=(root.parent/'svg/symbol.svg').read_text(encoding='utf-8')
symbol_paths=re.findall(r'<path[^>]*d="([^"]+)"',symbol)

def outlines(text,font_path,size):
    font=TTFont(font_path)
    glyphs=font.getGlyphSet(); cmap=font.getBestCmap(); scale=size/font['head'].unitsPerEm
    pen=SVGPathPen(glyphs); bounds=BoundsPen(glyphs); x=0
    for ch in text:
        glyph=cmap[ord(ch)]
        transform=(scale,0,0,-scale,x,0)
        glyphs[glyph].draw(TransformPen(pen,transform))
        glyphs[glyph].draw(TransformPen(bounds,transform))
        x+=font['hmtx'][glyph][0]*scale
    return pen.getCommands(),bounds.bounds,x

word,bounds,advance=outlines('dougong','C:/Windows/Fonts/seguisb.ttf',39)
badge,bb,_=outlines('NET','C:/Windows/Fonts/consola.ttf',18)
word_x=62-bounds[0]
word_y=32-(bounds[1]+bounds[3])/2
word_end=word_x+bounds[2]
badge_x=word_end+15
badge_w=48;badge_h=24
badge_text_x=badge_x+(badge_w-(bb[2]-bb[0]))/2-bb[0]
badge_text_y=32-(bb[1]+bb[3])/2
width=round(badge_x+badge_w+10)
def svg(fg,bg):
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="64" viewBox="0 0 {width} 64" role="img" aria-labelledby="title"><title id="title">dougong NET</title><g fill="{fg}" transform="translate(10 14) scale({36/176}) translate(-40 -40)">{''.join('<path d="'+d+'"/>' for d in symbol_paths)}</g><path fill="{fg}" transform="translate({word_x:.4f} {word_y:.4f})" d="{word}"/><rect x="{badge_x:.4f}" y="20" width="48" height="24" rx="2.5" fill="{fg}"/><path fill="{bg}" transform="translate({badge_text_x:.4f} {badge_text_y:.4f})" d="{badge}"/></svg>'''

black=svg('#111315','#ffffff'); white=svg('#ffffff','#111315')
(root/'dougong-net.svg').write_text(black,encoding='utf-8')
(root/'dougong-net-white.svg').write_text(white,encoding='utf-8')
def uri(s):return 'data:image/svg+xml;base64,'+base64.b64encode(s.encode()).decode()
html='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>dougong NET — 品牌组合</title><style>
*{box-sizing:border-box}body{margin:0;background:#edf0f2;color:#262a30;font:14px/1.6 'Segoe UI','Microsoft YaHei',sans-serif;padding:34px}main{max-width:1000px;margin:auto}header{display:flex;justify-content:space-between;align-items:center;margin:0 0 22px}h1{margin:0;font-size:22px;letter-spacing:-.5px;font-weight:600}header span{font-size:12px;color:#606975}.primary{height:270px;background:#f8f9fa;border:1px solid #dfe3e7;border-radius:10px;display:flex;align-items:center;justify-content:center}.primary img{width:600px;max-width:85%;height:auto}.pair{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin-top:16px}.sample{background:white;border:1px solid #dfe3e7;border-radius:9px;padding:19px 22px}.sample.dark{background:#111315;border-color:#111315;color:#fff}.sample p{margin:0 0 13px;font-size:12px;color:#606975}.sample.dark p{color:#bdc3cb}.sample img{width:300px;max-width:100%;display:block}.app{margin-top:20px;border:1px solid #d5dbe1;border-radius:9px;overflow:hidden;background:white}.appbar{height:40px;padding:0 16px;display:flex;justify-content:space-between;align-items:center;background:#f8f9fa;border-bottom:1px solid #e4e7eb;font-size:12px;color:#606975}.appcontent{display:grid;grid-template-columns:280px 1fr;min-height:150px}.brand{padding:24px 18px;background:#f5f6f8;border-right:1px solid #e4e7eb}.brand img{width:238px;max-width:100%;display:block}.appnote{padding:28px 30px}.appnote strong{font-size:16px;display:block;margin-bottom:7px}.appnote p{margin:0;color:#606975}footer{display:flex;justify-content:space-between;gap:14px;margin-top:20px;color:#606975;font-size:12px}a{color:#3e536b;text-underline-offset:4px}@media(max-width:650px){body{padding:18px}.pair{grid-template-columns:1fr}.primary{height:190px}.appcontent{grid-template-columns:1fr}.brand{border-right:0;border-bottom:1px solid #e4e7eb}.appnote{padding:20px}footer{display:block}}
</style><main><header><h1>dougong NET</h1><span>黑白品牌组合 / 2026.10.31</span></header><section class="primary"><img src="__BLACK__" alt="dougong NET 主组合"></section><div class="pair"><section class="sample"><p>浅色界面</p><img src="__BLACK__" alt="黑色组合"></section><section class="sample dark"><p>深色界面</p><img src="__WHITE__" alt="白色组合"></section></div><section class="app"><div class="appbar"><span>Dougong Desktop</span><span>—　□　×</span></div><div class="appcontent"><div class="brand"><img src="__BLACK__" alt="侧栏实际尺寸"></div><div class="appnote"><strong>合构图标，小写字标，NET 徽标。</strong><p>沿用已有产品图标，组合结构参考你提供的 dsh 截图。<br>字标已转为矢量轮廓，小尺寸使用不依赖本机字体。</p></div></div></section><footer><span>NET 按你的命名使用大写，不加句点。</span><span><a href="dougong-net.svg">SVG</a>　<a href="dougong-net.png">透明 PNG</a>　<a href="dougong-net-white.svg">反白 SVG</a></span></footer></main></html>'''
(root/'preview.html').write_text(html.replace('__BLACK__',uri(black)).replace('__WHITE__',uri(white)),encoding='utf-8')
(root/'metrics.json').write_text(json.dumps({'size':[width,64],'symbol_visible':[36,36],'badge':[48,24],'wordmark':'outlined Segoe UI Semibold','badge_text':'outlined Consolas','text':'dougong NET'},indent=2),encoding='utf-8')
print('Built lockup',width,64)
