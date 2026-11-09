# TKCode.Arc 1.0.0 组件盘点

> **这份文档回答什么**：升到 1.0.0 之后，哪些组件能用、哪些正好补上我们的缺口、
> 哪些看着有用但用不了。
>
> 数据来源：**按 DLL 元数据扫的**，不是照文档站猜的。
> 2026-11-09 实测 `1.0.0` 全部 13 个包。

---

## 一、升级结论：向后兼容，零删除

按 `public` 顶层类型逐名比对 `0.4.0-preview.1` → `1.0.0`：

| 包 | 0.4.0 | 1.0.0 | 新增 | 删除 |
|---|---|---|---|---|
| `TKCode.Arc` | 951 | 1038 | +87 | **0** |
| `TKCode.Arc.Special` | 860 | 1039 | +179 | **0** |
| `TKCode.Arc.Blocks` | 1133 | 1275 | +142 | **0** |
| `TKCode.Arc.Terminal` | 77 | 77 | 0 | **0** |

**四个包全部零删除**，所以不需要改任何现有代码。实测印证：
升级后构建 0 警告 0 错误，91 条检查全过，界面截图与升级前逐像素一致。

Avalonia 仍精确锁 `[12.1.4]`，无需调整。

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

## 四、1.0.0 新增里真正值得用的

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

**建议**：换掉 `MainWindow : Window` 与 `AppShell.BuildTitlebar` 里手写的那套。
预计能删掉平台分支与 80px 常量，并去掉一处"靠截图估算"的技术债。
**没实测过**，换之前要单独验一次 macOS 红绿灯位置。

### 4.2 `DockLayout` / `DockSplit` / `DockGroup`（`TKCode.Arc.Dock`）

`0.4.0-preview.2` 才上 NuGet（我们停的 `preview.1` 里没有；llms.txt 也还写着
"not on NuGet yet"）。含 `DockPane`、`DockSplit`、
`DockGroup`、`DockDocumentArea`、`DockAutoHide*`、拖拽请求模型。

**这正好是 P8e 的 E4「dockkit 分裂标签树」。** 进度表原来写的是
"画布用 `WorkflowCanvas`，分裂树自建"——现在分裂树也有成品了。

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

## 五、看着该修但**没修**的：终端

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
