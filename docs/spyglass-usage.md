# Spyglass 使用手册（Avalonia 可视化调试）

> **这份文档解决什么问题**：本项目此前验收靠手写 `DumpLayout()` 打印控件尺寸与实例数。
> 那套东西能回答"某个控件在不在、多大"，回答不了"它的样式为什么是这个值"、
> "悬停态长什么样"、"这个绑定为什么失败"。
>
> Spyglass 补上这些。它是 out-of-process 的 Avalonia 调试器（类比浏览器 DevTools）。

**接入状态**：已接入，仅 Debug 构建。验证于 2026-11-05，Spyglass `0.2.3`。

---

## 一、怎么启动

### 1. 装客户端（一次性）

```bash
dotnet tool install -g TKCode.Spyglass
```

装完后 `~/.dotnet/tools/` 下有 `spyglass.cmd`。

**注意：那个 .cmd 是转发脚本，没有同名 exe。** 真实可执行文件在：

```
~/.dotnet/tools/.store/tkcode.spyglass/0.2.3/tkcode.spyglass.win-x64/
  0.2.3/tools/net11.0/win-x64/Spyglass.exe
```

所以在 Git Bash 里直接敲 `spyglass` 会报 `command not found`——用 `.cmd` 或上面的完整路径。

### 2. 启动 GUI

```bash
# Git Bash / CMD
~/.dotnet/tools/spyglass.cmd

# 或用完整路径（PowerShell 里更省事）
```

启动后 `netstat` 应看到 `127.0.0.1:29500 LISTENING`。

### 3. 跑我们的 App，按热键

```bash
# 必须是 Debug 构建（Release 里没有探针）
scripts/rebuild.sh DougongDesktop.slnx Debug
src/DougongDesktop.App/bin/Debug/net11.0/DougongDesktop.App.exe
```

然后在 App 窗口里按：

| 热键 | 作用 |
|---|---|
| **Ctrl+Shift+I** | 打开 Spyglass（必要时自动拉起客户端） |
| **Ctrl+Shift+C** | 拾取元素（点哪个看哪个） |

macOS 对应 `Cmd+Option+I` / `Cmd+Option+C`。

---

## 二、一个必须知道的机制：agent 是被动的

**App 启动时不会监听 29500，也不连客户端。按热键才会去连。**

实测过一次：启动 App 后 `netstat | grep 29500` 什么都没有，我误判成"没生效"，
实际是这时候还没按热键。

**判断连接是否成功的正确方式**：

```bash
netstat -ano | grep 29500
```

看到**两条 ESTABLISHED**（一条客户端侧一条 App 侧）才算通。
只有一条 `SYN_SENT` 说明 App 在连、但客户端没起来。

---

## 三、五个面板各能替代什么手工活

| 面板 | 能力 | 替代了什么 |
|---|---|---|
| **Elements** | 视觉/逻辑树、按类型/名称/class/文本搜索、拾取元素、实时改属性 | `DumpLayout()` 里的尺寸与实例数统计 |
| **Styles** | 样式级联、被覆盖的值划掉、**强制 `:pointerover` / `:pressed` 等伪类** | 无法验证悬停态（此前只能读代码推断） |
| **Bindings** | 失败的绑定路径、消息、XAML 行号、DataContext | 绑定问题只能看输出里的异常栈 |
| **Resources** | 主题字典、按 key 查、**实时改颜色** | 改 token 要重编译才能看到效果 |
| **Performance** | FPS、布局/渲染分位、应用内浮层 | 虚拟化效果只能数容器数量 |

### 对我们最有用的一条：**force pseudo-class**

此前 `A11` 的 token 映射验完色值就结束了，但**悬停态、按下态从没验过**——
因为没法在不手动移动鼠标的情况下触发。样式面板可以直接强制伪类。

---

## 四、AI agent 用 MCP（无需 GUI）

Spyglass 带一个 headless MCP server，挂在本机 `http://127.0.0.1:29500/mcp`。

```bash
claude mcp add spyglass -- spyglass mcp
```

**这对后续会话的价值**：agent 可以直接读控件树，比让它看截图或读手写 dump 更结构化。

前提同样是：App 在 Debug 构建下运行、并且已经建立连接（按过热键）。

---

## 五、接入配置（已完成，此处备查）

### 包与条件

`Directory.Packages.props`：

```xml
<PackageVersion Include="TKCode.Spyglass.Agent" Version="0.2.3" />
```

`DougongDesktop.App.csproj`：

```xml
<!-- 只在 Debug 构建里引用：Release 包不该带调试探针 -->
<PackageReference Include="TKCode.Spyglass.Agent" Condition="'$(Configuration)' == 'Debug'" />
```

### 代码

`Program.cs` 的 `BuildAvaloniaApp()`：

```csharp
#if DEBUG
    .UseSpyglass()
#endif
    .LogToTrace();
```

### 两个实测出来的约束

1. **它的 Avalonia 版本要求与我们完全一致**（`12.1.4` 精确）。
   它用 `[PrivateApi]` 成员，版本漂移会出问题——这反而与 Arc 的约束同向。
2. **`TKCode.Spyglass.Agent` 只有 `net8.0` 目标框架**，net11.0 靠兼容引用。
   能正常工作，但如果哪天出现奇怪的类型加载问题，这是第一个要查的地方。

### Release 构建必须干净

实测验证过：Release 产物里 `ls | grep -i spyglass` 结果是 0 个。

**这不是洁癖**：它开本地 WebSocket 端口，生产包带这个等于多一个攻击面。

---

## 六、常见问题

| 现象 | 原因 |
|---|---|
| `spyglass: command not found` | Git Bash 不认 `.cmd`。用完整路径或用 CMD/PowerShell |
| 连接列表里没有我们的 App | App 是 Release 构建（没有探针），或没按热键 |
| 按热键没反应 | 键位被别的处理器占用。可用 `o.OpenGesture` / `o.InspectGesture` 改 |
| 只有 `SYN_SENT` 没有 `ESTABLISHED` | App 在连，但客户端没起来 |
| `netstat` 看不到 29500 | App 侧正常（要按热键）；若客户端也没有，说明 GUI 没启动 |

---

## 七、和现有 `DumpLayout()` 的关系

**两者并存，不互相替代。**

| | `DumpLayout()` | Spyglass |
|---|---|---|
| 运行环境 | 任何构建（含 Release） | 仅 Debug |
| 输出 | stderr 文本，可进 CI | 交互式 GUI |
| 适合 | **回归断言**（检查脚本里跑） | **人工排查** |
| 检查工程能用吗 | ✅ 能 | ❌ 不能（要 GUI） |

**所以自动化断言继续用 `DumpLayout()`，人工排查用 Spyglass。**
不要因为有了 Spyglass 就把 `DumpLayout()` 删掉——那会让 81 条检查里的
运行时观测部分失去依据。
