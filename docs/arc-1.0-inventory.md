# TKCode.Arc 组件盘点（当前 1.1.0）

> **这份文档回答什么**：当前版本哪些组件能用、哪些正好补上我们的缺口、
> 哪些看着有用但用不了。
>
> 数据来源：**按 DLL 元数据扫的**，不是照文档站猜的。
> 文件名保留 `arc-1.0` 是历史原因（多处文档引用），内容跟版本走。

---

## 〇、1.0.0 → 1.1.0（2026-11-16）

### 版本约束变了：Avalonia 必须一起升

| | 1.0.0 | 1.1.x |
|---|---|---|
| Arc 的 Avalonia 依赖 | `[12.1.4]` **精确锁定** | `[12.1.5, 13.0.0)` **区间** |

所以 `1.0.0 → 1.1.0` 是**成对**的：`Avalonia 12.1.4 → 12.1.5` + 八个 Arc 包。
只升一边会 NU1605。

> **1.1.1-preview.2 起 Avalonia 不用再动**：nuspec 的约束仍是
> `[12.1.5, 13.0.0)`，与 1.1.0 完全一致（逐包核过），所以只升 Arc 八个包。

Spyglass 0.2.3 的 nuspec 写的是裸 `12.1.4`，NuGet 语义等于 `>= 12.1.4`，
因此与 12.1.5 共存（实测 restore 通过，不是推断）。

### 元数据 diff

按公开类型与成员逐名比对（八个包）：

| 包 | 公开类型 | 新增 | 删除 |
|---|---|---|---|
| `TKCode.Arc` | 1040 → 1184 | +144 | **0** |
| `TKCode.Arc.Blocks` | 1277 → 1903 | +626 | **0** |
| `TKCode.Arc.Special` | 1041 → 1296 | +255 | **0** |
| `TKCode.Arc.Core` | 152 → 153 | +1 | **0** |
| Dock / Workflow / Terminal / Icons.Lucide | 不变 | 0 | **0** |
| **合计** | | **+1026** | **0** |

另 **54 处类型成员有变，全部是纯增（`+n/-0`）**，没有一处删成员或改签名。

> **踩到一个假阴性，值得记**：第一版 diff 报「新增 771 / 删除 0」。
> 那是假的——`Special` 包引用同版本的 `TKCode.Arc.dll`，
> 而导出器没拿到它，`GetExportedTypes` 整体失败，脚本把空表当"无变化"跳过，
> 于是 Special 的 255 个新增全被漏掉，还差点被当成"AgentTimeline 被删了"。
> 修法是给导出器传入**同版本兄弟包目录**，并把"导出为空"与"无变化"分开报错。
> 教训：**工具报 0 变化时先证明它能报出非 0**。

### 唯一的破坏性变更

`CommandPaletteDialog.ItemSelected` 弃用 → 改用 `ItemInvoked`（CS0618，本项目按错误处理）。
弃用说明写明两者会**同时触发**，所以换掉既不会漏调也不会重调。

### 视觉回归：逐像素比对

无头渲染 `MainWindow`（`Avalonia.Headless` + `UseSkia`，因本机 `-6661` 起不了真窗口），
升级前后各截一张 1280×820：

- 差异像素 **479 / 1,049,600 = 0.046%**
- 差异区域只有 `(10,798)` 起 67×12 一块——状态栏的「已连接」vs「启动引擎…」
- 其余**逐像素一致**（布局、配色、字体、间距全没动）

### 终端缺陷：1.1.x 全系未修

用同一份 7 行最小复现重测：

```
Version = 0.1.0-dev
Create FAILED: NullReferenceException
```

和 0.4.0-preview.1、1.0.0 完全一样。**F1 继续卡着**（F2 渲染控件不受影响）。
升级前先验这条是既定做法——不然升完发现还是坏的更浪费时间。

### 1.1.0 新增里值得注意的

| 组件 | 包 | 与本项目的关系 |
|---|---|---|
| `ToggleGroup` / `ToggleGroupItem` / `ToggleGroupOption` | Arc | 多个独立开关成组。设置页若有多选用途 |
| `TreeSelect`（29 个属性） | Arc | 树形单选/多选下拉，带搜索保留路径 |
| `RadioCard` / `RadioGroupItem` | Arc | 需要解释的选项卡片——权限预设可能更适合它 |
| `Form` / `FormItem` | Arc | 表单容器 + 校验布局 |
| `SplitBar` / `SplitBarSegment` | Arc | 占比条，可悬停读数字、图例开关 |
| `Rating` / `RulerSlider` / `RangeHistogramSlider` | Arc | 装饰性/统计类，暂不需要 |
| `PresenceStack` | Arc | 多人头像堆叠，本项目无多人场景 |
| `BackToTop` | Arc | 回到顶部——会话滚动可以用，但我们已有自己的回到底部 |
| `SlotText` / `MetricTicker` | Arc | 数字滚动动画，装饰性 |
| `OnScreenKeyboard` | Arc | 触屏键盘，桌面端不需要 |
| `RadialMenu` / `OrbitMenu` | Arc | 环形菜单，不符合 dsh 形态 |
| `PageHeader`（新增 `IsTitleBar` / `IsHeaderOnly` / `Size`） | Blocks | **`IsTitleBar` 可能替代我们手搭的标题区** |
| `SettingsRow`（新增 `Leading` / `Details`） | Blocks | **设置行现在支持前置图标 + 详情区**，C3 可用 |
| `SettingsSection`（新增 `Value` / `BadgeTone`） | Blocks | 分区可带值摘要与徽章 |
| `Sidebar`（新增 `SelectionChanged` / `ExpandedWidth` / 成员可见性） | Blocks | 选中事件是我们缺的；成员 `IsVisible` 可做筛选 |
| `AgentTimeline`（17 个类型，**1.0.0 就有，不是新增**） | Special | D4 Trajectory 视图可用它——但需先验证可实例化 |
| `AgentRun` / `AgentTasks` / `AgentApprovals` / `McpServers` | Blocks | 与 P8d 后台任务、MCP 场景相关，**偏 SaaS 模板形态**，需评估形状 |
| `TypewriterTerminal` / `TypewriterTerminalLine` | Special | 打字机终端效果——dsh 的终端不是这个形态，不用 |
| `ToolDock` / `ToolDockItem` | Special | 底部工具坞，dsh 没有这个交互 |
| `EmojiPicker` / `KeycapKeyboard` / `ConfirmPopover` / `UndoToast` | Special | 通用小件，按需 |

Blocks 新增的 626 个里绝大多数是整页营销/SaaS 模板
（`InvoiceStudio`、`KnowledgeBase`、`SeatMap`、`BookingPill`、`SupportWidget` …），
与桌面客户端形态不匹配，**不采用**。

### 1.1.0 → 1.1.1-preview.2（2026-11-16）

| 包 | 公开类型 | 新增 | 删除 |
|---|---|---|---|
| `TKCode.Arc` | 1184 → 1189 | +5 | **0** |
| `TKCode.Arc.Blocks` | 1903 → 1949 | +46 | **0** |
| Special / Core / Dock / Workflow / Terminal / Icons.Lucide | 不变 | 0 | **0** |
| **合计** | | **+51** | **0** |

**22 处类型成员有变，全部是纯增（`+n/-0`）**，零删除。

新增的 51 个里没有一个是新的顶层控件——都是既有组件的成员增强：

| 类型 | 新增 |
|---|---|
| `PrizeWheel` | +47（`CodeCopied` / `SelectionChanged` 事件、`Charging`/`Claimed` 伪类、`Code` 属性等） |
| `SandText` | +10（`DigRadius` / `Palette` / `HeadingLevel` 等） |
| `TocProgress` | +6（`ReadingOffset` / `ReadingTime` / `ShowsTimeLeft`） |

都属装饰性/演示品类，本项目不用。

**视觉回归**：无头渲染逐像素比对 1.1.0 vs preview.2，
差异 **479 / 1,049,600 = 0.046%**，与上次升级到 1.1.0 时**完全相同的那一块**
（状态栏「已连接 / 启动引擎…」，连接时序差异），其余逐像素一致。

**API 状态复核**：`CommandPaletteDialog.ItemSelected` 在 preview.2 仍标
"The former name of …"，弃用关系没变，我们用的 `ItemInvoked` 依然正确。

**终端缺陷复测**：同一份 7 行最小复现仍抛 `NullReferenceException`
（`Version = 0.1.0-dev` 握手正常）。**1.1.1-preview.2 也没修**，F1 继续卡着。
每升一次都重跑这条是既定做法——不重跑就等于拿旧结论代替验证。


---

### 1.1.1-preview.2 → 1.3.0（2026-11-20）

**Avalonia 必须一起升**：1.3.0 的依赖从 `[12.1.5, 13.0.0)` 提到
**`[12.1.6, 13.0.0)`**，所以 `Avalonia 12.1.5 → 12.1.6`。

| 包 | 公开类型 | 新增 | 删除 |
|---|---|---|---|
| `TKCode.Arc` | 1189 → 1161 | 0 | **28** |
| `TKCode.Arc.Blocks` | 1920 → 1941 | +21 | 0 |
| `TKCode.Arc.Special` | 1325 → 1357 | +32 | 0 |
| `TKCode.Arc.Terminal` | 77 → 87 | +10 | 0 |
| `TKCode.Arc.Core` | 153 → 154 | +1 | 0 |
| Dock / Workflow / Icons.Lucide | 不变 | 0 | 0 |
| **合计** | | **+64** | **28** |

#### 那 28 个"删除"是搬家，不是消失

主包里少了 `Markdown`、`MarkdownHighlightPlugin`、`MarkdownServices`
与整个 `Chat*` 系列（`ChatThread` / `ChatComposer` / `ChatMessage` …）
以及它们的 token 类。

**`Markdown` 拆成了独立包 `TKCode.Arc.Markdown` 1.3.0**——
它作为 `TKCode.Arc` 的依赖被自动引入，所以 `new Markdown()` 照常编译。
实测：`typeof(Markdown).Assembly.GetName().Name == "TKCode.Arc.Markdown"`。

> **因此把它改成显式引用**：我们直接 `new Markdown()`，靠传递依赖意味着
> 上游再拆一次就会编译失败。`Chat*` 系列我们没用，不处理。

`MarkdownServices` 是**真的没了**（同包里查不到）。我们没用它，无影响。

> **踩到的坑，值得记**：元数据 diff 报"删除 28"，我一度以为要停下来。
> 但**编译器没报错**——两个信号矛盾时，先信编译器，再查清为什么。
> 导出器只覆盖单一程序集，看不到类型搬到了哪个包；
> 用 `typeof(X).Assembly` 一问就清楚了。

**视觉回归**：无头渲染逐像素比对，差异 **479 / 1,049,600 = 0.046%**，
与上两次升级**完全相同的那一块**（状态栏「已连接/启动引擎…」，
连接时序），其余逐像素一致。

**终端缺陷复测**：同一份 7 行最小复现仍抛 `NullReferenceException`
（`Version = 0.1.0-dev` 握手正常）。**1.3.0 也没修**，F1 继续卡着。

**1.3.0 新增里可选用的**：

| 组件 | 包 | 说明 |
|---|---|---|
| `CodeReview` 系列（9 个类型） | Blocks | 代码审查：diff 行、评论线程、建议、PR 元数据。P8d 交付物卡片可能用得上 |
| `TerminalView` +34 个成员 | Terminal | 光标闪烁、下划线/删除线度量、自定义字形、渲染层——**但 `TerminalCore.Create` 仍抛 NRE**，F1 依旧卡着 |
| `ArmingSwitch` / `CelebrateButton` / `FeedbackMorph` / `FeedbackMood` | Special | 交互特效件，与桌面客户端形态不匹配，不用 |
| `CodeMagicMove` | Special | 代码块动画，dsh 没有这个形态 |

---

## 一、0.4.0 → 1.0.0 那次的结论（历史，保留作方法参考）

按 `public` 顶层类型逐名比对 `0.4.0-preview.1` → `1.0.0`：

| 包 | 0.4.0 | 1.0.0 | 新增 | 删除 |
|---|---|---|---|---|
| `TKCode.Arc` | 951 | 1038 | +87 | **0** |
| `TKCode.Arc.Special` | 860 | 1039 | +179 | **0** |
| `TKCode.Arc.Blocks` | 1133 | 1275 | +142 | **0** |
| `TKCode.Arc.Terminal` | 77 | 77 | 0 | **0** |

**四个包全部零删除**，所以不需要改任何现有代码。实测印证：
升级后构建 0 警告 0 错误，91 条检查全过，界面截图与升级前逐像素一致。

Avalonia 当时仍精确锁 `[12.1.4]`，无需调整（**1.1.0 起改为 12.1.5**，见 §〇）。

> 口径限于**类型名**；成员签名未逐个比对。`TerminalView` 单独查过：
> 只多出 `CachedRowCount` / `GlyphOverride` / `RowRunBuilds` 三个属性，无移除。

---

## 二、可实例化控件：363 个

| 包 | 控件数 |
|---|---|
| `TKCode.Arc` | 135 |
| `TKCode.Arc.Blocks` | 93 |
| `TKCode.Arc.Special` | 86 |
| `TKCode.Arc.Core` | 19 |
| `TKCode.Arc.Mobile` | 16 |
| `TKCode.Arc.AdvancedCharts` | 7 |
| `TKCode.Arc.Workflow` | 4 |
| `TKCode.Arc.Dock` | 2 |
| `TKCode.Arc.Terminal` | 1 |
| **合计** | **363** |

（0.4.0 是 313，净增 50。统计口径：`Visibility == Public` 的顶层类型，
基类是 Avalonia 控件，且排除 `*Token` / `*Options` / `*EventArgs` 等辅助类型。）

---

## 三、我们目前在用的（15 个）

判据：产品代码里真的构造或使用，**不含 `ArcProbeWindow`** 那个探针窗口。

| 组件 | 包 | 用在哪 |
|---|---|---|
| `Sidebar` | Blocks | 左栏导航 |
| `Tabs` / `TabItem` | Arc | 右栏 dtype 标签 |
| `Button` | Arc | 通用按钮 |
| `ToolCall` | Special | 工具卡（9 种 SDK 状态映射） |
| `CommandPaletteDialog` | Blocks | Cmd+K |
| `AgentStatus` / `AgentPlan` / `AgentPlanStep` | Special | 状态胶囊 / 计划面板 |
| `Markdown` | Arc | 消息正文与流式渲染（含代码块） |
| `AiComposer` / `AiAttachment` / `AiCommand` | Blocks | 输入区与斜杠命令 |
| `LucideIcon` | Icons.Lucide | 图标 |
| `TerminalView` | Terminal | 右栏终端 tab（渲染层已接，见 §五） |

---

## 四、1.0.0 新增里真正值得用的（历史）

### 4.1 `ArcWindow` + `TitleBar` —— 该替换我们的手写实现

**这是本次升级最重要的发现。** 我们手写的标题栏有两个已知问题：

1. macOS 红绿灯安全区是**猜的 80px**（`Columns.MacTrafficLightInset`），
   当初只能靠截图估算，控件树证明不了"没被压住"。
2. Windows 上要自绘 —/□/✕，还要按平台分支。

`ArcWindow` 的文档原话：

> A desktop window whose client area extends into the title bar, with an Arc
> `TitleBar` drawn at the top. ... The window keeps resizing and, **on macOS,
> the traffic lights**.

它直接处理了交通灯。`TitleBar` 提供 `LeftContent` / `CenterContent` /
`RightContent` 三个插槽加 `CaptionButtons`，Windows 上还会走系统标题栏
（系统菜单、Aero Snap、Snap Layouts）。

**已采用（2026-11-09）**。实测过程与结果：

1. 先建**最小探针窗口**（只 new 一个 `ArcWindow`，塞 LEFT/CENTER/RIGHT）验证可行性。
   截图确认：交通灯在最左、`LEFT` 在其右侧约 24px 处未被压住、无自绘 —/□/✕。
2. 再改产品代码，把 `MainWindow` 的基类换成 `ArcWindow`。

**删掉的东西**：
- `Columns.MacTrafficLightInset = 80`（截图估的魔数）
- `AppShell.BuildTitlebar` 里的 `OperatingSystem.IsMacOS()` 平台分支
- `BuildWindowButtons()` 与 `TitleButton()`（约 100 行自绘窗口按钮，全成死代码）
- `AppShell` 根布局里的标题栏行（`Auto,*,Auto` → `*,Auto`）

**踩到的坑（值得记）**：第一次改完是**两套标题栏叠在一起**——`ArcWindow`
自带一条，`AppShell` 又建了一条。源码里看不出异常，是运行时诊断
`标题栏按钮 7 个`（多出 4 个 0x0）暴露的。已加无头检查
`titlebar-uses-arc-slots` 守这个。

### 4.2 `DockLayout` / `DockSplit` / `DockGroup`（`TKCode.Arc.Dock`）

`0.4.0-preview.2` 才上 NuGet（我们停的 `preview.1` 里没有；llms.txt 也还写着
"not on NuGet yet"）。含 `DockPane`、`DockSplit`、
`DockGroup`、`DockDocumentArea`、`DockAutoHide*`、拖拽请求模型。

**这正好是 P8e 的 E4「dockkit 分裂标签树」。** 进度表原来写的是
"画布用 `WorkflowCanvas`，分裂树自建"——现在分裂树也有成品了。

**已接入（2026-11-09）**：右栏 5 个面板改成 `DockPane`
（文件/改动/计划/上下文走 `ToolsSource`，终端走 `DocumentsSource`）。
运行时实测 `pane 5 个，根=DockSplit`，无头与真实 GUI 都确认。
用 `DOUGONG_UI_DOCK=1` 开关控制，**默认仍走平铺 `Tabs`**——
Dock 多出拖拽区、关闭按钮、浮动窗口，交互路径变了，要先做视觉验收再切默认。

踩到一个 `Button` 命名冲突：`using TKCode.Arc;` 之后与
`Avalonia.Controls.Button` 撞车（CS0104），正是 llms.txt 警告的那条。
已用 `using ArcButton = TKCode.Arc.Button;` 别名解决。

### 4.3 `PageViewer` + `IPageSource`（Arc 主包）

缩放、平移、翻页；`IPageSource` 支持按需渲染页面；只画可见页
（"一万页的文档不比短文档贵"）；`Layers` 可在页上覆盖可交互图层。

**对应 P8e 的 E7 文档预览**里的 PDF 部分。另有独立 `TKCode.Arc.Pdf` 包，
只发过 `1.0.0` 这一个版本。

### 4.4 其余新组件（按用途归类）

| 组件 | 包 | 可能的用途 |
|---|---|---|
| `FluidTabControl` | Arc | 滚动联动标签（"settings pages, long forms"）——可用于 P8c 设置页的长表单 |
| `EnumSelector` | Arc | 枚举选择器，`[Flags]` 自动处理。**配置类界面省事** |
| `TransferBox` | Arc | 穿梭框。插件启停、模型选择都可能用 |
| `WaterfallPanel` / `TilePanel` / `CyclicPanel` | Arc | 三种布局面板，可直接当 `ItemsPanel` 用 |
| `FilterBox` / `NumberFilterBox` / `TextFilterBox` | Arc | 带操作符的筛选框，适合列表/日志 |
| `GanttTimeline` | Blocks | 甘特图。若 P8d 要做任务视图可用 |
| `ControlCenter` / `ChargeSession` / `NowPlaying` / `LinkUnfurl` / `HoloCard` / `CardToArticle` / `BlackHole` / `MorphLoader` | Special | 装饰性/场景化，**与桌面端需求不匹配，不用** |

---

## 五、看着该修但**没修**的：终端（1.1.0 复测仍坏，见 §〇）

**1.0.0 没有修复 `TerminalCore.Create` 的崩溃。** 用同一份 7 行最小复现重测：

```
Version = 0.1.0-dev          ← 版本握手正常
Create FAILED: NullReferenceException
```

也就是说 F1 继续卡着。**不要以为升级就能解决**——这是我这次升级前
特地先验的一条，否则升完发现还是坏的会更浪费时间。

F2（`TerminalView` 控件本身）不受影响，它已经接进右栏 dock 了。

---

## 六、判断方法（沿用）

**有组件 ≠ 能用。** 三步，缺一不可：

1. **它 `public` 吗？** 查 DLL 元数据的 `Visibility`。
   XML 文档里有 `<member>` 条目**不代表**能实例化——
   0.4.0 那次就有 4 个推荐组件全是 `internal`。
2. **我们有数据吗？** 组件再好，喂不进真数据就只能造假。
3. **形状对得上吗？** 例：`ResizablePanels` 用百分比，
   而 dsh 的列宽是像素语义，我们三栏因此没用它。

本次盘点的每一项都过了第 1 步；第 2、3 步在"建议"里单独注明。

---

## 七、下一步建议

按"能删掉多少技术债"排序：

1. **换 `ArcWindow` / `TitleBar`** —— 能删掉平台分支 + 80px 魔数，
   并消掉一处"靠截图估算"的欠账。要单独验 macOS 红绿灯。
2. **`TKCode.Arc.Dock` 补 E4** —— 分裂标签树从"自建"变"用成品"。
3. **`PageViewer` 补 E7 的 PDF 部分**。
4. 终端等上游修，或自己封 `ghostty_terminal_new` 绕开 `TerminalCore`。
