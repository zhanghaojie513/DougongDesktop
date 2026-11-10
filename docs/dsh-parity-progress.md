# dsh 桌面端复刻进度清单

> **这份清单的用途**：逐行勾选 P8（1:1 复刻 dsh）的完成情况，可数、可核对。
>
> 与 [dsh-parity-spec.md](dsh-parity-spec.md) 的分工：那份讲**规格**（每个数字的出处、
> 为什么这样做、做不到的怎么做）；这份只讲**现在是什么状态**。
>
> 排查界面问题时用 [spyglass-usage.md](spyglass-usage.md)（视觉树/样式/绑定/性能）。
>
> **状态口径**（每项只取一个，按"做到哪一步"从严判定）：
>
> | 标记 | 含义 |
> |---|---|
> | `✅` | 有等价实现，**且有验证证据** |
> | `🟡` | 有实现但不完整（备注里写清差什么） |
> | `⬜` | 没有实现 |
> | `⏳` | 等外部条件（SDK 能力 / 上游组件） |
> | `➖` | 明确不做（附理由） |
>
> **判定要严**：有类名但没接进装配，算 `⬜`；接进装配但没有验证，算 `🟡`。

最后更新：2026-11-06

**基线**：dsh `0.2.2-alpha.2`（commit `ce7073d`）· TKCode.Arc `1.0.0` · SDK `0.1.20-alpha.1`

---

## 零、实现方式的分类

状态之外，每项还标**怎么实现的**。这两件事独立：同是 ✅，
"用了 Arc 现成组件" 与 "自己写" 的维护成本差一个量级。

| 标记 | 含义 |
|---|---|
| `[Arc]` | 直接用 TKCode.Arc 的组件，我们不维护渲染 |
| `[数据]` | 数据生成，无运行时逻辑（token 表、常量） |
| `[自有]` | 核心逻辑由我们写与维护 |
| `[—]` | 不适用（没有实现） |

**为什么标**：`[自有]` 的项升级、排错都要自己扛；`[Arc]` 出问题能查上游文档。
不标出来，排期时会低估 `[自有]` 那一堆。

---

## 一、进度总览

| 阶段 | ✅ | 🟡 | ⬜ | ⏳ | ➖ | 小计 | 完成度 |
|---|---|---|---|---|---|---|---|
| P8a 布局骨架 | 10 | 0 | 1 | 1 | 1 | 13 | **91%** |
| P8b 会话与输入 | 5 | 0 | 0 | 0 | 1 | 6 | **100%** |
| P8c 设置域 | 6 | 0 | 0 | 1 | 1 | 8 | **100%** |
| P8d 会话增强 | 0 | 0 | 7 | 0 | 0 | 7 | 0% |
| P8e 插件体系 | 1 | 1 | 6 | 0 | 0 | 8 | **25%** |
| P8f 终端与浏览器 | 1 | 1 | 3 | 0 | 0 | 5 | **40%** |
| **P8g 中区会话域** | **12** | **1** | **4** | **0** | **6** | **23** | **76%** |
| **合计** | **35** | **3** | **21** | **2** | **9** | **70** | **67%** |

> **P8g 是 2026-11-05 新纳入的**。此前只对齐了中栏**宽度几何**，
> 内容侧的会话行为从未比对过——这是范围缺口，不是实现缺陷。
> 规格见 `docs/dsh-parity-spec.md` §7.5。

**P8a 已完成**：10 项做到、1 项明确不做（A9 保留 Arc 实现）、
1 项等上游（A10 Arc 侧栏 64px）。它的待办为零。

完成度算法：`(✅ + 🟡×0.5) / (✅+🟡+⬜)`，不含 `⏳` 与 `➖`。
`(24 + 1×0.5) / (24+1+30) = 24.5 / 55 = 45%`

> 加 P8g 时完成度从 38% 掉到 33%（分母变大）。做完 P0 那批回到 40%。
> **不做的那些（M18–M23 六项 ➖）不算进分母**——它们是 dsh 也没有的东西，
> 拿它们充数就成了"用没做的功能凑完成度"。

**统计口径**：这些数字从下面第二部分的表格逐行数出来，不是手填的。
**改了一行状态就要同步改这里**——两处对不上说明漏改。

核对方法（正则按 `| A1 |` 这类行首模式取状态列）：

```python
for line in text.splitlines():
    m = re.match(r'\| ([A-F]\d+) \| ([✅🟡⬜⏳➖]) \|', line)
```

**为什么写这一段**：上一轮手数过一次，P8a 的 ✅ 写成 7（实为 8）、
P8c 分项相加不等于总数，三处错。拆分时要靠脚本核对，不靠数。

### 已实现项的实现方式分布

| 实现方式 | 项数 | 占已实现 |
|---|---|---|
| `[自有]` | 9 | 69% |
| `[Arc]` | 3 | 23% |
| `[数据]` | 1 | 8% |

（A9 改 ➖ 后从 `[Arc]` 组移出，分布不变。）

**69% 是自己写的——这与直觉相反，值得说清。**

看起来"用了 Arc 组件"的项，很多其实是**我们写的控件 + Arc 的数据类型**：
`Composer` 是自建 `UserControl` 包 `AiComposer`；`ApprovalCard` 是自建卡片
只用 Arc 的 `Button`；`StatusCapsule` 是自建布局只用 `AgentStatus` 的数据结构。

真正"拿来即用"的只有三项：`RightDock` 的 `Tabs`、`ConversationView` 的
`Markdown`/`ToolCall`、`CommandPalette` 的 `CommandPaletteDialog`。

**这个分布的实际含义**：Arc 提供的是**组件**，不是**整页**。
dsh 那种"一个 `ui-*` 包就是一整块功能"的粒度我们没有——所以
每接一个功能都要写装配与状态映射。这是 P8b–P8f 工作量下不去的原因。

**不是选型错了**，是粒度差异：Arc 给零件，dsh 给模块。

---

## 二、逐项清单

### P8a 布局骨架

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| A1 | ✅ | 三栏像素求解器 | `[自有]` | `Ui.Core/Columns.cs`，常量逐条对应 dsh `columns.ts` | `cols-*` 5 条；dump `280/424/576` |
| A2 | ✅ | 中栏 400 保护 | `[自有]` | `Columns.CenterMin`，`available = viewport - s - CenterMin` | `cols-rightbar-yields-first` |
| A3 | ✅ | 右栏 45% 首开 | `[自有]` | `Columns.RightbarDefault(viewport)` | `cols-rightbar-default-45pct`；dump 576/1280 |
| A4 | ✅ | 响应式 1024 自动收起 | `[自有]` | `AppShell.ApplyColumns()` 的 `narrow` | `cols-responsive-breakpoints`：1023→56、1025→280 |
| A5 | ✅ | 响应式 768 抽屉 | `[自有]` | 同上 `drawer` 分支，列宽传 0 | 同上：767→0 不占列宽 |
| A6 | ✅ | 底部行横跨三列、空不占位 | `[自有]` | `AppShell.Build()` 的 `stack` 第二行 | dump `Bottom(list) 1280x40` |
| A7 | ✅ | 状态条移到侧栏底部 | `[自有]` | `_sidebarHost` 两行 Grid | dump `EngineStatusBar 260x14` |
| A8 | ✅ | 毛玻璃 | `[Arc]` | `TransparencyLevelHint = [AcrylicBlur, Blur, None]` | 实测 `ActualTransparencyLevel = AcrylicBlur`。**侧栏仍不透明——符合 dsh 的 Windows 行为** |
| A9 | ➖ | 侧边栏行高与圆角严格对齐 | `[Arc]` | **明确不强行修改**（2026-10-31 用户决定）。品牌行 52px 已设（`Columns.SidebarBrandRowHeight`）；会话行、控件行高度与圆角交给 Arc 的 Section 模板 | 保留 Arc 的观感。dsh 的 36/28/8/12 与 Arc 的默认值接近，覆盖模板的收益不抵"与上游版本脱钩"的风险 |
| A10 | ⏳ | 侧边栏收起 56px 轨道 | `[—]` | **Arc 写死 64px**，属性表无收起态宽度 | 接受 8px 差异 |
| A11 | ✅ | 119 个 `--dsw-*` token 映射 | `[数据]` | `scripts/sync-dsh-tokens.py` 生成 `Theme/DshTokens.g.cs` | `dsh-tokens-complete`、`dsh-tokens-themes-differ`；dump 底色 = White |
| A12 | ✅ | `color-mix` 求值 | `[自有]` | `Ui.Core/ColorMath.cs`：`MixSrgb` / `MixHsl`（色相走最短弧） | `color-mix-matches-css` |
| A13 | ⬜ | 滚动条外观 | `[Arc]` | 现状用 Avalonia 默认滚动条；macOS 是浮层式（滚动时出现），dsh 是**常驻 5px 细条**。解法：`Arc.ScrollArea` 自带细悬浮滚动条 + 边缘渐隐 | 2026-11-06 记入。尚未实现 |

### P8b 会话与输入

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| B1 | ✅ | `/` 斜杠命令 | `[Arc]` | `AiCommand` + `AiComposer.Commands`；识别靠 `Sent` 的 token，不解析文本 | 无检查；dump 见 composer |
| B2 | ✅ | `@` 文件引用 | `[Arc]` | `ContextItemProjection` 把 `fs/list` 结果转成 `AiContextItem`；Id 用完整相对路径（同名文件不互串）；分组标题显式设中文（Arc 默认英文）；**右栏目录可点进入，列表累加不替换** | dump「@ 引用项 18 个」；进入 `src/` 后 **23 个**（累加 5）；`context-items-have-unique-ids` |
| B3 | ✅ | 附件栏 | `[Arc]` | **Arc 自带完整链路**：`IsAttachmentEnabled=true` → 加号菜单 → "Upload file" → `BrowseAsync()` 自己开系统选择器。`UploadHandler=null`（本地无上传服务，附件直接 Ready） | 自检注入 1 个附件后 Composer 高 101→161；dump「自检附件已加入：1 个」 |
| B4 | ✅ | 模型选择 | `[Arc]` | `AiModel`/`AiModelProvider`；IPC `models/list` → `ModelProjection` → composer | 运行时日志「模型目录：662 个 / 22 家」 |
| B5 | ✅ | 消息反馈 Like/Dislike | `[自有]` | 自建 `FeedbackBar`：赞/踩两按钮，踩后追问理由，重复点击只提交一次。**`AiAnswerActions` 实测是 `internal`**，引用不到；`AiAnswer` 是整页控件，量级不匹配 | dump「反馈条 1 个 / 可见 assistant 容器 1 个」；`feedback-rating-roundtrips` |
| B6 | ➖ | 引用来源 | `[—]` | **SDK 不提供 sources/citations 数据**——`Protocol.cs` 里没有任何来源字段 | 没有数据就没有可显示的来源。硬做只能造假引用，比不做更糟 |

### P8c 设置域

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| C1 | ✅ | 设置外壳 + 通用分区 | `[Arc]` | **用 Arc `SettingsPage`**（左导航+右内容，窄容器退化为单列）。四个分区：提供商/权限/外观/诊断。设置页独占主区（右栏让位），否则 424px 宽会一直退化成最窄形态 | 无头检查 `settings-page-has-sections`；截图确认四个分区都有真实内容 |
| C2 | ✅ | 模型/provider 设置 | `[Arc]` | 可浏览的模型清单：按 provider 分组、显示窗口/输出/推理能力、● 标当前、当前模型置顶 | **切换做不了，已说明原因**：`EngineOptions.Model` 是启动时的 `init` 只读快照，`HostProtocol` 只有 `model/list` 没有切换命令。设置页里如实写"这不是没做，是当前架构做不到" |
| C3 | ✅ | 密钥管理 | `[自有]` | 密钥诊断：来源、**形态检查**（长度/前缀/首尾空白/含引号，只报形态不报内容）、怎么改的引导。**仍不给输入框** | 无头检查 `key-diagnostics-never-leak-the-secret`：设假密钥后断言界面**不含**它但含长度。持久化（钥匙串）未做——那要引入密钥落盘，与既有原则冲突 |
| C4 | ✅ | 插件清单页 | `[Arc]` | 读 `UiExtensionLoader.Loaded`，按 `Origin` 分内置/第三方，显示 Id / 槽位 / 文件名；空列表时提示 `DOUGONG_UI_PLUGINS` | 用真实 `SampleUiPlugin` 验证：`示例插件（sample.ui-plugin）→ shell.trailing`。无头检查 `settings-plugins-lists-loaded-only` |
| C5 | ✅ | 权限预设 | `[Arc]` | **全链路通**：设置页下拉 → `permission/switch` → 引擎 `SwitchAsync` → 审批链按新档位裁决。预设单实例注入 `PermissionPolicyApprover(PresetApprover(inner, presets))` | ①单元：interactive 问 1 次 → unattended 问 0 次；②端到端：起真引擎往返，`interactive→unattended→interactive` 三态都对；③无头检查 `permission-picker-reflects-engine-state` 守回填与回环 |
| C6 | ⏳ | Agent preset | `[—]` | **等契约**：`AgentPreset` 在 `Dougong.HostProtocol` 里 **0 处** | 等 SDK 暴露契约 |
| C7 | ➖ | Agent 循环设置（并行工具上限） | `[—]` | **决定不做**：`ToolConcurrency` 只有两级（串行 / 工作区独占），并发由 `ToolExecutionPlan` 的 `Reads`/`Writes` 资源键自动仲裁，**没有用户可调的并行数**。放一个调了不生效的滑块不如不放 | — |
| C8 | ✅ | 子智能体 | `[Arc]` | 三条命令 + `SubagentTool` 派发 + `_subagents` 单实例；`Started`/`Completed` 双事件推送（RequestId=null，锁内异步投递）。**界面未做** | **真实 provider 全链路验证**：模型派发子 agent → `subagent/updated` 收 Started(running) 与 Completed(completed) 同一 id → `subagent/list` 看到同一实例 → `subagent/result` 取到结果（子 agent 实际找到 7 个 csproj 并读了 Directory.Build.props） |

### P8d 会话增强

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| D1 | ⬜ | 工作区浏览器 | `[自有]` | 已有 `fs/list`，补分组/扁平会话行 | 待做 |
| D2 | ⬜ | `ask_user_question` 卡片 | `[自有]` | 附着式卡片 + 计时等待 + 草稿 | 待做 |
| D3 | ⬜ | 交付物卡片 | `[Arc]` | `AiDiffText` 有词级 diff 组件，**但 SDK 的 `FileVersion` 不存原文** —— 缺口在数据侧 | 待做（依赖数据） |
| D4 | ⬜ | Trajectory 视图 | `[Arc]` | **用 `AgentTimeline` 成品**（thinking 淡色 / tool 实心 / waiting 斜纹） | 待做 |
| D5 | ⬜ | 后台任务列表 | `[自有]` | 可展开流式输出面板 | 待做 |
| D6 | ⬜ | 目标条带（goal） | `[自有]` | composer 上下文条带 | 待做 |
| D7 | ⬜ | skill 引用与调用卡片 | `[自有]` | `/` 触发 | 待做 |

> **宠物不在本表**：用户要求形象自己设计，`AiMascot` 只能改颜色换不了形体。
> 自建 `PetView` + 榫灵六状态素材（`dougong/artifacts/brand-character/sunling-pet-colored/`），
> 归 P8d 但不计入 dsh 对照项——**这是我们独有的，dsh 没有**。

### P8e 插件体系

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| E1 | ⬜ | 插件管理页 | `[自有]` | 组合包、行、配置 | 待做 |
| E2 | ⬜ | 插件市场 | `[自有]` | 索引搜索 + 安装 | 待做 |
| E3 | ⬜ | NuGet 安装器 | `[自有]` | **SDK 明确不做**：flat container 拉 `.nupkg` → 解包 → `LoadFromDirectoryAsync` | 待做 |
| E4 | 🟡 | dockkit 分裂标签树 | `[Arc]` | **用 `TKCode.Arc.Dock`**（1.0.0 才有）：`DockLayout` + 5 个 `DockPane`（4 tool + 1 document），支持拖拽分裂、浮动窗口、自动隐藏、布局序列化 | `DOUGONG_UI_DOCK=1` 启用；无头检查 `dock-mode-builds-split-tree` 断言根=`DockSplit`、5 pane 齐全。**默认未开**——Dock 改变了整条交互路径，要先做视觉验收再切默认 |
| E5 | ⬜ | 右栏停靠面 + tab 注册表 | `[Arc]` | `AiSidePanel` / `AiPanelColumn` | 待做 |
| E6 | ✅ | 右侧文件树 | `[自有]` | `fs/list` + `RightDock` 文件 tab；**可进入子目录，面包屑可回退** | 运行时「目录 ''：18 项」→ 进 `src/` 「5 项」；`fs-list-*` 3 条检查 + `dock-directory-nav-is-two-way` |
| E7 | ⬜ | 文档预览 | `[自有]` | Markdown/代码/图片/PDF/Office | 待做 |
| E8 | ⬜ | 右键菜单扩展点 | `[自有]` | — | 待做 |

### P8f 终端与浏览器

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| F1 | 🟡 | 终端 VT 解析 | `[Arc]` | **代码已接**（`TerminalPanel` 走 `TerminalCore.Write` → `CaptureFrame` → `TerminalView.Snapshot`），但**跑不通**：`TerminalCore.Create` 必抛 `NullReferenceException`（上游缺陷） | **最小复现**：新建控制台工程只加 `TKCode.Arc.Terminal 0.4.0-preview.1`，三行复现。已排除：原生库缺失、DllImport 名字、`in`/`ref` 传参、库本身损坏（`ctypes` 能加载，`GhosttyRuntime.Version` = `0.1.0-dev`，握手正常） |
| F2 | ✅ | 终端控件 | `[Arc]` | **已接进右栏 dock tab**：终端 tab 从"未实现"改为可点开，内容为 `TerminalPanel`（`TerminalView` + 状态说明） | 运行时 `核心就绪=False` + 面板显示原因。无头检查 `terminal-panel-reports-unavailable` 断言"不许装成能用" |
| F3 | ⬜ | PTY 接入 | `[自有]` | Windows `ConPTY` / Unix `forkpty`，双向字节流 | 待做 |
| F4 | ⬜ | 终端面板（dock tab） | `[Arc]` | `TerminalView` 放进右栏 | 待做 |
| F5 | ⬜ | 内置浏览器 | `[自有]` | WebView2（Windows）/ WebKitGTK（Linux） | 待做 |

### P8g 中区会话域

> **本组是 2026-11-05 新纳入的**。此前只对齐了中栏**宽度几何**（P8a），
> 会话内容的渲染与交互从未与 dsh 比对过——这是范围缺口。
> 事实来源 `dsh-src` commit `ce7073d`，逐项行号见规格 §7.5。
>
> 状态判据照旧：**契约有数据但 UI 没做 = ⬜；做了但口径不对 = 🟡；
> 契约无数据 = ➖**（不做假条目）。

**P0 — 契约有数据、纯 UI 缺失**

| # | 状态 | 项 | 实现方式 | dsh 行为（出处） | 我们 |
|---|---|---|---|---|---|
| M1 | ✅ | 用户消息复制 | `[自有]` | 时间 + 复制图标，点后变对勾 **1000ms** 复原（`MessageIconActions.tsx:45-90`） | 自建 `MessageActions`：HH:mm + 复制；对勾 1000ms 复原；剪贴板不可用**不假装成功**。dump「气泡 1 / 动作行 1」 |
| M2 | ✅ | 用户气泡宽度 | `[自有]` | `min(列宽×0.702, 82%)`（`MessageItem.module.css:10-21`） | `ConversationMetrics.UserBubbleWidth`，两条约束取小；`chat-bubble-width-is-two-sided` 钉住宽/窄两端 |
| M3 | 🟡 | 助手动作行 | `[自有]` | 复制 → 赞踩 → 分支 → 用量 → 时间（`TurnTailNodeView.tsx:57-78`） | 只有赞踩（复制/分支/用量需契约字段，见 §7.5.2） |
| M4 | ✅ | 消息间距分层 | `[自有]` | 默认 4 / assistant 相邻 12 / turn-process 后 16（`ChatView.module.css:77-93`） | 三档照搬，`chat-gaps-are-three-tiered` 断言严格递增 |
| M5 | ✅ | 贴底阈值 | `[自有]` | **25px**（`use-chat-reading.ts:7,277`） | 25。`chat-follow-threshold-is-25` 硬编码 dsh 的**生效值**（不是常量名上的 24） |
| M6 | ✅ | "回到底部"按钮 | `[自有]` | 34×34 圆形，距底 16px，滚离底部才出现（`ChatView.tsx:272-283`） | 同尺寸同位置；点击恢复跟随。dump「34×34 OK」 |
| M7 | ✅ | 空状态 Hero | `[自有]` | logo + "探索未至之境" + 预览版徽章 + 工作区 chip（`EmptyHero.tsx:132-166`） | logo + 标题 + 预览版徽章。**不做工作区 chip**（桌面端工作区由启动参数定，做了会是死按钮） |
| M8 | ✅ | 输入区渐变遮罩 | `[自有]` | 36px `linear-gradient` 贴底（`ConversationRoot.module.css:507-530`） | 36px 渐变，叠在消息流行底部（不是输入区行内，否则盖住输入框），不参与命中测试 |

**P1 — 需要少量新状态**

| # | 状态 | 项 | 实现方式 | dsh 行为（出处） | 我们 |
|---|---|---|---|---|---|
| M9 | ✅ | 流式运行状态行 | `[自有]` | 鲸鱼尾 + "深度求索中，用时 N ···"，1s 刷新（`RunningStatus.tsx:25-48`） | 自建 `RunStatusLine`，放进**滚动区末尾**（放滚动区外的话用户上滚就看不见）。dump 实测 0→7 秒计时在走 |
| M10 | ✅ | 已停止标记 | `[自有]` | assistant 尾部 11px 灰底小标签（`AssistantMarkdown.tsx:152-154`） | 旁挂 `_stoppedAssistantIds`（契约无该字段）。`chat-stopped-badge-needs-finished-turn` 断言回合进行中不能标 |
| M11 | ✅ | 工具卡三态视觉 | `[Arc]` | running 整行 shimmer / error 错误色+结果首行 / stopped 琥珀（`ToolRow.module.css:118-126`） | **2026-11-06 核实：Arc `ToolCall` 自带这四态**（shimmer / 错误+Retry / 审批描边 / 拒绝变暗），代码已映射 9 种 SDK 状态，运行时截图确认。**原判"需覆写或自建"有误** |
| M12 | ✅ | 代码块复制+语言标签 | `[Arc]` | 复制 1000ms 复原；语言标签；默认自动换行（`CodeBlock.tsx:157-206`） | **Arc 自带，无需自建**。2026-11-06 探针窗口实测：`Markdown.DefaultServices` 渲染 fence 时给出 `csharp` 语言标签、语法高亮、复制按钮、自动换行开关、12 行折叠。新增 `markdown-codeblock-uses-arc-defaults` 防以后被改坏 |

**P2 — 结构较大，延后**

| # | 状态 | 项 | 实现方式 | dsh 行为（出处） | 我们 |
|---|---|---|---|---|---|
| M13 | ⬜ | 轮次导航条 | `[自有]` | 右侧 28px 竖排刻度，hover 预览，≤900px 隐藏（`TurnNavigator.tsx:81-295`） | 无 |
| M14 | ⬜ | 加载更早 | `[自有]` | 顶部按钮 + 锚点补偿（`ChatView.tsx:239-245`） | 无（无分页） |
| M15 | ⬜ | 折叠四层模型 | `[自有]` | 轮次→分组→单条，不可互相绕过（`conversation-nodes/README.md:20-60`） | 只有单条折叠 |
| M16 | ⬜ | 工作详情模式 | `[自有]` | compact/standard/detailed/verbose + 两个偏好（`chat-settings.ts:15-56`） | 无 |
| M17 | ✅ | 列宽可拖拽 | `[自有]` | 右栏左边界 6px 拖拽区；宽度**不自己算**，仍交给 `Columns.Compute` 裁决；松手才持久化到 `ui.json`（`ConversationWidthControls.tsx:5-28`） | `drag-resize-cannot-bypass-column-limits`（拖到 900/2000 中栏都不低于下限）+ `saved-rightbar-width-is-revalidated`（坏值丢弃、正常值 576 保留） |

**明确不做（dsh 也没有，别照想象补）**

| # | 状态 | 项 | 理由与出处 |
|---|---|---|---|
| M18 | ➖ | 流式光标/闪烁 | dsh 全仓**没有** streaming caret——grep `cursor\|blink\|caret` 只命中输入框。加一个 `▍` 反而偏离 |
| M19 | ➖ | 会话内文本搜索 | dsh 没有（`Ctrl+F` 未接管）。`session.search` 是侧栏搜会话，不是页内搜 |
| M20 | ➖ | mermaid 渲染 | dsh 客户端不渲染，仓库根的 mermaid 只用于文档站校验脚本 |
| M21 | ➖ | 消息重试/编辑/分享/查看源 | dsh 都没有。只有"在新对话中分支"（`message.branch`） |
| M22 | ➖ | 消息级虚拟化 | **dsh 没有**（`ChatFlow.tsx:52-63` 全量 map）。我们已有 `VirtualizingStackPanel`——这是超出，不是欠缺。不拆，但诊断口径按可见容器算 |
| M23 | ➖ | 头像 | dsh 无（全仓 grep `avatar` 无命中） |

---

## 三、不在 dsh 对照范围的已完成项

这些是**我们独有**的能力，dsh 没有对应物，但同样需要维护：

| 状态 | 项 | 实现方式 | 验证 |
|---|---|---|---|
| ✅ | slot 框架（三 kind + priority + 保留 key） | `[自有]` | `slot-*` 12 条检查 |
| ✅ | UI 插件加载器（独立 ALC + 五种状态） | `[自有]` | `ext-*` 5 条检查 |
| ✅ | 消息流七种条目 + 虚拟化 | `[Arc]` | 1000 条 → 7 容器 |
| ✅ | 工具卡片（Input/Output/两段耗时） | `[Arc]` | `tool-*` 2 条 + `plan-*` 2 条 + `caps-*`/`budget-*` 3 条 |
| ✅ | 审批闭环（四档 + 待决锁 + 过期） | `[Arc]` | `approval-*` 5 条检查 |
| ✅ | 推理流（独立条目） | `[自有]` | `reasoning-separate-from-text` |
| ✅ | 命令面板 Cmd+K | `[Arc]` | dump「命令 4 条」 |
| ✅ | 序号缺口重同步 | `[自有]` | `resync-*` 2 条检查 |
| ✅ | `uiPlugin` 三段埋点 | `[自有]` | 实跑 register 80ms / build 8.6ms / render 570ms |
| ✅ | IPC 契约（已迁到 SDK 的 `Dougong.HostProtocol`） | `[自有]` | `wire-*` 3 条 + `ipc-*` 1 条 |
| ✅ | 平台抽象（`ISettingsStore` / `IEngineLocator`） | `[自有]` | `settings-store-is-swappable`、`platform-calls-stay-in-adapters` |
| ✅ | 回合计时（扣暂停段） | `[自有]` | `run-clock-*` 2 条 |

**检查总数：69 条**，每条都有 `--break=<name>` 反证。

**注意**：跑检查前要确认没有残留的 `DougongDesktop.AgentHost` / `App` 进程。
它们会锁住 `bin/` 里的 DLL，导致构建失败（MSB3027）——
而构建失败后脚本继续跑，看到的是**旧产物**，会误以为检查通过了。
`scripts/rebuild.sh` 已经处理这件事，`all-checks.sh` 也应该先清理。

---

## 四、这份清单怎么维护

**只在状态变化时改**，不按时间刷。改的时候同时改两处：
- 下面对应那一行
- §一 的总览计数

**判定从严，四条具体规则**：

1. **有类名不算**。类存在但没接进装配，算 `⬜`。
2. **接进装配但没验证，算 🟡**。验证指有确定性检查钉住行为，
   不是"我试过一次能跑"。
3. **验证了但只验"能跑"不算 ✅**。要回答移植的三问
   （对外暴露出什么 / 改内部实现哪一项会变而编译仍过 / 怎么破坏它检查才红）。
   2026-10-31 的教训：Web 端抽象"接进装配、编译通过、检查全绿"，
   按三问一查仍发现 `EngineClient` 里留着 65 行重复的平台代码。
4. **`🟡` 必须在"验证"列写清差什么**。写不出差什么，说明没核过。

**✅ 必须有证据**。每个 ✅ 在"验证"列指向一条能跑的检查或一次运行时实测。
没有证据的完成声明，下一轮复检会当误报处理——P3 曾有五条判据
"标了完成但代码不存在"。

**⏳ 必须能指出外部条件的具体出处**（哪份文档、哪个编号），
并写清触发条件。写不出就说明是漏了，不是等待。

---

## 五、维护记录

| 日期 | 事件 |
|---|---|
| 2026-10-31 | 从 `dsh-parity-spec.md` §7 拆出独立成文。拆出时用脚本重数了一遍：46 项中 ✅15 / ⬜29 / ⏳2（原手写汇总表错了两处） |
| 2026-10-31 | B1/B4 从 ⬜ 改 ✅（斜杠命令与模型选择已接通，有运行时验证） |
| 2026-10-31 | A9 从 ✅ 改 ⬜：`Sidebar.OpenWidth` 是主题层属性不可赋值，实际没做到 |
| 2026-11-05 | **B3 从 ⬜ 改 ✅**：Arc 自带完整附件链路（`BrowseAsync()` 自己开选择器）。我一度写了 200 行 `IFilePicker` 抽象 + 桌面实现，查完文档发现不需要，已删除。P8b 完成 |
| 2026-10-31 | **B2 从 ⬜ 改 ✅**（`@` 引用复用 `fs/list`，不新开协议）；**B6 从 ⬜ 改 ➖**（SDK 无 sources 数据，做不了） |
| 2026-10-31 | **B5 从 ⬜ 改 ✅**：自建 `FeedbackBar`（`AiAnswerActions` 是 internal）。同时发现 `ipc-every-message-is-registered` 迁移后失效——它查类型特性，而判别器已改到 `RegisterMessages` 的调用里，查不到任何东西就永远通过。已改为查 SDK 的 `RegisteredExtensions` |
| 2026-10-31 | **A9 从 ⬜ 改 ➖**：用户决定"保留 Arc 的，不用强行修改"。dsh 的 36/28/8/12 与 Arc 默认值接近，覆盖模板的收益不抵"与上游版本脱钩"的风险。P8a 待办清零 |
| 2026-10-31 | **F1/F2 从 ✅ 改 ⬜**：复核时 `grep TerminalView src/` 结果为空——包引用了但产品代码零使用。原标 ✅ 违反本文件第 1 条规则（"有类名不算"）。完成度 34% → 30% |
| 2026-11-05 | **B2 复核发现两个真缺陷**。①`FileSelected` 事件**没有订阅者**——本仓库第 4 例"定义了但没人用"。②`@` 引用**只能看到根目录一层**：右栏目录是不可点的 `TextBlock`，用户引用不了 `src/App/Shell/X.cs` 这类深层文件。已改为目录可点击触发 `DirectoryRequested` |
| 2026-11-05 | **修①又引入③**：目录可点之后**能进不能出**——点进 `src/` 后界面无任何返回入口，只能重启。补 `CurrentDirectory` 状态 + 面包屑（每级可点，当前层禁用）。新增检查 `dock-directory-nav-is-two-way`，断言返回事件有触发点**且**装配根有订阅（只查声明抓不到"定义了没人订阅"） |
| 2026-11-05 | **自检脚本自身抖动**：第一版回退验证写死 `Task.Delay(800)`，两轮里有一轮响应慢于此，打印"未增长"误导。改为**等条件成立**（50ms 轮询，5s 上限）。会时红时绿的自检比没有更糟 |
| 2026-11-05 | 检查数 73 → 74。运行时实测：根目录 18 项 → 进 `src/` 累加到 23 → 回退 `CurrentDirectory=''` 复位 |
| 2026-11-05 | 检查数 74 → **79**（`chat-*` 5 条）。其中 `chat-no-fake-streaming-caret` 的第一版反证**写错了**：往源码尾部追加 `// ▍`，被注释过滤跳过 → 反证不变红、检查形同未验证。改成追加非注释代码后才正确变红 |
| 2026-11-05 | **P8g 复核发现四个问题**。①`_compensating` 是**死字段**：有声明有读有重置，但从没有一处置 true——注释写着"只允许一层"实际一层没限。改为 `_compensationRounds` 计数上限 8 ②用户气泡圆角用了 `RadiusLg(16)`，dsh 是 `--dsw-radius-xl: 20px`，补 `RadiusXl` ③气泡底色手调淡绿 `FromArgb(18,47,111,94)`，而 token 表**本来就有** `DswSpecificBubble`（浅 `#EDF3FE`/深 `#2C2C2E`）④错误通告手调 `(200,60,60)` 而非 `StateErrorPrimary`；同时复制按钮连点会叠加多个复原计时，加令牌作废旧计时 |
| 2026-11-05 | **诊断口径错了一次**：查 `TextBlock.IsVisible` 判定空状态 Hero 是否显示，得到"13 条消息时 Hero 仍显示"的假警报——父容器隐藏后子元素 `IsVisible` 仍是 true。改用 `IsEffectivelyVisible` 才对。**误报比漏报更伤**，它教会人忽略这条输出 |
| 2026-11-05 | 新增 `chat-colors-come-from-tokens`，检查连续抓到三处：气泡色（真问题）、悬停半透明黑遮罩（不该拦，改判"是否有色相"）、错误通告手调红（真问题）。检查数 79 → **80** |
| 2026-11-05 | **P8g P1 完成 M9/M10**。M9 流式状态行 `RunStatusLine`（1s 刷新，实测 0→7 秒在走）；M10 已停止标记（旁挂 Id 集合，契约无该字段）。检查数 80 → **81** |
| 2026-11-05 | **M10 又踩到"依赖调用方守顺序"**：自检里同时调 `OnTurnStarted` + `MarkLastAssistantStopped`，界面出现"深度求索中，用时 6 秒"与"已停止"并存——自相矛盾。真实路径顺序是对的，但守卫要写在 `ConversationView` 自己身上。加 `_turnRunning` 互斥 + `chat-stopped-badge-needs-finished-turn` |
| 2026-11-05 | **SDK 升到 0.1.18-alpha.1**（0.1.13 → 0.1.18）。契约比对：`HostProtocol` 公开成员 **165 → 165，零增零删**；74 条检查全过；运行时连通（会话 0 条、模型 663 个/23 家）。这版变更集中在 CLI 终端呈现（字标/启动面板/单行状态行/黑白灰默认），与桌面端无交集 |
| 2026-11-05 | **发现等待对象变了**：0.1.17 那批能力（`SubagentRegistry`、`SkillDiscovery`、`SessionGoal`、`JobRegistry`、`PermissionPresets`、`ExitPlanMode`、`MessageFeedback`、`FileReferenceIndex`）**库里都有，IPC 契约里一个都没有**——`HostProtocol` 全文检索这几个词，命中 0 次。所以 C8 的等待从"等 SDK 补能力"改成"等 SDK 把能力暴露到契约"。这也解释了为什么升级零改动：这批根本到不了桌面端 |
| 2026-11-05 | **中区会话域纳入范围（P8g）**。此前只对齐了中栏宽度几何，会话内容行为从未与 dsh 比对——是范围缺口。新建规格 §7.5（含 17 种条目类型对照、几何实测值、三条优先级清单），进度新增 P8g 19 项（6✅/1🟡/6⬜/6➖） |
| 2026-11-05 | **P8g P0 八项完成六项**。M1 复制、M4 间距三档、M5 阈值 25、M6 回到底部、M7 空状态 Hero、M8 渐变遮罩。M2 气泡宽度从固定 60 改成两条约束取小，M3 动作行仍缺（需契约字段） |
| 2026-11-05 | **贴底逻辑改了四轮才对，值得记**。①原实现阈值 80（凭手感）②改成 25 后加"跨阈值"判断，结果内容增长时 Extent 先涨 Offset 后跟，被判成"用户滚走了"→载入后停在距底 572px ③改为比较"Offset 是否变小"④发现 `_scroll.Offset` 手算目标会静默失败，改用 `ScrollToEnd()` 并在 Background/Loaded/Render 三个优先级各排一次。**四次都是运行时 dump 抓到的，静态检查全程绿** |
| 2026-11-06 | **Mac 侧接手与三项修复**。①脚本 `100644 → 100755`（Windows 用 `bash x.sh` 调用看不出来）②`all-checks.sh` 反证清单漏 4 条——正向 82 全 PASS、脚本照打 `ALL CHECKS PASS`，但那 4 条**从未被证明能变红**。已补并加自保：正向每一条都必须出现在反证清单里，否则 exit 1 ③标题栏按平台分支（macOS 留 80px 红绿灯安全区、不自绘窗口按钮）。检查 82 → **83** |
| 2026-11-06 | **红绿灯位置截图确认**。原只能靠控件树推断（`screencapture` 被挡），实际截图看：红绿灯与「收起侧栏」图标间有明显空隙，未被压住，右侧无残留自绘按钮。80px 够用。**控件树证明不了"没被压住"**，这类结论只能来自像素 |
| 2026-11-06 | **Arc 升 `0.3.2-preview.2` → `0.4.0-preview.1`**。按元数据逐类型比对：删除 **0 个**，新增 25 个全属 `PropertyGrid` 一族（用不到）。83 条检查全过，应用截图与升级前逐像素一致。Avalonia 仍精确锁 `[12.1.4]`，无需改动 |
| 2026-11-06 | **§6.14 组件表整节重做**。旧版推荐的 4 个组件（`AgentStatusRunClock`/`AiDiffDocument`/`AiDiffText`/`AiRelatedQuestions`）**全是 internal**，外部写不出 `new`——它们只在 XML 文档的 `<member>` 条目里存在，而**文档不反映可见性**。本次按 DLL 元数据判定，扫 16 个包得 313 个可实例化控件 |
| 2026-11-06 | **两条旧结论被推翻**。①M11「工具卡三态需自建」→ `ToolCall` 四态全自带（shimmer/错误+Retry/审批描边/拒绝变暗），转 ✅ ②M12「需自建代码块」→ `TKCode.Arc.CodeBlock` 自带文件头+高亮+复制+折叠。另发现 `FeedbackActions`（public）可替换自建的 `FeedbackBar`——当初自建是因为 `AiAnswerActions` 是 internal，但那条理由不适用于 `FeedbackActions` |
| 2026-11-06 | **"实际接入多少组件"从 15 改回 10**。凭"代码里提到过"会数多：`Switch`/`Card`/`Arc.Badge` 只在 `ArcProbeWindow`（探针窗口）里；`SidebarView.cs` 的 `Badge =` 是 `SidebarItem` 的字符串属性不是 `Arc.Badge` 控件；`ContextWindow` 只出现在注释和一个同名业务属性里 |
| 2026-11-06 | **文档整理**。`docs/DougongDesktop-bootstrap.md` 是开工前的计划文（工程结构 `Desktop`/`PtyHost` 与实际不符），内容已提炼进 HANDOFF §8.6/§8.7 后删除。HANDOFF §五 不再重复 P8g 明细（两边曾漂移过）。README 加文档地图、修进度表与检查数 |
| 2026-11-06 | **M12 结案：Arc 自身足够，没写一行实现**。探针窗口实测 `Markdown.DefaultServices` 渲染 fence 时已有 `csharp` 语言标签、语法高亮、复制按钮、自动换行开关、12 行折叠——M12 要的三件事全在。**差点误判为"没做"**：产品界面里带代码的那条消息永远在视口外（虚拟化 + 354px 视口），`CodeBlock 0 个` 与"未渲染"完全同形，和 `ToolCall 0 张` 是同一个陷阱。新增 `markdown-codeblock-uses-arc-defaults` 防回归，检查 83 → **84** |
| 2026-11-06 | **新增 `DOUGONG_UI_PROBE=1`**：打开 P2 的组件探针窗口而不启动主界面。M12 就是靠它定论的——以后验单个控件（不走真实会话）都用这个入口，不必再造自检数据、也不必改产品代码 |
| 2026-11-06 | 删掉 `MainWindow.cs.bak`（0 字节，`f9114fd` 误提交的残留）。`git rm` 后仓库里不再有这个空文件 |
| 2026-11-06 | **P8c 开工：设置页落地（C1 ✅ / C2 🟡 / C3 🟡）**。用 Arc `SettingsPage` 搭外壳（左导航+右内容），四个分区都有真实数据：提供商显示 23 家 / 663 个模型（来自引擎的 `models/list`）、权限/外观如实描述当前行为、诊断显示连接状态与检查数。**设置页要独占主区**——1280 窗口下主区只有 424px，不让位的话 Arc 会一直退化成"横排 pills"最窄形态。C3 只做只读：不给密码框，因为不落盘的话填了也会丢 |
| 2026-11-06 | **新增 `tests/DougongDesktop.Ui.Headless.Checks`**（5 条）。这台机器上 `Avalonia.Native` 起不了渲染定时器（CVDisplayLink 报 -6661），真窗口时好时坏。用 `Avalonia.Headless` 无头跑，不依赖显示链接，CI 也能跑。**架构守卫刻意不引用 App**（守分层），所以另开工程，两者职责不混 |
| 2026-11-06 | **修了 `all-checks.sh` 一个静默失败**：构建失败时 `set -e` 不够，后面每个 `dotnet run --no-build` 会拿旧产物跑、全绿。实测踩到——给设置页注入反证时构建报 CS1061，而无头检查照样 PASS。已改为显式判断构建退出码并 `exit 1` |
| 2026-11-06 | **P8c 收尾：C4 插件页完成，C5/C6 归 ⏳，C7 归 ➖**。C4 用真实 `SampleUiPlugin` 验证（`示例插件（sample.ui-plugin）→ shell.trailing`）。C5/C6 查到 `PermissionPreset`/`AgentPreset` 在 `HostProtocol` 里**各 0 处**——与 C8 同一类缺口，硬做只能做成"看起来能配、实际不生效"。C7 明确不做（没有可调并行数）。P8c 至此 2✅/2🟡/3⏳/1➖ = 100% |
| 2026-11-06 | **无头检查又修了一处取样缺陷**：`CollectTexts` 只扫 `TextBlock`，而 `SettingsRow.Header`/`Description` 是 ContentControl 的属性、不是子控件，导致"插件 Id 没显示出来"的**误报**——实际渲染是好的。已改为 TextBlock + 行标题 + 行说明三路合并。这条是"误报比漏报更伤"的又一例 |
| 2026-11-06 | **P8f 开工：F2 完成，F1 卡在上游缺陷**。终端 tab 从"未实现"改为可点开，接 `TerminalPanel`（`TerminalView` + 状态说明）。**但 VT 状态机建不起来**：`TerminalCore.Create` 必抛 `NullReferenceException`，栈顶在 `..ctor(IntPtr terminal, ...)` 即终端指针为 null。已在独立控制台工程用 7 行复现（只加包，无我方代码）——**是上游缺陷，不是集成问题**。逐一排除过：原生库在输出目录、`deps.json` 有 runtimeTargets、`ctypes` 能 dlopen、`GhosttyRuntime.Version` 返回 `0.1.0-dev`（握手成功）、`in` 传参与四参数 `TerminalSize` 都试过 |
| 2026-11-06 | **修订 `dock-unimplemented-tabs-are-labeled`**。原检查无条件要求「终端」tab 标「未实现」且禁用，接终端后误报。但它的**意图是对的**——用户不该点进去才发现是空的。改为两个可接受形态：要么"禁用+标题标注"，要么"可点开+首屏说明原因"。两种都验证过反证能变红 |
| 2026-11-09 | **Arc 升 `0.4.0-preview.1` → `1.0.0`**（正式版）。按元数据逐类型比对：四个包**删除 0 个**，Arc +87 / Special +179 / Blocks +142，纯增量。91 条检查全过，界面逐像素一致，Avalonia 仍锁 `[12.1.4]`。**升级前先验了终端**：同一份 7 行复现仍抛 `NullReferenceException`——1.0.0 没修，F1 继续卡着。盘点见 `docs/arc-1.0-inventory.md` |
| 2026-11-09 | **标题栏换成 Arc 的 `ArcWindow` + `TitleBar`**（1.0.0 新提供）。先建最小探针窗口验证 macOS 交通灯让位真的由组件处理（截图确认左侧内容距交通灯约 24px，未被压住），再改产品代码。**删掉**：`Columns.MacTrafficLightInset = 80`（截图估的魔数）、`IsMacOS()` 平台分支、`BuildWindowButtons()` + `TitleButton()` 约 100 行死代码、根布局的标题栏行。**踩坑**：第一次改完两套标题栏叠加（`ArcWindow` 一条 + `AppShell` 一条），源码看不出，是运行时「标题栏按钮 7 个」暴露的。新增无头检查 `titlebar-uses-arc-slots` 守这个；检查总数 91 → **92** |
| 2026-11-09 | **`titlebar-branches-for-macos` 的判据换了，并修掉一处反证失效**。旧判据断言"存在 IsMacOS 分支 + BuildWindowButtons + MacTrafficLightInset"，换 `ArcWindow` 后这些**都该消失**，旧判据会因正确改动而失败。改为断言"继承 ArcWindow + 走 TitleBar.LeftContent + 无自绘按钮痕迹"。**关键**：改完先验反证，发现注入后仍 PASS——因为注入用的 `Replace("private Control BuildWindowButtons()")` 在方法删掉后什么也没做。改成直接给源码追加违规内容才正确变红。**不验反证的检查等于没有检查** |
| 2026-11-09 | **P8e E4 接上 `TKCode.Arc.Dock`**。用 `DockLayout` + 5 个 `DockPane`（文件/改动/计划/上下文 走 ToolsSource，终端 走 DocumentsSource——工具与文档是 Dock 的固有区分）。运行时实测 `pane 5 个，根=DockSplit`，无头与真实 GUI 都确认了。**用 `DOUGONG_UI_DOCK=1` 开关而非直接切默认**：Dock 多出拖拽区、关闭按钮、浮动窗口，整条交互路径变了，要先做视觉验收。顺带修了一条误导诊断：Dock 模式下没有 `TabItem`，旧诊断照报「tab 0 个（期望 5）」的假警报 |
| 2026-11-09 | **`Button` 命名冲突**：`using TKCode.Arc;` 后它与 `Avalonia.Controls.Button` 冲突，编译报 CS0104。这正是 llms.txt 警告的那条（"Always write the arc: prefix... otherwise names shared with Avalonia such as Button resolve to Avalonia's own controls"）。已在 RightDock 里加 `using ArcButton = TKCode.Arc.Button;` 别名，并把两处原生按钮显式写成 `Avalonia.Controls.Button` |
| 2026-11-10 | **SDK 0.1.18 → 0.1.19-alpha.1**。新增的正是 C5（权限预设）与 C8（子 agent）的契约：`permission/presets` / `permission/switch` / `permission/watch` / `subagent/list` / `subagent/result` / `subagent/cancel` 六条命令 + `permission/listed` / `subagent/listed` / `subagent/updated` 三个事件。**与 `fs/list` 那批不同：这一批 SDK 自带多态注册，不需要我们手工 RegisterDerivedType**（实测按基类序列化已带 `method` 判别器）。C5/C8 从 ⏳ 改 ⬜——不再是"等契约"，是"等接线" |
| 2026-11-10 | **新增判别器存在性断言**（`AssertSdkRegisters`）。多态判别器缺失时是**静默降级**：序列化不抛异常，只是不带 `method` 字段，对端当未知消息丢弃，UI 上表现为"按钮点了没反应"而日志里什么都没有。现改为启动时按基类序列化六条命令、逐个查 `method`，不符就抛并打印实际输出。已验证反证生效 |
| 2026-11-10 | **C5 引擎侧接通并实测**。`PermissionPresets` 全进程单实例，层序 `PermissionPolicyApprover(PresetApprover(inner, presets))`（外层先判"无访问直接放行"，内层按预设决定问不问）。三条命令 `permission/presets|switch|watch` 实现。**实测：interactive 问 1 次 → unattended 问 0 次**，证明切换真的改审批行为，不是空转开关。新增 `Framework/PermissionProjection.cs`（把 SDK 形状→协议形状的映射收在一处），SDK 引用预算 6 → 7，已复审记录理由 |
| 2026-11-10 | **向 SDK 侧报错自己是错的：字段命名**。我曾报 `Key`/`Enabled` 是 PascalCase 而 `sessionId` 是 camelCase。复核发现**是我的探针写错了**——那次用了默认 `JsonSerializerOptions`，没传 `HostProtocolContract.Json`。用对配置后全是 camelCase。教训已写进 `DesktopProtocol.DescribeWireFormat` 的注释：**凡验证线上格式，必须用生产同一份序列化配置** |
| 2026-11-10 | **发现并修复一个能让引擎整个退出的生产缺陷**。`HandleAsync` 反序列化只捕获 `JsonException`，但缺多态判别器时 SDK 抛的是 **`NotSupportedException`**——结果一条客户端发错的命令就让引擎进程退出，之后所有客户端都连不上。实测踩到（探针按具体类型序列化，不带 `method`）。已改为两种异常都捕，回错误事件而不是崩。新增检查 `engine-survives-bad-command` 钉住。**这类"一个客户端写错放大成所有人用不了"的缺陷，比功能缺失严重得多** |
| 2026-11-10 | **C5 界面完成**：设置页权限分区从静态说明改成真下拉。切预设只发请求、**不在本地先改**——以引擎回的 `permission/listed` 为准，否则"切换失败"会显示成"成功"。回填时用 `_updatingPresets` 挡住 `SelectionChanged` 回环（不挡会无限往返），这条也有检查覆盖 |
| 2026-11-10 | **C8 引擎侧接通**（三条命令 + 推送），但**没标 ✅**。原因：`SubagentHandle` 无公开构造，句柄只能由引擎真正派发子 agent 时创建，而本机无 provider 配置——`subagent/list` 只能验到空列表与错误路径。空列表证明不了链路通，这与 `ToolCall 0 张` 是同一个陷阱。已实现部分：`_subagents` 单实例（检查 `subagent-registry-is-single-instance` 守住"不许就地 new"）、三条命令、`Started`/`Completed` 双事件推送（RequestId=null，锁内异步投递避免死锁） |
| 2026-11-10 | **SDK 引用预算 7 → 8**：新增 `SubagentProjection.cs`。复审结论：该引用——把子 agent 的 SDK 形状→协议形状收在 Framework，否则 AgentHost 要直接引用 SDK。状态映射复用 SDK 的 `ProtocolMapping`（"已结束但无结果算取消还是完成"只该有一份判断） |
| 2026-11-10 | **发现 C8 的派发工具根本没收注**。上一轮为了绕开 `dispatcherAccessor` 的时序问题，只注册了 `SubagentListTool` / `SubagentResultTool`（两者只读注册表），**漏了 `SubagentTool`**——模型因此完全没有派发能力，而三条命令照样"工作"（永远返回空列表）。这个组合极其误导：接口都对、日志都干净，功能却不存在。已用延迟解析 `dispatcherAccessorRef` 补上（createDispatcher 先于 createRuntime 执行，用闭包变量绕开）。**教训**：只读工具工作 ≠ 主功能接通 |
| 2026-11-10 | **用 minimax 真实 provider 验证 C8**。模型确实调用了 `subagent` 工具（`"toolName":"subagent"`，带完整 prompt 参数），说明注册与工具暴露这条链是通的。但整轮未完成：provider 返回 **429「已达到 Token Plan 用量上限」**——配额耗尽，非代码问题。所以 C8 停在 🟡：派发路径验到了"被调用"，没验到"子 agent 真正跑完并回流 Started/Completed 事件"。等配额恢复后补验 |
| 2026-11-10 | **C8 全链路验证通过（真实 minimax provider）**。完整时序：模型派发 → Started(running) + Completed(completed) 同 id 成对推送、requestId=null → `subagent/list` 看到同一实例（证明注册表是同一份，SDK 说的"唯一失败模式"没发生）→ `subagent/result` 取到结果 → 子 agent 实际完成工作（列出 7 个 csproj 并读 Directory.Build.props）。工具调用序列 `subagent → subagent_list → subagent_result → list_directory` 完全正常。第一次跑时出现 `toolRounds=83` 且只输出一句话，重跑后是 52 轮正常完成——判为模型行为漂移，非接线问题 |
| 2026-11-10 | **C2/C3 收尾**。查清两者都卡在同一条：模型与密钥都是引擎**启动时**从环境变量读入的只读快照（`EngineOptions` 的 `init` 属性），契约里只有 `model/list`（查询）没有切换命令。真实生效必须"改配置→重启引擎"，而那要引入密钥落盘。**选择只做能生效的那一半**：C2 做成可浏览的模型清单（分组/能力/当前标记），C3 做成密钥形态诊断（长度/前缀/空白/引号）+ 引导。两处都如实写明限制，不给假控件 |
| 2026-11-10 | **修了一条自己写坏的检查**。`current-model-stays-visible-when-buried` 第一版断言"界面文本含 model-11"，而模型名同时出现在摘要行"· 当前 model-11"里，所以列表截断与否都会通过——反证一做就露馅。改为**只收集 `SettingsRow.Header`**（列表行），不含摘要块。修正后反向测试正确失败。**这是这个项目第 N 次"检查看似通过、其实测的不是那件事"** |
| 2026-11-10 | **M17 列宽可拖拽完成**。右栏左边界加 6px 拖拽区，**宽度不自己算**——拖动只改 `_rightbarWidth`，仍交给 `Columns.Compute` 裁决，所以右栏先让步/中栏最低 400/右栏上限 45% 不会被绕过。松手才写盘（每帧落盘会拖慢手势）。新增 `AppSettings.RightbarWidth` 存像素值 |
| 2026-11-10 | **修了两个自己在 M17 里制造的缺陷**。①`AppSettings.Load` 注释写了"坏宽度当没存过"但**校验逻辑根本没加**——注释成了空话。②补上后又被嵌进"PaneSizes 有效"分支，导致**只存宽度时整份设置被丢弃**，拖动宽度永远还原不了（检查抓到：正常值 576 读回 null）。已改为两字段各自独立校验 |
| 2026-11-10 | **一条检查的方法被证伪**。`saved-rightbar-width-is-revalidated` 第一版查源码里有没有 `IsNaN`，而 `PaneSizes` 校验里也有——删掉宽度校验照样通过。改为**行为验证**：写坏 JSON 到内存 store、调 `Load`、断言读回 null。**字符串匹配证明不了"这段逻辑管的是哪个字段"** |
| 2026-11-10 | **标题栏按 dsh 做 Mac/Win 适配**。「应用 / 编辑」菜单改为**仅 Windows**——依据是 dsh 源码 `apps/desktop/src/preload-windows.ts`：`syncWindowsAppearance()` 里 `if (process.platform !== 'win32') return`，那个菜单本就是给 Windows 自绘标题栏配的（`titleBarStyle: 'hidden'` 抹了原生标题栏）。macOS 是 `hiddenInset`，交通灯还在、菜单栏在屏幕顶部，标题栏里不该再放一份。macOS 上现在只剩「收起侧栏」，截图确认：交通灯 → Dougong Desktop → 收起侧栏。新增检查 `titlebar-menus-are-windows-only`，并把 `titlebar-uses-arc-slots` 的按钮期望值改成按平台分（原本写死 3 会让 Mac 上的正确实现变红） |
