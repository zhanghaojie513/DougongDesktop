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

**基线**：dsh `0.2.2-alpha.2`（commit `ce7073d`）· TKCode.Arc `0.4.0-preview.1` · SDK `0.1.18-alpha.1`

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
| P8a 布局骨架 | 10 | 0 | 0 | 1 | 1 | 12 | **100%** |
| P8b 会话与输入 | 5 | 0 | 0 | 0 | 1 | 6 | **100%** |
| P8c 设置域 | 0 | 0 | 7 | 1 | 0 | 8 | 0% |
| P8d 会话增强 | 0 | 0 | 7 | 0 | 0 | 7 | 0% |
| P8e 插件体系 | 1 | 0 | 7 | 0 | 0 | 8 | 13% |
| P8f 终端与浏览器 | 0 | 0 | 5 | 0 | 0 | 5 | 0% |
| **P8g 中区会话域** | **10** | **1** | **6** | **0** | **6** | **23** | **65%** |
| **合计** | **26** | **1** | **32** | **2** | **8** | **69** | **46%** |

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
| C1 | ⬜ | 设置外壳 + 通用分区 | `[自有]` | 主区 keyed slot 的 `settings` 面板 | 待做 |
| C2 | ⬜ | 模型/provider 设置 | `[Arc]` | 表单 + `ProviderCatalog.AllPresets`（22 家 / 662 模型） | 待做 |
| C3 | ⬜ | 密钥管理 | `[自有]` | **三档**：环境变量 / 凭据管理器 / 会话内存。绝不落配置文件 | 待做 |
| C4 | ⬜ | 插件清单页 | `[Arc]` | 按作用域分组，复用 `UiExtensionLoader.Loaded` | 待做 |
| C5 | ⬜ | 权限预设 | `[自有]` | 默认权限行 + `/permission` 选择器 | 待做 |
| C6 | ⬜ | Agent preset | `[自有]` | 选择 + 说明 | 待做 |
| C7 | ⬜ | Agent 循环设置（并行工具上限） | `[自有]` | **原描述"只缺配置暴露"经 0.1.18 实测有误**：`ToolConcurrency` 只有两级（串行 / 工作区独占），并发由 `ToolExecutionPlan` 的 `Reads`/`Writes` 资源键自动仲裁，**没有用户可调的并行数**。界面不该放一个调了也不生效的滑块 | 待做（范围待定） |
| C8 | ⏳ | 子智能体设置 | `[—]` | **等 SDK**：`dsh-capability-gap.md` P2 #15（台账 #13）。**0.1.18 实测更新**：SDK 库内已有 `SubagentRegistry` + `subagent`/`subagent_list`/`subagent_result` 工具（0.1.17 引入），但 `Dougong.HostProtocol` 的 119 个顶层类型里 **0 处提及 subagent**——能力在引擎里、到不了桌面端。等待对象从"等能力"改成**"等契约暴露"** | — |

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
| E4 | ⬜ | dockkit 分裂标签树 | `[Arc]` | 画布用 `WorkflowCanvas`，分裂树自建 | 待做 |
| E5 | ⬜ | 右栏停靠面 + tab 注册表 | `[Arc]` | `AiSidePanel` / `AiPanelColumn` | 待做 |
| E6 | ✅ | 右侧文件树 | `[自有]` | `fs/list` + `RightDock` 文件 tab；**可进入子目录，面包屑可回退** | 运行时「目录 ''：18 项」→ 进 `src/` 「5 项」；`fs-list-*` 3 条检查 + `dock-directory-nav-is-two-way` |
| E7 | ⬜ | 文档预览 | `[自有]` | Markdown/代码/图片/PDF/Office | 待做 |
| E8 | ⬜ | 右键菜单扩展点 | `[自有]` | — | 待做 |

### P8f 终端与浏览器

| # | 状态 | 项 | 实现方式 | 我们 | 验证 |
|---|---|---|---|---|---|
| F1 | ⬜ | 终端 VT 解析 | `[—]` | 包已引用，但**没有代码调用** `TerminalCore` | 只在独立探针里实测过 Write → FormatPlainText 正确，产品代码零使用 |
| F2 | ⬜ | 终端控件 | `[—]` | 包已引用，但**没有代码实例化** `TerminalView` | 反射确认公开、原生库随包落地——但那不算"接进装配" |
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
| M12 | ⬜ | 代码块复制+语言标签 | `[Arc]` | 复制 1000ms 复原；语言标签；默认自动换行（`CodeBlock.tsx:157-206`） | **改用 `TKCode.Arc.CodeBlock`**（public）：文件头即语言标签 + 语法高亮 + 复制动作 + 可选折叠，正是 M12 要的三件事。待确认 `Markdown` 能否把代码块交给它 |

**P2 — 结构较大，延后**

| # | 状态 | 项 | 实现方式 | dsh 行为（出处） | 我们 |
|---|---|---|---|---|---|
| M13 | ⬜ | 轮次导航条 | `[自有]` | 右侧 28px 竖排刻度，hover 预览，≤900px 隐藏（`TurnNavigator.tsx:81-295`） | 无 |
| M14 | ⬜ | 加载更早 | `[自有]` | 顶部按钮 + 锚点补偿（`ChatView.tsx:239-245`） | 无（无分页） |
| M15 | ⬜ | 折叠四层模型 | `[自有]` | 轮次→分组→单条，不可互相绕过（`conversation-nodes/README.md:20-60`） | 只有单条折叠 |
| M16 | ⬜ | 工作详情模式 | `[自有]` | compact/standard/detailed/verbose + 两个偏好（`chat-settings.ts:15-56`） | 无 |
| M17 | ⬜ | 列宽可拖拽 | `[自有]` | min 640、边缘预算 176、localStorage 持久化（`ConversationWidthControls.tsx:5-28`） | 固定 clamp |

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
