# Dougong Desktop 原型共同规格

日期：2026-10-30。阶段：方向初稿，尚未选择，不是已实现的桌面应用。

## 来源
已阅读用户指定的三个桌面端文件：
- C:/myObsidian/Haihao/projects/dougong/Dougong-Desktop-实施方案.md
- C:/myObsidian/Haihao/projects/dougong/Dougong-Desktop-开发计划.md
- C:/myObsidian/Haihao/projects/dougong/Dougong-Desktop-Slot机制规格.md

最新 Slot 规格优先：Single / List / Keyed 三种；不展示虚构的第四种 chain 插槽。保留 conversation。
开发计划与实施方案对 reasoning 存在矛盾，方向初稿不展示实时推理。后续按 SDK 实际能力核实。

## 受众、场景、输出
面向在本地工作区使用 Agent 的开发者；长时间阅读、检查工具调用、审批、管理扩展。
每方向一个可离线打开的单文件 HTML，主评审尺寸 1440×900，兼容 1280×800；375 宽提供可用的精简视图。
这是桌面界面设计，不做营销落地页。三个方向同一演示任务：梳理 Dougong Desktop 的项目结构。
所有会话、计时、工具结果、用量、插件数据均为明确标示的原型演示数据，不连接模型、不执行命令、不读取真实密钥。

## 功能与信息架构
- 三栏必须保留：工作区及会话树 / 对话 / 可收起的右栏；提供侧栏折叠。
- 主消息：用户右对齐、Agent 左对齐；工具输入输出折叠；计划按事件内容呈现。
- 待审批 read_file，目标 src/DougongDesktop.Framework/SessionComposer.cs。四个决策：允许一次、本会话允许、拒绝、拒绝并停止；待审批时输入禁用并解释；答复只能生效一次。
- 工具结果未知时明确写“结果未知，请人工核实，不要重试”，与失败分开。
- 右栏：文件（只读）、改动（本场景无改动）、计划、上下文、终端（明确未实现，不提供伪运行）。
- 状态胶囊：在侧栏底部，含状态与 0.1 秒计时，可展开步骤；等待审批冻结计时；未开始显示 —。
- 榫灵嵌在窗内，可通过设置隐藏；减少动态效果。不取代状态胶囊。
- 插件管理是主内容视图：Agent 插件 / UI 插件；已装列表、状态、启停、本地目录装载信任确认、卸载确认、故障隔离提示；原型操作明确为模拟。
- 设置是主内容视图：用户定义提供商、名称、协议、Base URL、模型、最大上下文、最大输出、凭据来源、模拟测试连接；运行回合中不可切换提供商。
- Ctrl+K / Cmd+K 命令面板，含新建会话、切换会话、插件管理、设置。Escape 关闭。
- 无插件市场、MCP、外部 Agent、多 Agent 编排、真实终端、会话分支与导出。

## 真实资产
产品标志使用 C:/work/myProject/dougong/assets/brand/svg/symbol.svg（暗色用 symbol-white.svg）。
榫灵使用 C:/work/myProject/dougong/artifacts/brand-character/sunling-pet-colored/svg/sunling-*.svg。
HTML 图片均使用 data URI 内嵌；不得重画品牌 Logo。Logo 的黑白偏好只约束 Logo，不强行限制整套界面。

## 五问及假设
叙事角色：真正可操作的工作台主界面。观众距离：桌面近距阅读。视觉温度：每方向独立。容量：三栏高密度，但主对话留呼吸空间。母题：工具执行证据与人类授权同屏。
假设：使用中文界面、Windows 标题栏；桌面工程当前没有 UI 设计系统可继承，采用已有品牌资产。
先提供三个方向及六候选总览；用户选定后才拓展完整页面与状态规范。
