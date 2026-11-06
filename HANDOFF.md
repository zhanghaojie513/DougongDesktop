# 交接文档（切换到 macOS 继续开发）

> **这份文档写给"在 Mac 上重新打开这个项目的下一个会话"**。
> 目标：读完之后能立刻构建、跑检查、知道现在停在哪、下一步做什么。
>
> 写于 2026-11-05。Windows 侧最后一次全绿：82 条；**Mac 上已验证：83 条**。

---

## 一、这个项目是什么

**Dougong Desktop**：以 `Dougong SDK` 为 agent 框架、`TKCode.Arc` 为 UI 框架的
桌面客户端示范项目，UI 与功能按 **1:1 复刻 dsh 桌面端**
（`deepseek-ai/deepseek-harness`）推进。

四层架构，两条硬禁令（由架构检查守卫）：

```
App (Avalonia UI)  ──禁引 SDK──►  Ipc (纯契约)  ◄── Framework (唯一知 SDK)
                                        ▲
                                   AgentHost (引擎进程)
```

- **App 不得引用 SDK**
- **Framework 不得引用 Arc**

因此 "SDK 数据 → UI 类型" 只能走 IPC 纯数据。改动时越界会被
`app-does-not-reference-sdk` 与 `sdk-side-has-no-ui-dependencies` 拦下。

---

## 二、Mac 上第一次跑起来

### 1. 前置

```bash
dotnet --version     # 需要 .NET 10 SDK（global.json 钉 10.0.402，rollForward latestFeature）
python3 --version    # 只有改配色 token 时才需要
```

> **注意事项**：Homebrew 装的 SDK 在 `/usr/local/share/dotnet`，
> 默认可能不在 PATH 里。报 `command not found: dotnet` 时先
> `export PATH="/usr/local/share/dotnet:$PATH"`，或写进 `~/.zshrc`。
> 实测 10.0.403 可用（`rollForward: latestFeature` 覆盖 10.0.4xx）。

### 2. 构建与自检

```bash
cd <repo>
scripts/rebuild.sh DougongDesktop.slnx Release
scripts/all-checks.sh
```

**期望**：`ALL CHECKS PASS`，83 条（正向 83 / 反证 83）。

`all-checks.sh` 做两件事——正向跑一遍，再用 `--break=<slug>` 逐条破坏，
确认**每条检查真的会红**。反证不生效会被判 FAIL（"检查抓不到真违规"）。

### 3. 跑起来看界面

```bash
src/DougongDesktop.App/bin/Release/net10.0/DougongDesktop.App
```

无 provider 时用自检数据看渲染：

```bash
DOUGONG_UI_SELFTEST=1 src/DougongDesktop.App/bin/Release/net10.0/DougongDesktop.App
```

### 4. 环境变量一览（自检开关）

| 变量 | 作用 |
|---|---|
| `DOUGONG_UI_SELFTEST=1` | 灌入 13 条样本消息（含审批、流式、超长工具输出） |
| `DOUGONG_UI_APPROVAL=1` | 走真实 `ShowApproval` 路径，验证输入区待决锁 |
| `DOUGONG_UI_STRESS=N` | 灌 N 条，验证虚拟化（1000 条只建少量容器） |
| `DOUGONG_UI_NAV=1` | 驱动目录导航：根 → `src/` → 回退，验证累加与复位 |
| `DOUGONG_UI_RUNNING=1\|2` | `1` 只看运行状态行；`2` 走完整流程（起→停→标已停止） |
| `DOUGONG_UI_SCROLL=end` | 滚到底再 dump（验证贴底与尾部条目） |
| `DOUGONG_UI_TOOLFIRST=1` | **把工具条目前置**，强制它们进视口——验证工具卡构建路径 |

**输出全在 stderr**。诊断行带 `OK` 或 `（**…**）` 标记，扫一眼就知道哪项坏了。

---

## 三、Mac 适配——Windows 侧已改掉的三处

这三处是切到 Mac 之前**为跨平台专门修的**（Mac 上实测确认有效）。
Mac 上又新发现的三处见 §4.2 之后的"四之二"节。

### 1. 脚本里的 `tasklist` / `taskkill`

`scripts/rebuild.sh` 与 `scripts/all-checks.sh` 原来只用 Windows 命令杀进程。
Mac 上 `tasklist` 不存在 → 条件静默为假 → **进程杀不掉、DLL 被占用** →
构建报错但脚本继续跑，看到旧产物。现已按 `uname -s` 分支：Windows 走
`tasklist/taskkill`，其他走 `pkill -f`。

### 2. 写死的 Windows 字体

4 处写的是 `"Geist Mono, Consolas, monospace"`。Windows 上 Geist 先命中所以看不出问题；
macOS 上没有 Consolas，**静默回退到系统 monospace**——字形变、行宽变，
等宽对齐的键值预览会错位。

已收成 `DshTheme.MonoFont`（`fonts:GeistMono, Geist Mono, Menlo, monospace`）。
`WithArcFonts()` 在 `Program.cs` 里已注册内嵌 Geist Mono，随包分发，三平台一致。
新增检查 `ui-fonts-work-on-macos` 钉住这条（拦 Consolas / Segoe UI / 微软雅黑 / Cascadia）。

### 3. 引擎可执行文件名

`IEngineLocator.cs:131` 已按 `OperatingSystem.IsWindows()` 补 `.exe`，**这处原本就对**。

---

## 四、Mac 验证结果（2026-11-05 已实测，结论取代原"待验证"清单）

**Mac 上已跑通**：Release 构建 0 警告 0 错误，**83 条检查全绿**（正向 83 / 反证 83 对齐）。
下面逐条记录实测结论。

### 4.1 窗口装饰 —— 已修（原"风险最高"）

`MainWindow.cs:79-80` 仍开 `ExtendClientAreaToDecorationsHint = true`。
**macOS 上红绿灯确实叠在自绘内容上**，位置由系统决定——不分支就会压住左侧
「收起侧栏」图标（下图的分支修掉的正是这个）。

已按平台分支修掉（`AppShell.BuildTitlebar`）：

| | Windows | macOS |
|---|---|---|
| 左侧内边距 | 8px | **80px**（`Columns.MacTrafficLightInset`） |
| 自绘 —/□/✕ | 有 | **无**（系统红绿灯已提供） |

自检实测：`标题栏按钮` 从 **6 个降到 3 个**（剩下的 3 个是「收起侧栏 / 应用 / 编辑」，不是窗口按钮）。
新增检查 `titlebar-branches-for-macos` 钉住这条，反证已验证能变红。

**视觉已确认**（2026-11-06 截图复核）：红绿灯在最左，与「收起侧栏」图标之间
有肉眼可见的空隙，图标未被压住；标题栏右侧干净，没有残留的自绘 —/□/✕。
80px 这个估算是够用的。

> 若日后再改标题栏布局：单独截 `screencapture -x` 复核一次。
> 控件树只能证明分支生效，证明不了"没被压住"——这次的结论来自像素，不是控件树。

### 4.2 毛玻璃 —— 比预期好，没有降级

```
[shell] 毛玻璃 请求=AcrylicBlur/Blur/None 实际=AcrylicBlur（生效）
```

**HANDOFF 原来预测"很可能降级为 None"，实测没有。** macOS 上 Avalonia 原生支持
AcrylicBlur，配色不需要按不透明底重调。

### 4.3 滚动条外观 —— 仍属已知偏差

未改动。macOS 默认浮层式滚动条与 dsh 的常驻 5px 细条不同。
这是**超出预期外的偏差**，不影响功能。

### 4.4 DumpLayout 会输出两组数据，这是预期


窗口宽度不变（1280），但自检期间 `DumpLayout` 被调用两次，**右栏状态不同**：

```
[shell] 窗口 1280 x 820
[shell] Conversation.Scroll  1000.0 x 354.0   ← 第一次：右栏还没展开
...
[shell] 窗口 1280 x 820
[shell] Conversation.Scroll   424.0 x 354.0   ← 第二次：右栏展开后中区被压窄
```

**第二次才是最终状态**。第一次在 `ConnectEngineAsync` 之前，
布局尚未就绪，所以带 `（布局未完成，不判定）` 的条目是正常的，不是 bug。

判断布局有没有问题，只看**第二次** dump。

---

## 四之二、Mac 上发现并修掉的三个问题
这三处都是 **Windows 上不可能发现**的（要么没暴露，要么行为不同）。

### 1. 脚本没有可执行位（macOS 直接跑不了）

`scripts/*.sh` 在 git 里是 `100644`。Windows 上用 `bash script.sh` 调用所以看不出来，
Mac 上直接 `./scripts/rebuild.sh` 就是 `permission denied`。

已 `git update-index --chmod=+x` 改成 `100755`（三个脚本都改了）。

### 2. `.py` 工作区是 CRLF（仓库里是 LF）

`.gitattributes` 已规定 `*.py eol=lf`，但 Windows 检出留下的工作区文件是 CRLF。
`python3` 会因为 shebang 后的 `\r` 报错。已重新检出修正。

### 3. `all-checks.sh` 的反证清单漏了 4 条检查

**这是本轮最值得记的一条**：正向 82 条全 PASS、脚本打印 `ALL CHECKS PASS`，
但反证区只跑了 78 条。漏掉的 4 条（`settings-store-is-swappable`、
`platform-calls-stay-in-adapters`、`run-clock-excludes-failure`、
`run-clock-counts-waiting`）**从未被证明能变红**。

按 §8.4 的道理，等于这 4 条从没被验证过。已补上，并加了一道自保：
正向输出里的每一条 slug 都必须出现在反证清单里，否则脚本 exit 1。
自保已验证——故意删一条清单项，脚本报
`FAIL（以下检查没有反证： sdk-files-stay-contained ）` 并返回 1。

### 另外：两条会误导人的诊断已修

- `ResizablePanel 数量 0（期望 3）` —— 三栏早就改成 Grid + 像素求解
  （`AppShell.cs:287`），"期望 3"是**过时断言**，会让人以为布局退化了。
  改成 `（预期 0：三栏是 Grid+像素求解）`。
- `ToolCall 0 张` —— **0 张不等于坏了**。消息流的视口只有 353px，
  排在中间的工具卡本来就进不了视口，虚拟化不构建它。
  实测：前置工具条目后 5 张全部渲染，Input/Duration/Summary 都对。
  诊断已补上数据侧条数与提示。新增 `DOUGONG_UI_TOOLFIRST=1` 走这条验证路径。

---

## 五、当前进度

### 总览（83 条检查全绿）

| 阶段 | 状态 |
|---|---|
| P0–P7 | ✅ 完成（含五次复检修正） |
| P8a 布局骨架 | ✅ 10/12（1 项等上游，1 项明确不做） |
| P8b 会话与输入 | ✅ 5/6（引用来源无数据） |
| **P8g 中区会话域** | 🟡 **10 ✅ / 1 🟡 / 6 ⬜ / 6 ➖** |
| P8c 设置域 | ⬜ 0/8 |
| P8d 会话增强 | ⬜ 0/7 |
| P8e 插件体系 | 🟡 1/8 |
| P8f 终端与浏览器 | ⬜ 0/5 |

**总体完成度 46%**（26✅ / 1🟡 / 32⬜，不含 ⏳ 与 ➖）。

详见 `docs/dsh-parity-progress.md`（状态清单）与
`docs/dsh-parity-spec.md`（规格，含 §7.5 中区会话域全部出处行号）。

### P8g 中区：已完成

| # | 项 | 实现 |
|---|---|---|
| M1 | 用户消息复制 | `MessageActions`：HH:mm + 复制，对勾 1000ms 复原 |
| M2 | 用户气泡宽度 | `min(列宽×0.702, 82%)` 两条约束取小 |
| M4 | 消息间距三档 | 4 / 12 / 16 |
| M5 | 贴底阈值 | **25px**（dsh 常量写 24，调用处 `+1`，实际 25） |
| M6 | 回到底部按钮 | 34×34，距底 16，滚离才出现 |
| M7 | 空状态 Hero | logo + "探索未至之境" + 预览版徽章 |
| M8 | 输入区渐变遮罩 | 36px，叠在消息流底部（不是输入区行内） |
| M9 | 流式运行状态行 | `RunStatusLine`，"深度求索中，用时 N 秒"，1s 刷新 |
| M10 | 已停止标记 | 旁挂 `_stoppedAssistantIds`，`_turnRunning` 互斥 |
| M11 | 工具卡状态视觉 | **Arc `ToolCall` 自带**：运行 shimmer / 失败红色+Retry / 待审批描边 / 拒绝变暗。我们只做 9 种 SDK 状态 → `ToolCallStatus` 的映射 |

### P8g 中区：剩余

| # | 项 | 难度 | 卡在哪 |
|---|---|---|---|
| M3 🟡 | 助手动作行（复制/分支/用量/时间） | 中 | **契约无字段**：`HostProtocol` 没有 usage、branch、message id 之外的支撑 |
| M12 | 代码块复制 + 语言标签 | 中 | **改用 `TKCode.Arc.CodeBlock`**（public）：文件头=语言标签 + 语法高亮 + 复制 + 可选折叠。待确认 `Markdown` 能否把代码块交给它 |
| M13 | 轮次导航条 | 大 | 右侧 28px 竖排刻度 + hover 预览卡 |
| M14 | 加载更早 | 中 | 顶部按钮 + 锚点补偿（当前无分页） |
| M15 | 折叠四层模型 | 大 | 轮次→分组→单条，不可互相绕过 |
| M16 | 工作详情模式 | 大 | compact/standard/detailed/verbose + 两个偏好 |
| M17 | 列宽可拖拽 | 中 | min 640、边缘预算 176、localStorage 持久化 |

### 明确不做（dsh 也没有，别照想象补）

M18 流式光标、M19 会话内搜索、M20 mermaid 渲染、
M21 消息重试/编辑/分享、M22 消息级虚拟化、M23 头像。

> **M22 注意**：dsh 的消息流**没有虚拟化**（`ChatFlow.tsx:52-63` 全量 map），
> 我们用了 `VirtualizingStackPanel`——这是**超出**不是欠缺。
> 不拆，但诊断口径必须按"可见容器"算，不能按总条目算
> （这条踩过两次：反馈条数量、用户气泡数量）。

---

## 六、下一步建议

**推荐顺序**：M12 → M11 → M3（或直接转 P8c）。

理由是 M12（代码块复制）出现频率最高——每条含代码的回复都会用到，
而且 Arc 没有现成件，越晚做越可能被迫改结构。

**M12 做法**：自建一个薄包装。Arc 的 `Markdown` 渲染代码块时给的是
`pre`/`code` 结构，可以在 `Markdown` 外面检测子控件，或在 `Markdown.CodeBlockTheme`
之类钩子上注入。**先查 Arc 源码**——Mac 上需要 clone
`AIDotNet/TKCode.Arc`（见第七节）。

### 另外两件待办（Mac 本轮新增）

1. **人眼看一次红绿灯位置**：80px 安全区是估算值（18px 边距 + 三个 14px 圆点），
   控件树证明不了"没被压住"。截图权限被系统挡着，需要人工确认或授权终端录屏。
2. **滚动条样式**（§4.3）：macOS 浮层式与 dsh 常驻细条仍有差异，未处理。


---

## 七、Mac 上需要重新准备的外部资源

Windows 上这两个路径是本地拷贝，**Mac 上要重新 clone**：

```bash
# dsh 源码（规格里所有行号出处都指向它，commit 必须对上 ce7073d）
git clone https://github.com/deepseek-ai/deepseek-harness dsh-src
cd dsh-src && git checkout ce7073d

# Arc 源码（查组件是否存在、哪些是 internal 用）
git clone https://github.com/AIDotNet/TKCode.Arc arc-src
```

**这两个不是构建依赖**——只在"复核规格有没有写错"和"查 Arc 组件能不能用"时才需要。
构建只要 nuget.org 上的公开包。

---

## 八、几条踩过的坑（省时间用）

### 8.1 "编译通过"和"检查全绿"都证明不了功能能用

本项目多次出现：编译 0 错误、82 条检查全绿，但功能是坏的。
典型例子——`DesktopProtocol.Json` 定义了却一处没用，桌面端特有消息
根本发不出去（模型目录一直空），只有读运行时日志才发现。

**所以每次都要跑一次真实应用看 stderr。**

### 8.2 定义了但没人用 —— 已出现 5 次

- `DenyAndStop` 是假的（只有 `Approved`/`Reason`，表达不了"还要停回合"）
- `PickFilesAsync` 反射查不到（internal），白写了 200 行 `IFilePicker`
- `FileSelected` 事件没有订阅者
- `SetContextItems` 没有调用者
- `_compensating` 有声明有读有重置，**从没有一处置 true**

新加事件/字段后，用 `grep` 确认**真的有触发点和订阅者**。

### 8.3 贴底逻辑改过四轮，四次都是运行时抓到的

①阈值 80（凭手感）→ ②改 25 但"跨阈值"判断被内容增长误伤（停在距底 572px）
→ ③改为比较"Offset 是否变小"→ ④手算 `_scroll.Offset` 静默失败，
改用 `ScrollToEnd()` 并在 Background/Loaded/Render 三个优先级各排一次。

**静态检查全程绿。** 涉及滚动、布局时序的问题只能靠运行时观察。

### 8.4 检查的反证必须真能变红

`chat-no-fake-streaming-caret` 第一版反证往源码尾部追加 `// ▍`，
被注释过滤跳过 → **反证不变红，等于这条检查从没被验证过**。
改成追加非注释代码后才正确变红。

写新检查时，先确认 `--break=<slug>` 真的失败。

### 8.5 误报比漏报更伤

诊断输出查 `TextBlock.IsVisible` 判断空状态 Hero 是否显示，
得到"13 条消息时 Hero 仍显示"的假警报——父容器隐藏后子元素
`IsVisible` 仍是 true。要用 `IsEffectivelyVisible`。

**一次误报会训练人忽略这条输出**，之后真问题也看不见了。

---

## 九、文件地图

| 路径 | 作用 |
|---|---|
| `src/DougongDesktop.App/Shell/ConversationView.cs` | **中区主体**（~1400 行），条目分派、滚动跟随、空状态 |
| `src/DougongDesktop.App/Shell/MessageActions.cs` | 用户消息时间 + 复制 |
| `src/DougongDesktop.App/Shell/RunStatusLine.cs` | "深度求索中" 状态行 |
| `src/DougongDesktop.App/Shell/FeedbackBar.cs` | 赞/踩（Arc `AiAnswerActions` 是 internal，故自建） |
| `src/DougongDesktop.App/Shell/ApprovalCard.cs` | 审批四档 + 倒计时 |
| `src/DougongDesktop.App/Shell/Composer.cs` | 输入区（包装 Arc `AiComposer`） |
| `src/DougongDesktop.App/Shell/RightDock.cs` | 右栏 5 tab（含目录面包屑导航） |
| `src/DougongDesktop.App/Shell/DshTheme.cs` | dsh token 取色层 + `MonoFont` |
| `src/DougongDesktop.Ui.Core/Columns.cs` | **布局与中区常量唯一真源** |
| `src/DougongDesktop.App/Views/MainWindow.cs` | 装配根（~1400 行），含 `DumpLayout` 诊断 |
| `tests/DougongDesktop.Architecture.Checks/ConversationChecks.cs` | 中区 8 条检查 |
| `docs/dsh-parity-spec.md` | 规格，§7.5 是中区全部出处 |
| `docs/dsh-parity-progress.md` | 状态清单（46→65 项，含维护记录） |

### 一条规矩

**任何硬编码数值都必须引用 `Columns.cs` / `ConversationMetrics`**，
否则改一次 dsh 规格就要满仓库找数字。颜色必须走 `DshTheme` / `DshTokens`
（`chat-colors-come-from-tokens` 会拦手调色，黑白灰叠加层除外）。

---

## 十、提交状态

**改动尚未提交**。Mac 上接手时先看一眼：

```bash
git status --short
git diff
```

改动内容（Windows 侧）：
- SDK 0.1.13 → 0.1.18-alpha.1（契约 165→165 零变更）
- P8b 复核（目录双向导航 + 面包屑）
- P8g 中区会话域新增（规格 §7.5 + 9 项实现）
- 跨平台修复（脚本 tasklist、字体 Consolas）
- 检查 74 → 82 条

改动内容（Mac 侧，2026-11-05）：
- 脚本可执行位 `100644 → 100755`（三个脚本）
- `sync-dsh-tokens.py` 工作区 CRLF → LF
- `all-checks.sh` 补 4 条漏掉的反证 + 加"漏项自保"（不一致即 exit 1）
- 标题栏按平台分支：macOS 留 80px 红绿灯安全区、不自绘窗口按钮
- 新增检查 `titlebar-branches-for-macos`（检查 82 → 83）
- 修两条误导诊断（`ResizablePanel 期望 3`、`ToolCall 0 张`）
- 新增 `DOUGONG_UI_SCROLL=end` 与 `DOUGONG_UI_TOOLFIRST=1` 两个自检开关

**新增文件**：
- `src/.../Shell/MessageActions.cs`
- `src/.../Shell/RunStatusLine.cs`
- `tests/.../ConversationChecks.cs`
- `HANDOFF.md`（本文件）

**Mac 上已跑通 `scripts/all-checks.sh`：83 条全绿，构建 0 警告 0 错误。**

