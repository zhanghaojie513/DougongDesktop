# dougong NET / V2 设计深化

本目录是用户明确选定的唯一工作版本。当前按用户明确要求使用大写 DOUGONG，保留V2圆角骨架，设计D的内切角与G的开口横梁，交付完整组合。

## 交付
- preview.html / preview.png：完整设计稿，包含浅色、反白、d/o/g细节及桌面侧栏使用示例。
- dougong-net.svg / dougong-net-white.svg：黑白及反白矢量组合。
- dougong-net.png / dougong-net-white.png：4倍透明PNG，1336×288。
- wordmark.svg：独立字标矢量，仅作为组合的支持资产。

组合基准334×72；图标可见34×34，NET徽标44×22。建议完整组合显示宽度不低于220px，更小使用原合构图标。
不拉伸、改字距、单独放大NET；深色背景使用反白版。SVG均为路径，不依赖字体。

## 构建
在项目目录运行：
python assets/brand/net-lockup/v2/build.py
python assets/brand/net-lockup/v2/render.py

presentation-template.html是当前完整稿模板，构建不依赖其他探索版本。默认使用项目既有symbol.svg，不重新绘制图标。source-before-lettering.py仅保存字形深化前的源代码，作为同一方向的可恢复记录。
