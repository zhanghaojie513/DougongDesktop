# Dougong Desktop · 清晰工作台

用户选定 A 方向后的交互原型，2026-10-30。

## 打开
直接打开 `index.html`，资源全部内嵌，可离线使用。开发预览地址：http://127.0.0.1:8766/index.html。

## 体验路径
- 初始任务等待文件读取授权：四档审批，等待时锁定输入，答复后不可重复授权。
- 允许读取后检查工具结果和完成的计划；拒绝后保留未完成的步骤。
- 点击输入框模型旁的环形用量按钮，展开上下文详情；点击外部或 Esc 关闭。右栏没有独立上下文面板。
- 侧栏切换工作区会话，切换插件/设置后消息仍保留；新建会话绑定默认提供商。
- 右栏四项：文件、改动、计划、终端。文件为说明性只读预览，终端明确未实现。
- 插件页切换 Agent / UI 分类、搜索、启停；详情中可以卸载非内置插件；本地装载需先勾选信任。
- 设置中新增、编辑、删除未使用的提供商；检查最大上下文与最大输出；连接测试为模拟。
- 布局与陪伴：调整阅读字号、密度，隐藏榫灵，减少动态效果；拖动列分隔线调整宽度。
- Ctrl+K / Cmd+K 命令面板；Ctrl+N 新建会话；Esc 关闭浮层。
- 右侧最下方的信息图标打开“原型状态评审”，可查看过期、未知结果、工具失败、引擎断开、压缩、首次引导、会话恢复等状态。

## 页面链接
- `index.html?view=plugins` 插件管理
- `index.html?view=settings` 模型与提供商
- `index.html?view=settings&tab=appearance` 布局与陪伴
- `index.html?view=settings&tab=diagnostics` 本地诊断
- `index.html?view=onboarding` 首次配置
- `index.html?view=recovery` 会话恢复
- `index.html?scenario=unknown` 结果未知
- `index.html?scenario=disconnected` 引擎断开
- `index.html?scenario=compacted` 有损压缩

## 交付边界
这是 HTML 设计稿，不是 Avalonia 实现。全部工具、插件、连接、用量和凭据状态为模拟；不访问实际工作区，不读写密钥，不发送模型请求。
会话及配置演示保存在页面内存中，刷新恢复；只有布局、阅读与陪伴偏好存储在浏览器本地。
原始三方向保留于 `../design-demos/`，本轮用户修正与信息架构见 `design-spec.md`。

## 源文件
`app.js` 负责交互，`extra.css` 负责深化样式，`build.py` 将原始 A 外壳、深化代码及真实品牌资产内嵌为单文件 `index.html`。
`verify.py` 检查交互与多种窗口尺寸，`verification.json` 记录结果，`screens/` 为真实浏览器截图。
