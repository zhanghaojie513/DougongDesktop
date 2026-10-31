# dsh 桌面对照规格（1:1 复刻依据）

> **这份文档是什么**：从 dsh 源码实测提取的布局与视觉规格，作为桌面端 1:1 复刻的唯一依据。
> 所有数字都有出处（文件:行），不是估计。

## 版本索引

> **两套版本，都要记**：dsh 是复刻基线，Arc 是实现载体。

### dsh（复刻基线）



| 项 | 值 |
|---|---|
| **dsh 版本** | `0.2.2-alpha.2` |
| **commit** | `ce7073d51254c26cd63b22a60835b8f948deb447` |
| **commit 时间** | 2026-10-30 00:13:15 +0800 |
| **仓库** | https://github.com/deepseek-ai/deepseek-harness |
| **源码位置** | `C:\work\myProject\dsh-src`（sparse checkout，只拉 UI 相关包） |
| **提取日期** | 2026-10-30 |

**复现命令**：

```sh
git clone --depth 1 --filter=blob:none --sparse \
  https://github.com/deepseek-ai/deepseek-harness.git dsh-src
cd dsh-src
git sparse-checkout set \
  packages/client/ui-layout packages/client/ui-theme packages/client/ui-dockkit \
  packages/client/ui-sidebar packages/client/ui-sidebar-right \
  packages/client/ui-sidebar-files packages/client/ui-chat \
  packages/client/ui-conversation packages/client/ui-primitives
```

**升级时要做的事**：dsh 处于 developer preview，README 原话是
"THERE WILL BE COMPATIBILITY-BREAKING CHANGES"。换版本后必须重跑提取，
逐条对比本文件的数字。**不要假设规格稳定。**

### TKCode.Arc（实现载体）

| 项 | 值 |
|---|---|
| **版本** | `0.3.2-preview.2`（2026-10-30 从 preview.1 升级） |
| **仓库** | https://github.com/AIDotNet/TKCode.Arc |
| **commit** | `db031ffe`（2026-10-30） |
| **Avalonia** | 必须精确 `12.1.4` |
| **包数** | 17 个（我们用了 4 个，另发现 Terminal / Workflow / Icons.Lucide 三个未用） |

**升级记录**：从 `preview.1` → `preview.2`，零警告零错误，57 条检查全过，
运行时三栏 `280/424/576` 正常，虚拟化仍生效（1000 条 → 7 容器）。
**preview.2 没有修侧边栏 64px**（`Sidebar.IsCollapsed` 注释仍是 "64px icon rail"），
所以 §9.1 的 8px 偏差依旧存在。

---

## 0. 一个必须先说清的事实

**dsh 桌面端没有独立的桌面 UI。** 它是 Electron 外壳，加载的是**同源的 Web 应用**。
`apps/desktop/README.zh.md` 原话：

> 桌面应用是完整 dsh Web 应用外的一层 Electron 壳。

所以"1:1 复刻 dsh 桌面端"= **1:1 复刻 dsh 的 Web UI**，外壳差异（标题栏、原生菜单、
窗口按钮）不属于 UI 复刻范围。

**这带来一个直接结论**：`packages/client/ui-*` 就是全部 UI 规格来源，不看 `apps/desktop`。

---

## 1. 三栏几何（最关键的规格）

出处：`packages/client/ui-layout/src/client/columns.ts`

| 常量 | 值 | 含义 |
|---|---|---|
| `SIDEBAR_DEFAULT` | **280** | 侧边栏默认宽度 |
| `SIDEBAR_MIN` | **264** | 拖拽下限 |
| `SIDEBAR_MAX` | **420** | 拖拽上限 |
| `SIDEBAR_COLLAPSED` | **56** | 收起后的图标轨道（24px 图标栏 + 两侧 16px padding） |
| `CENTER_MIN` | **400** | 中栏保护下限 |
| `RIGHTBAR_MIN` | **300** | 右栏拖拽下限 |
| `RIGHTBAR_MAX_RATIO` | **0.7** | 右栏上限 = 框架宽 × 70% |
| `RIGHTBAR_DEFAULT_RATIO` | **0.45** | 右栏首次打开 = 框架宽 × 45% |
| `SIDEBAR_DRAWER_BREAKPOINT` | **768** | 低于此宽度侧边栏变抽屉（浮层，不占列宽） |
| `SIDEBAR_AUTO_COLLAPSE` | **1024** | 低于此宽度侧边栏自动收起到轨道 |

### 求解顺序（这是 dsh 的核心取舍）

`computeColumns()` 的顺序是**右栏先让步**：

```ts
const s = sidebar === 0 ? collapsedWidth : clampWidth(sidebar, 264, 420)
const available = viewport - s - CENTER_MIN
const r = rightbar === 0 || available < RIGHTBAR_MIN
  ? 0                                                    // 放不下就整条轨道消失
  : Math.min(available, clampWidth(rightbar, 300, viewport * 0.7))
return { sidebar: s, center: Math.max(0, viewport - s - r), rightbar: r }
```

**顺序含义**：
1. 侧边栏**不让步**（除响应式自动收起外）
2. 右栏**先缩小**到 300
3. 右栏再让就**整条轨道消失**（`r = 0`）
4. 只有轨道消失后，中栏才允许低于 400

**这与我当前实现的关键差异**：我现在用 `ResizablePanels` + 百分比 `Sizes`，
而 dsh 用**像素**宽度 + 这个求解器。百分比在窗口缩放时会漂移，像素才是 dsh 的行为。

### 拖拽

`AppFrame.tsx`：
- 侧边栏拖拽：`setSidebar(base + dx)`（`base` 是**拖拽开始时的渲染宽度**，
  不是存储的偏好值——防止"抓住已被压缩的面板"时跳回）
- 右栏拖拽：`setRightbar(base - dx)`（**反向**，因为右栏贴右边缘）
- 用 `setPointerCapture` + `requestAnimationFrame` 节流
- 拖拽期间**暂停轨道过渡动画**，否则列边缘会脱手

---

## 2. 侧边栏几何

出处：`packages/client/ui-sidebar/src/client/SidebarRoot.module.css`

| 位置 | 值 |
|---|---|
| 根容器 padding | `6px 8px`（上/下 6，左/右 8） |
| 根容器字号 | **14px** |
| 品牌行（展开态） | 高度 **52px**，负 margin `-6px -8px 0`（抵消根 padding，让分隔线通栏） |
| 品牌行（Windows 原生标题栏） | 高度 **40px** |
| 新建会话按钮 | 高度 **36px**，圆角 `radius-md`(12px) |
| 控件行（通用） | 高度 **28px**，圆角 `radius-sm`(8px) |
| 会话行 | 高度 **36px**，圆角 `radius-md`(12px) |
| 行内 gap | **6px**（`--dsh-sidebar-label-gap`） |
| 行 padding | **12px**（`--dsh-sidebar-row-padding`） |

### 收起轨道几何（Figma rail spec，CSS 注释原话）

> 36x36 control boxes centered in the 56px rail (10px side padding),
> 12px vertical rhythm, 18px from the rail top to the whale's box (24px to the 24-wide whale glyph itself).

- 收起态 padding：`18px 10px 6px`
- 控件盒：**36 × 36**，居中于 56px 轨道（两侧留 10px）
- 垂直节奏：**12px**
- 顶部到 logo 盒：**18px**

### 结构（从上到下）

1. **品牌行**：logo mark + 名称（两个独立 slot）+ 折叠按钮
2. **新建会话**：占满宽度，左侧图标 + 文字 + 右侧快捷键
3. **工作区 / 会话列表**：可滚动区，滚动条隐藏但保留布局（`.quietBars`）
4. **设置**：**固定在底部**

---

## 3. 主区与右栏

出处：`packages/client/ui-layout/README.zh.md` + `AppFrame.tsx`

- 右栏**是一条轨道（track），不是一个盒子**：占位者把自己的面板贴到框架右边缘绘制，
  轨道只决定中栏要不要让出空间
- 右栏首次打开：视口宽 × **45%**
- 右栏上限：视口宽 × **70%**
- 中栏保护：**400px**
- `shell.bottom` **横跨三列**，在整行下方；三列按它的高度缩短；**空内容不预留空间**
- `shell.overlay` 覆盖整个框架
- `shell.leading` 只在抽屉态或 macOS 侧栏完全隐藏时挂载

### 底部行

**注意与我的实现差异**：我把状态条放在底部行的**右侧**（`*,Auto` 两列）。
dsh 的 `shell.bottom` 是**横跨整行的单一 slot**，且默认组合**不占用它**。

---

## 4. 颜色 token

出处：`packages/client/ui-theme/src/styles/design-platform.css`（430 行）

- **light**：`body { ... }`（第 177 行起）
- **dark**：`body[data-ds-dark-theme] { ... }`（第 300 行起）
- 两段各有 **108 个 `--dsw-alias-*`** 语义 token
- 前缀 `--dsw-static-*` 是原始色阶（amber / blue / deepseek / green / neutral-bluish / red / violet）
- 另有 `--dsw-specific-sidebar-fill`（侧边栏填充，与主区分开）

### 关键 token（light / dark 成对）

| token | 用途 |
|---|---|
| `--dsw-alias-bg-base` | 文档底色 |
| `--dsw-alias-bg-layer-1/2/3` | 三层容器 |
| `--dsw-alias-label-primary` | 主文字 |
| `--dsw-alias-border-l1..l4` | 四级描边（0.04 / 0.10 / 0.12 / 0.16 黑） |
| `--dsw-alias-brand-primary` | 品牌色 |
| `--dsw-alias-bg-mask-1/2/3` | 三级遮罩 |

### 圆角

| token | 值 |
|---|---|
| `--dsw-radius-xs` | 4px |
| `--dsw-radius-sm` | 8px |
| `--dsw-radius-md` | 12px |
| `--dsw-radius-lg` | 16px |
| `--dsw-radius-xl` | 20px |
| `--dsw-radius-panel` | **28px** |

### 字体

- 品牌字体：**Montserrat**（`--dsw-font-family-brand`）
- 正文字号默认 **14px**（10–22 可调）
- 代码字号默认 **11px**（10–16）
- 终端字号默认 **13px**（10–20）

---

## 4.5 功能面盘点（59 个 UI 包）

> **关键认知修正**：用户要求的是"包含功能"的 1:1，不只是 UI。
> dsh 有 **59 个 `ui-*` 包**，每个都是一个独立功能域。下面按功能归类。

### 已实现（我们已有）

| dsh 包 | 我们对应 |
|---|---|
| `ui-layout` | AppShell 三栏（本轮改成 dsh 几何） |
| `ui-sidebar` | SidebarView |
| `ui-chat` / `ui-conversation` | ConversationView |
| `ui-tool` | ToolCall 卡片（P5） |
| `ui-approval` | ApprovalCard（P6） |
| `ui-plan` | 计划 tab（P5/P7） |
| `ui-theme` | 部分（Arc 主题，未对齐 `--dsw-*`） |
| `ui-slots` | Ui.Core 的 SlotRegistry |

### 未实现，且**不依赖新 SDK 能力**（可做）

| dsh 包 | 功能 | 工作量估计 |
|---|---|---|
| `ui-commands` | `/` 斜杠命令 | 中 |
| `ui-shortcuts` | 快捷键总览与搜索 | 小 |
| `ui-model-selection` | `/model` 弹窗 + composer 模型位 | 中 |
| `ui-settings-general` | 设置外壳 + 通用分区 | 中 |
| `ui-settings-models` | provider 行、API 密钥、模型列表 | 大 |
| `ui-settings-plugin-inventory` | 插件清单（按作用域分组） | 中 |
| `ui-settings-plugins` | 内置插件设置分区 | 中 |
| `ui-workspace` | 工作区浏览器（分组/扁平会话行） | 中 |
| `ui-session` | 会话控制器与逐会话上下文 | 中 |
| `ui-attachment` | 附件栏、拖放、图片画廊、灯箱 | 大 |
| `ui-input-trigger` | `/` 与 `@` 触发流水线与候选菜单 | 中 |
| `ui-reference` | `@file` / `@session` 引用 | 中 |
| `ui-context-menu` | 右键菜单扩展点 | 小 |
| `ui-message-feedback` | 消息 Like/Dislike + 反馈弹窗 | 小 |
| `ui-user-questions` | `ask_user_question` 卡片 + 计时等待 | 中 |
| `ui-deliverables` | 改动文件卡片、交付文件卡片、review tab | 中 |
| `ui-trajectory` | 按轮次的事件记录表 + 时间概览 | 大 |
| `ui-dockkit` | 可分裂的标签格树 | 🟡 部分：`WorkflowCanvas` 可做画布部分，分裂树仍需自建 |
| `ui-sidebar-right` | 每会话一个停靠面 + tab 类型注册表 | 大 |
| `ui-sidebar-files` | 工作区文件树（逐层列出） | 中（部分已有） |
| `ui-sidebar-documentpreview` | Markdown/代码/图片/PDF/Office 预览 | 大 |
| `ui-goal` | composer 目标条带 | 小 |
| `ui-skill` | skill 引用与调用卡片 | 中 |
| `ui-jobs` | 后台任务列表 + 流式输出面板 | 中 |
| `ui-schedule` | 自动化任务与会话提醒 | 大 |
| `ui-subagent` | subagent 目录与 `@` 引用 | 大 |
| `ui-workflow-run` | 工作流运行节点 | ✅ **已有载体**：`TKCode.Arc.Workflow` 的 `WorkflowCanvas`。见 §11 |
| `ui-permission-presets` | 默认权限行 + `/permission` 选择器 | 中 |
| `ui-agent-preset` | Agent preset 选择与说明 | 中 |
| `ui-secret-mask` | 已发送文本里的凭据遮挡 | 小 |
| `ui-file-actions` | 下载/另存原始文件 | 小 |
| `ui-file-drag` | 应用内文件拖放 | 小 |
| `ui-plugin-manager` | 插件组合包管理与配置 | 大 |
| `ui-plugin-market` | 插件市场「发现」页 | 大 |

### 未实现，且**依赖我们没有的能力**（做不到 / 需外部组件）

| dsh 包 | 功能 | 障碍 |
|---|---|---|
| `ui-sidebar-terminal` | 交互式 shell 标签页 | ✅ **已有载体**：`TKCode.Arc.Terminal` 的 `TerminalView`（Ghostty 内核）。见 §11 |
| `ui-sidebar-browser` | 沙箱内浏览 HTTP(S) 页面 | 需要内嵌浏览器内核（WebView2），Avalonia 需额外集成 |
| `ui-settings-account` | DeepSeek 账号登录 | **用户已明确不做账户登录** |
| `ui-settings-web-search` | 网页搜索 API 设置 | 依赖 DeepSeek 侧 API，我们没有 |
| `ui-settings-subagent` | 子智能体委派深度/并行 | **等 SDK**：`dsh-capability-gap.md` P2 #15 列为缺口，SDK 正在完善 |
| `ui-settings-agent-loop` | 并行工具调用上限 | **非缺口**：`LoopAgentRuntime` 已存在，只是配置未暴露，属 UI 侧待接 |
| `ui-open-in-app` | 在外部应用打开文件 | 平台相关，Avalonia 可做（`Process.Start`），低优先 |
| `ui-directory-picker-native` | 原生目录选择器 | Avalonia 有 `StorageProvider`，**可做** |

### 数量结论

| 类别 | 数量 |
|---|---|
| 已实现 | 8 |
| 可做（不依赖新能力） | 30 |
| 依赖我们没有的能力 | 8 |
| **合计** | 46 个功能域（包数 59，含 13 个基础/内部包） |

**30 个"可做"是主要工作量来源。** 按每项 0.5–3 天估，全做完约 **45–60 天**，
远超原计划 P8 的定位。这必须由用户决定取舍。

---

## 5. 与当前实现的差异清单

| # | 项目 | 当前实现 | dsh | 影响 |
|---|---|---|---|---|
| 1 | 三栏宽度 | Arc `ResizablePanels` + 百分比 `Sizes` | **像素** + `computeColumns` 求解器 | 布局行为不同：dsh 右栏先让步并整条消失 |
| 2 | 侧边栏默认宽 | 404（实测，2.4 份） | **280** | 视觉明显不同 |
| 3 | 侧边栏收起 | Arc 面板折叠 | **56px 图标轨道** | 收起后要有图标栏，不是消失 |
| 4 | 右栏 | `Tabs` 横排 tab | 贴右边缘的面板 + 轨道 | 形态不同 |
| 5 | 底部行 | 左侧 slot + 右侧状态条 | 横跨三列的单一 slot，**默认空** | 需要重构 |
| 6 | 状态条 | 自建色点 + 文字 | dsh **底部默认不占位** | 可能要移除或移位 |
| 7 | 断点 | 无 | 768（抽屉）/ 1024（自动收起） | 需要响应式 |
| 8 | 配色 | Arc 主题 | `--dsw-*` 108 个语义 token | 需要映射表 |

---

## 6. 偏差逐条核实（2026-10-31 修正）

> **这一节被重写过一次。** 初版凭印象写了 5 条"Avalonia 做不到"，
> 实测 API 后发现有 4 条是错的。保留这次修正的痕迹，因为
> "没查就断言做不到"是个该记的教训。

### 6.1 核实结论总表

| # | 项目 | 初版判断 | **核实结果** | 处置 |
|---|---|---|---|---|
| 1 | Electron 毛玻璃/vibrancy | ❌ 无 API | ✅ **能做**：`WindowTransparencyLevel.{Blur,AcrylicBlur,Mica}` + `ExperimentalAcrylicBorder` | 用 `AcrylicBlur` + `ExperimentalAcrylicBorder` |
| 2 | CSS grid 轨道过渡 | ❌ 列宽无过渡 | ✅ **能做**：`Transitions` + `DoubleTransition` | 动画一个 double 绑到列宽（`GridLength` 本身不可动画） |
| 3 | 60fps 拖拽跟随 | ⚠️ 无 rAF | ✅ **本来就没问题** | 事件驱动直接跟手。rAF 是浏览器帧调度，不是"60fps" |
| 4 | `device-posture` 折叠屏 | ❌ 无 API | ✅ **确实没有** | 桌面端无折痕屏，**不做** |
| 5 | Web 字体回退链 | ❌ 只支持已注册 | ⚠️ **能做，形式不同**：`FontManagerOptions.FontFallbacks` / `FontFamilyMappings` / `EmbeddedFontCollection` 都在 | 设置页改为**选择已注册字体**，不支持自由输入任意字体名 |
| 6 | `color-mix()` | ❌ 无运行时混色 | ⚠️ **能做**：`Color.ToHsl/ToHsv` 齐全，自己写 Mix | 写 `ColorMath.Mix()`，token 里的 color-mix 在编译期算成固定值 |

**真实做不到的只有 1 条**（折叠屏姿态），且它对本项目无实际影响。

### 6.2 各条的落地方式

#### 1. 毛玻璃（可做）

```csharp
// 窗口级
TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };

// 侧边栏
new ExperimentalAcrylicBorder
{
    Material = new ExperimentalAcrylicMaterial
    {
        BackgroundSource = AcrylicBackgroundSource.Digger,
        TintColor = Color.Parse("#1B1B1C"),
        TintOpacity = 0.85,
        MaterialOpacity = 0.6,
    },
    Child = sidebarContent,
};
```

**平台差异**：Windows 上是系统级 Acrylic（与 Electron 同级）；Linux 可能退化为无模糊（取决于合成器），需在启动时检查 `TransparencyLevel` 实际值并降级为不透明。

#### 2. 轨道过渡（可做，但要绕一步）

`GridLength` **不在** `DoubleTransition` 的可动画类型里。做法是：

```csharp
// 用一个 double 属性承载目标宽度，动画它，再同步到列定义
public static readonly StyledProperty<double> SidebarPxProperty = ...;
// Transitions 加 DoubleTransition；列宽在属性变化的回调里赋值
```

#### 5. 字体（可做，但设置形态不同）

- 支持：`FontManagerOptions.FontFamilyMappings` 显式注册 + `FontFallbacks` 回退链
- 不支持：用户输入 `"MyFont, sans-serif"` 这种 CSS 式自由列表——映射表必须预先建好
- 所以设置页做**下拉选择**，而不是 dsh 的**自由文本输入**

---

## 7. 实施计划

> **主清单已拆出为独立文件：[dsh-parity-progress.md](dsh-parity-progress.md)**
>
> 分工：那份只讲**状态**（可逐行勾选、可数）；这份讲**规格**（每个数字的出处、
> 为什么这样做、做不到的怎么处置）。
>
> 拆开的理由：进度会被频繁改动，规格应该稳定。混在一起时，
> 每次改状态都要动规格文件，而规格文件里那些"逐条核实过的数字"
> 不该因为一次勾选而产生 diff 噪音。
>
> **下面 7.11–7.14 保留**，它们是规格性的记录（验收理念、复检发现、组件取舍），
> 不是进度表。

---

## 7.11 验收理念：行为等价，不是编译通过

> 这一节抄的是 pi.net 的移植理念（用户指定），但**它的适用范围比移植更广**——
> 我们不是把 dsh 的 TS 翻成 C#，而是在不同框架上重建同样的行为。
> 陷阱是同一个：**编译通过 ≠ 做完了**。

### 该问的问题

编译器一个都不会回答的那些：

| 问题 | 我们的对应 |
|---|---|
| 流式事件的顺序一样吗？ | `TextDelta` / `ReasoningDelta` 的交错顺序 |
| 工具参数被截断时错误文案一样吗？ | `ToolResultTruncated` 的措辞 |
| 会话恢复后消息树的分叉点一样吗？ | `SessionSnapshot` 的 `Sequence` 基准 |
| JSON 少一个字段，默认值一样吗？ | `wire-null-omitted` 检查 |
| 终端里一个 emoji 的宽度算对了吗？ | `TerminalView` 的 `CellMetrics` |

### 落地成什么

**把"可观察行为"写成断言，而不是把"源码长得像"当标准。**

具体的三种断言形态（本项目已有的）：

1. **固定样本往返**：用**迁移前生成的真实 JSON 字面量**硬编码，用新配置反序列化。
   `wire-cross-version-compat` 就是这条——自己序列化再自己反序列化是**测不出**
   格式变化的，因为两侧用同一份代码。

2. **线上文本断言**：断言实际的 JSON 字符串里有什么。
   `wire-enum-format`（camelCase 枚举）、`wire-null-omitted`（null 不出现在线上）。
   这两条都是 dsh/SDK 文档专门警告过的裂缝。

3. **数值逐条对齐**：`cols-*` 5 条、`dsh-tokens-*` 2 条、`titlebar-tokens-match-dsh`。
   不是"看起来差不多"，是逐像素、逐色值相等。

### 一个反例（本项目真实发生）

迁移到 `Dougong.HostProtocol` 时，我写了 `DesktopProtocol.Json` 却**一处都没用**——
所有收发仍走 SDK 的 `HostProtocolContract.Json`。

- 编译：通过
- 65 条检查：全过
- 运行时：桌面端特有消息（`fs/list`、`models/list`）**根本发不出去**
- 表现：模型目录一直是空的

**这是"编译通过"最典型的骗局。** 后来靠读运行时日志发现，不是靠任何检查。

教训：**"定义了但没用"这类问题，静态检查抓不住，必须看运行时行为。**

---

## 7.12 复检记录（2026-10-31）：抽象只切了一半

用户要求复检。按"问三问"（真的能用吗 / 检查能抓住真违规吗 / 有没有定义了没用的）查，
**发现一处真问题**：

### `IEngineLocator` 的抽象只做了一半

`EngineClient` 里还留着 `StartAgentHostAsync`（26 行）与 `InferAgentHostPath`（39 行），
内容与 `FileEngineLocator` 里的重复——**共 65 行死代码**。

- 编译：通过（两个方法都在，只是没人调用）
- 67 条检查：全过
- 功能：正常（走的是 locator 那条路）
- **只有肉眼看代码才发现**

这正是"抽象是装饰"的典型形态：接口加上了，旧实现没删。而漏着的
`File.*` / `Process.*` 调用正是 web 端会崩的地方。

### 新增检查 `platform-calls-stay-in-adapters`

断言两个应保持平台无关的文件里不出现 `File.` / `Directory.` / `Process.` / `OperatingSystem.`：

| 文件 | 为什么该纯净 |
|---|---|
| `EngineClient.cs` | 纯网络客户端，端点发现应由 `IEngineLocator` 负责 |
| `AppSettings` | 只是 JSON + 存储抽象，不该知道存到哪 |

**双向验证**：
- 反证（`--break`）：直接返回错误 → 判红 ✓
- **真实注入**：临时在 `EngineClient` 加 `File.Exists("x")` → 判红，并精确报出文件与那一行 ✓

### 复检结论

| 问 | 结论 |
|---|---|
| 三个抽象真的在用吗 | ✅ `ISettingsStore` 4 处、`IEngineLocator` 4 处、`DesktopProtocol.Json` 4 处，无死代码（删掉那 65 行之后） |
| 检查能抓住真违规吗 | ✅ 新检查经真实注入验证；反证与正向都有效 |
| 有没有定义了没用的 | ❌ 发现并已删除 65 行 |

检查总数 66 → 67。

---

## 7.13 Arc 新增的 41 个 AI 组件：哪些用、哪些不用

`0.3.2-preview.2` 的 Special 包里有 **132 个 `Ai*` 类型**（含新增的 41 个组件）。
我们已经在这个版本上，**不需要升级**。逐个判断：

### 值得用的三个

| 组件 | 对应我们哪个需求 | 决策 |
|---|---|---|
| `AiCommandPalette` | 现有 `CommandPaletteDialog` 的增强版（Tab 切 AI 提问模式） | **待评估**：是产品决策（要不要在 Cmd+K 里直接问 AI），不是技术升级 |
| `AiWorkflow` | dsh 的 `ui-workflow-run` | **用**：成品卡片（32px 行 + 1px 轨道 + 分支 tab），比用 `WorkflowCanvas` 自己拼强 |
| `AiStreamText` | 消息流式渲染 | **待评估**：我们是 `MarkdownStream`，它是逐块淡入的纯文本。要不要换取决于是否保留 Markdown |

### 明确不用：`AiMascot`

**用户要求宠物形象自己设计，而 `AiMascot` 只能改颜色、换不了形体。**

实测它的可配置项只有 `BodyColor` / `EyeColor` / `DotColor` / `ShowCompanion` / `ShowGround` / `Size` / `Tone` / `FollowPointer`。
`AiMascotCanvas` 直接绘制 "soft pebble with pill eyes + companion dot" 那个固定形状——
**想换成榫灵那只戴红檐斗拱帽的小匠人，办不到。**

### 宠物：用已有素材自己实现

素材位置：`dougong/artifacts/brand-character/sunling-pet-colored/`

| 资源 | 内容 |
|---|---|
| `svg/sunling-{idle,thinking,working,success,sleeping,dragged}.svg` | **六种状态各一份 SVG**，不需要精灵图 |
| `png/sunling-{...}.png` | 位图版本（1024 宽） |
| `sunling-state-keyframes.json` | 状态 → 坐标映射（400×400 帧，2 行 3 列） |
| `榫灵形象设定.png` / `角色设定.md` | 形象与性格设定 |

**实现方式**：一个自建 `PetView` 控件
- 按状态切 SVG（`Avalonia.Media.Imaging` + `SvgImage` 或预转 PNG）
- 六状态映射：`idle`←就绪 / `thinking`←模型思考 / `working`←工具执行 / `success`←回合完成 / `sleeping`←空闲超时 / `dragged`←用户拖拽
- 尊重 `prefers-reduced-motion`（素材说明里明确要求）
- 透明 SVG 绘制，可作为后续桌面悬浮资源

**归入 P8d（会话增强）**，不进 P8a/P8b。

---

## 7.14 全量检索 Arc 组件：按需求对照（2026-10-31）

检索范围：`0.3.2-preview.2` 三个包共 **5463 个公开类型**（Arc 877 / Blocks 1924 / Special 1045，
过滤掉辅助类后）。按 P8 各阶段的实际需求定向匹配，结论如下。

### 高价值：直接改善现有实现

| 组件 | 包 | 对应我们的需求 | 决策 |
|---|---|---|---|
| **`AgentStatusRunClock`** | Special | 状态胶囊的计时 | **立刻替换**。它的语义是"从运行开始到现在，**扣掉每段 paused 和 failed**"——我们手写的 `StatusCapsule` 只扣了审批等待，漏了失败段 |
| **`AgentTimeline`** | Special | P8d 的 `ui-trajectory` | **用成品**。原计划自己写"按轮次的事件记录表 + 时间概览"。它把 **thinking（淡色）/ tool calls（实心）/ waiting（斜纹）** 三种段用视觉区分，还带编排者与子 agent 的分行 |
| **`AiRelatedQuestions`** | Blocks | 消息流末端的"接下来问什么" | **待评估**。dsh 的 `ui-chat` 有这个能力，Arc 也是成品 |

### 中价值：取决于数据而非组件

| 组件 | 包 | 结论 |
|---|---|---|
| `AiDiffDocument` / `AiDiffText` | Blocks | **组件能做词级 diff，但我们没数据**。SDK 的 `FileVersion` 只存长度/时间/哈希，不存原文。这与之前 §9 的结论一致：缺口在数据侧，不在 UI 侧 |
| `AgentMemory` | Blocks | 对应 SDK 的 agent 记忆能力。**排在 P9 之后**，且 SDK 侧尚未接入 |

### 不用

| 组件 | 为什么不用 |
|---|---|
| `AgentLoop` | "agent 循环的动画解说"（环上三个站点：Gather context / Take action / Verify），是文档演示组件，不是运行时 UI |
| `AgentHandoff` | AI 转人工客服场景，我们不做 |
| `AgentCursor` | 多人协作编辑光标，单人场景不需要 |

### 一条方法论

**有组件 ≠ 能用。** 判断顺序应该是：
1. 我们有这个**数据**吗？
2. 组件的**形状**与我们的需求一致吗？
3. 只有前两条都成立，才考虑接入。

`AiDiffDocument` 就是第 1 条不成立的反例——组件再好，没有改动前后的原文就没法用。

---

## 8. 复刻验收方式

沿用既有判据——**控件树 dump 优于截图**：

- 三栏宽度必须等于 `computeColumns(视口宽, ...)` 的计算结果（逐像素）
- 侧边栏收起后必须是 **56px 且含图标**
- 断点：窗口宽 1023 时侧边栏自动收起；767 时变抽屉（不占列宽）
- 右栏在中栏不足 400 时整条消失

---

## 9. 由目标组件决定的偏差（逐条反馈）

> 这一节记的是**我们的 UI 框架做不到 dsh 那个数值**的地方。
> 不硬凑，如实记录差在哪、为什么、影响多大。

### 9.1 侧边栏收起宽度：dsh 56px vs Arc 64px

| 项 | dsh | 我们（TKCode.Arc `Sidebar`） |
|---|---|---|
| 收起轨道宽度 | **56px** | **64px**（写死，无可绑定属性） |
| 可调项 | — | 只有 `OpenWidth` 可绑定 |

**证据**：`TKCode.Arc.Blocks.xml` 里 `Sidebar.IsCollapsed` 的注释是
"Whether the sidebar is the 64px icon rail (two-way)"，且属性列表里
没有任何宽度相关的可绑定项用于收起态（`PaneWidth` 是只读的实际宽度）。

**影响**：整体宽度差 8px，中栏因此宽 8px。视觉上可见但不突兀。

**选项**：
1. 接受 8px 差异（推荐——用 Arc 的 `Sidebar` 换来品牌行/筛选/折叠动画全部现成）
2. 自建侧边栏控件，精确复刻 56px。代价：重写约 400 行，并失去 Arc 的折叠动画与无障碍处理

**当前选择**：方案 1，接受 8px。

### 9.2 待确认的其他偏差

| 项 | dsh | 我们 | 状态 |
|---|---|---|---|
| 折叠动画 | CSS 过渡 | Arc 的 morph spring | 视觉接近，时长不同 |
| 会话行圆角 | 12px（`radius-md`） | Arc Section 内建 | 待核 |
| 品牌行高 | 52px | Arc `Header` | 待核 |

---

## 11. 三个曾被漏掉的 Arc 包（2026-10-31 实测）

> **为什么漏了**：此前只用 `Directory.Packages.props` 里已有的 4 个 Arc 包
> （Arc / Fonts / Special / Blocks），没有检索整个仓库。实际有 **17 个包**。

### 11.1 `TKCode.Arc.Terminal` —— 终端不用自建

**命名空间是 `TKCode.Terminal.*`，不是 `TKCode.Arc.*`**（容易找错）。

| 项 | 实测结果 |
|---|---|
| `TerminalView` | `TKCode.Terminal.Avalonia.TerminalView : Control` |
| 自有属性 | `Snapshot` / `FontFamily` / `FontSize` / `Padding` / `Metrics` / `Preedit` / `CursorRectangle` |
| 输入事件 | `KeyPressed` / `TextEntered` / `PointerChanged` |
| 内核 | `TerminalCore.Create(options, effects)` —— 构造函数是 internal |
| 原生库 | `libghostty-vt.{so,dylib,dll}`，随包生成到 `runtimes/*/native/` |

**实测通过的链路**：

```csharp
var size    = new TerminalSize(24, 80, 10, 20);
var options = new TerminalCoreOptions(size);
var core    = TerminalCore.Create(options, new NullTerminalEffects());

core.Write(Encoding.UTF8.GetBytes("hello world

second line"));
core.FormatPlainText()  // → "hello world
second line"
core.Write(Encoding.UTF8.GetBytes("[31mRED[0m normal"));  // ANSI 正常
core.CaptureFrame(false) // → FrameSnapshot
```

**结论**：VT 解析、ANSI 颜色、帧捕获全部工作。**P8f 的终端只需接 PTY 进程 + `TerminalView`**，不用自建渲染。
此前估"需 ConPTY 自建约一阶段工作量"是错的。

**剩余工作**：接 PTY（Windows 用 ConPTY，Unix 用 forkpty）+ 双向字节流。
渲染、输入编码、IME、选择、剪贴板都在包里。

### 11.2 `TKCode.Arc.Workflow` —— 画布可用

`WorkflowCanvas` 属性（37 个）包含：`Graph` / `Zoom` / `Offset` / `MinZoom` / `MaxZoom`
/ `AutoFit` / `NodeTemplates` / `EdgeStyle` / `LayoutDirection` / `SnapToGrid`
/ `Virtualizing` / `UndoCommand` / `RedoCommand` / `DeleteSelectionCommand`
/ `SelectedNodes` / `SelectedEdges` / `IsEditable` / `ShowGrid`。

`WorkflowGraph` 有无参构造。

**用途**：dsh 的 `ui-workflow-run`（工作流运行节点）可直接用它。
`ui-dockkit`（分裂标签树）只能部分覆盖——画布有了，分裂树仍要自建。

### 11.3 `TKCode.Arc.Icons.Lucide` —— 2155 个图标

```csharp
Lucide.Geometry(LucideIconKind.X)   // → Geometry
Enum.GetValues<LucideIconKind>().Length == 2155
```

图标不用自己画。

---
