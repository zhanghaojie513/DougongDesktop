# Dougong Desktop

Dougong Desktop 是 [Dougong](https://github.com/zhanghaojie513/Dougong) SDK 的**参考桌面宿主**：
用 [Avalonia](https://avaloniaui.net/) + [TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc) 构建的原生桌面客户端。

它的目的不只是"做一个能用的客户端"，而是**证明 SDK 的设计承诺成立**：

- Agent 运行时可以被真实宿主内嵌，而不只是跑在 CLI 里
- 插件体系能被第三方扩展（agent 能力插件 + UI 形态插件，两套契约）
- SDK 与 UI 可以彻底解耦——换掉 SDK 不用改 UI

> **⚠ 早期开发中。** SDK 处于 `0.1.x-alpha`，Arc 处于 `0.3.2-preview`。
> 签名和行为都可能在任意提交中变更，不适合作为依赖。

---

## 当前状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0 | 装配正确性回归 | ✅ 六项判据全过 |
| P1 | 架构守卫（三条断言 + 三条反证） | ✅ |
| P2 | Arc 组件可用性验证 | ✅ 三个 preview 包可用 |
| P3 | 应用外壳（slot 框架 + 三栏） | 进行中 |
| P4–P10 | 消息流 / 审批 / 插件管理 / 持久化 / 设置 / 验收 | 未开始 |

**已能跑通**：AgentHost 启动 → IPC 连接 → 真实 provider 对话 → 流式回复。
实测 provider：MiniMax anthropic 兼容端点。

---

## 架构：四层，UI 与 SDK 严格解耦

```text
┌─────────────────────────────────────────────────────┐
│  DougongDesktop.App          Avalonia + TKCode.Arc  │
│  UI 进程 · JIT · 装 UI 插件                          │
└───────────────────────┬─────────────────────────────┘
                        │ 只依赖 DougongDesktop.Ipc
┌───────────────────────▼─────────────────────────────┐
│  DougongDesktop.Ipc          纯契约 + NDJSON over TCP│
└───────────────────────▲─────────────────────────────┘
                        │ 只依赖 DougongDesktop.Ipc
┌───────────────────────┴─────────────────────────────┐
│  DougongDesktop.Framework    唯一知道 SDK 的地方     │
└───────────────────────▲─────────────────────────────┘
                        │ 内嵌 Dougong.* NuGet 包
┌───────────────────────┴─────────────────────────────┐
│  DougongDesktop.AgentHost    引擎进程 · JIT · 装 agent 插件 │
└─────────────────────────────────────────────────────┘
```

### 三条硬边界

| 规则 | 由什么保证 |
|---|---|
| `App` **不得引用任何 `Dougong.*`** | `tests/DougongDesktop.Architecture.Checks`，含反证 |
| `Ipc`/`Framework`/`AgentHost` 不得引用 Avalonia / Arc | 同上 |
| `Ipc` 只能依赖 BCL | 同上 |

**为什么 `App` 禁止引用 SDK**：UI 与引擎之间只有一条 NDJSON 连接，消息类型全部来自 `Ipc`。
换掉 agent 框架只需重写 `Framework`（4 个文件、约 800 行），UI 一行不改。

### 为什么两个进程都是 JIT

`AgentHost` 用 `AssemblyLoadContext` 动态加载 agent 插件——与 NativeAOT 不兼容。
`App` 要支持 UI 插件，同理。

代价：安装包变大、启动变慢。换来的是**插件可替换**。这个取舍是刻意的。

---

## 快速开始

### 前置

- .NET SDK **10.0.402**（见 `global.json`）
- Windows / macOS / Linux（Avalonia 12 支持三平台）

### 构建与自检

```bash
dotnet build DougongDesktop.slnx -c Release

# 全量自检：构建 + 架构守卫（正向 + 反证）
bash scripts/all-checks.sh
```

不需要 provider、不联网。

### 跑起来

引擎和界面是两个进程，各自启动：

```bash
# 终端 1：引擎
set DOUGONG_PROVIDER_BASE=https://api.minimaxi.com/anthropic
set DOUGONG_PROVIDER_MODEL=<模型名>
set DOUGONG_PROVIDER_PROTOCOL=anthropic
set DOUGONG_PROVIDER_KEY=<你的 key>
dotnet run --project src/DougongDesktop.AgentHost

# 终端 2：界面
dotnet run --project src/DougongDesktop.App
```

引擎启动后会把端口写进 `~/.dougong-desktop/endpoint.json`，UI 读它来连接。

**密钥只从环境变量读，不落盘、不进日志。** 这是刻意的——配置文件会进 git、进备份、进日志。

### 环境变量

| 变量 | 必需 | 说明 |
|---|---|---|
| `DOUGONG_PROVIDER_BASE` | ✅ | 服务地址 |
| `DOUGONG_PROVIDER_MODEL` | ✅ | 模型名 |
| `DOUGONG_PROVIDER_KEY` | ✅ | 密钥 |
| `DOUGONG_PROVIDER_PROTOCOL` | | `openai`（默认）/ `anthropic` / `responses` |
| `DOUGONG_ANTHROPIC_PATH` | | Anthropic 路径覆盖，默认 `/v1/messages` |
| `DOUGONG_CONTEXT_WINDOW` | | 模型窗口 token 数；不设则按模型名推断 |
| `DOUGONG_MAX_OUTPUT` | | 单次输出上限，默认 4096 |
| `DOUGONG_SESSION_DIR` | | 会话目录，默认 `~/.dougong-desktop/sessions` |
| `DOUGONG_WORKSPACE` | | 工作区目录 |

**协议必须显式匹配端点。** 地址含 `/anthropic` 但协议写 `openai` 会 404，而报错信息不会指向这个原因。

---

## 目录结构

```text
src/
  DougongDesktop.Ipc/          IPC 契约（消息类型 + NDJSON 传输），零外部依赖
  DougongDesktop.Framework/    SDK 投影层 —— 唯一知道 Dougong.* 的地方
  DougongDesktop.AgentHost/    引擎进程（可执行），承载 Framework + IPC 服务端
  DougongDesktop.App/          UI 进程（Avalonia + TKCode.Arc）
tests/
  DougongDesktop.Architecture.Checks/   架构守卫（三条断言 + 反证开关）
scripts/
  all-checks.sh                全量自检（CI 与本地共用这一份）
design-demos/                  三方向设计稿（设计探索，非产品代码）
prototype/                     交互原型（同上）
```

---

## 关键设计决定

| 决定 | 理由 |
|---|---|
| **`App` 禁止引用 `Dougong.*`** | UI 只认 IPC 契约；换框架时 UI 不动 |
| **两进程都 JIT** | 为了支持插件加载，放弃 AOT 的体积与启动优势 |
| **IPC 用 NDJSON over TCP** | 跨平台一套代码，能手工 `nc` 调试 |
| **消息流自建 `ItemsControl`** | Arc 的 `ChatThread` 不支持自定义条目模板，无法内嵌工具卡片 |
| **验收标准是"行为等价"，不是"能显示"** | 截图看不出事件顺序错、状态机错、重放不一致 |

---

## 与 SDK 的关系

- 通过 **nuget.org** 消费 `Dougong.*` 包，**不做跨仓 `ProjectReference`**
- 包版本写死在 `Directory.Packages.props`，升级时只改一行
- SDK 的架构测试扫不到本仓库，所以本仓库有自己的守卫

---

## 文档

| 文档 | 内容 |
|---|---|
| [docs/ipc-protocol-readiness.md](docs/ipc-protocol-readiness.md) | IPC 协议就绪度评估 |

完整的实施方案、开发计划、Slot 机制规格在维护者的 Obsidian vault 中（不入库）。

---

## 许可

MIT

## 致谢

- [Dougong](https://github.com/zhanghaojie513/Dougong) — 插件内核与 Agent 运行时
- [TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc) — Arc 风格 Avalonia 组件库
- [Avalonia](https://avaloniaui.net/) — 跨平台 UI 框架
