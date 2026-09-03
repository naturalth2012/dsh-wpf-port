# dsh-wpf-port

**[English](#english) · [中文](#中文)**

---

## English

### What is this?

A **native Windows desktop client (C# + WPF)** for [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness).

It mirrors the user-facing functionality of the web client (`apps/web` + `packages/client`) and adds Windows-native enhancements. The backend — LLM calls, tools, session logic — is **reused as-is** through `packages/host`'s loopback HTTP/WebSocket gateway. Only the transport carrier and the UI layer are reimplemented in C#.

> ⚠️ **Status: alpha.** Functional but not production-ready. See [Known limitations](#known-limitations).
>
> ⚠️ **Not affiliated with DeepSeek.** This is an independent, community-maintained client. It is **not** a full replacement for the web client — see [Relationship to the web client](#relationship-to-the-web-client).

### Relationship to the web client

The WPF client covers **most** user-facing features of the web client and is stronger on Windows-native interactions (directory picking, file opening, DPI awareness, multi-window, system tray, offline viewing).

It is **not 100% equivalent**. Canvas timeline interaction, Todo/Queue dock write operations, Viewer integration and enhanced rendering (L2–L4), tray balloons, and font/bidi layout adaptation are still missing or read-only, and a few items are blocked by missing upstream RPCs.

### Features

- **Transport** — dual downstream WebSocket streams (event stream + RPC response stream) with reconnect generation semantics
- **Sessions** — workspace→session tree, branch switching, incremental upsert, hover cards, drag reordering
- **Chat** — full conversation loop, streaming render, compaction / retry rows, rich stats line
- **Collaboration** — prompts, approvals, Jobs-Queue panel, reconnect replay; Plan and permission chips via `settings.mutate` with CAS retry
- **Configuration** — settings page, model selection, credential management, model discovery
- **Subagents** — directory tree with read-only / continue modes
- **Background jobs** — job panel
- **Visualization** — trajectory timeline, artifacts, feedback, copy
- **Local storage viewer (`Dsh.Viewer`)** — browse local `.jsonl`/`.zstd` session data offline (L0–L4: tree browse, rich Markdown/tool rendering, in-session search, stats, Markdown/JSONL export, 6-language UI)
- **Windows-native** — multi-window, system tray, global hotkeys, always-on-top, narrow-window responsive layout, dark title bar, auto-scroll
- **Offline replay** — read back cached session history when the gateway is unreachable
- **Zero-config backend** — point the client at a `deepseek-harness` checkout (or set `DSH_HARNESS_DIR`) and it auto-installs dependencies, builds the web bundle, launches `pnpm dsh web`, and connects — no manual backend setup
- **i18n** — 6 languages (English, 中文, 日本語, Deutsch, Français, Español)

### Requirements

| | |
|---|---|
| **OS** | Windows (`Dsh.Wpf` and `Dsh.Viewer` target `net8.0-windows`; they need WPF and WebView2) |
| **SDK** | .NET 8 SDK **with the Windows desktop workload** |
| **External** | A local `deepseek-harness` checkout — **not bundled with this repo** |
| **Optional** | Node.js + pnpm (the client runs `pnpm install` + `pnpm run build` for the web bundle on first launch) |

### Quick start

```powershell
dotnet build dsh-wpf-port.slnx      # build
dotnet run --project Dsh.Wpf        # run the desktop client
dotnet run --project Dsh.Viewer     # run the local storage viewer
dotnet test dsh-wpf-port.slnx       # test
```

On first launch, pick your `deepseek-harness` directory in the **Service** panel. Alternatively set the `DSH_HARNESS_DIR` environment variable; the client also probes `~/deepseek-harness` and `~/dsh/deepseek-harness`.

**Backend auto-setup.** The client needs a running `deepseek-harness` host. On first launch it detects a fresh checkout (missing `apps/web/dist`), runs `pnpm install` + `pnpm run build`, launches `pnpm dsh web` on `http://127.0.0.1:3080`, polls until the gateway answers, then auto-connects. You only choose the directory once.

> **Note**: `dotnet run` does not hot-reload. Close the running `Dsh.Wpf.exe` before rebuilding, otherwise the build fails with MSB3027 (locked DLLs).

### Project structure

```
Dsh.Contract/   Contract layer — pure DTOs (envelopes, error codes, method registry, frames, projections)
Dsh.Client/     Transport layer — WpfApiClient, generations, frame routing
Dsh.App/        Application layer — services, projection repositories, interaction coordinator
Dsh.Wpf/        UI layer — WPF views and view models
Dsh.Viewer/     Local storage viewer (standalone, read-only)
os/             Development process documentation (Chinese) — see os/README.md
```

### Tests

**367 tests passing** (`Dsh.App.Tests` 218 + `Dsh.Contract.Tests` 95 + `Dsh.Viewer.Tests` 54).

Contract tests are written against **real wire fixtures** rather than mocks, so upstream protocol changes are caught early.

### Known limitations

| Area | Status |
|---|---|
| Canvas timeline interaction (drag-focus / wheel-zoom) | Axis is drawn; interaction layer missing |
| TodoDock / QueueDock write operations | Read-only (`session.updateQueue` contract exists, client not wired) |
| Viewer: host-client integration (L2), cross-session global search, HTML/CSV export | L0–L4 done + tested (tree browse, rich rendering, in-session search/stats, Markdown/JSONL export) |
| Tray job/approval balloons | Not implemented |
| Date/number localization, font & bidi layout | Not implemented |
| Job kill, credential-change events | **Blocked by upstream** (missing RPC) |

See `os/17-landable-gap.md` for the full list.

### Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build prerequisites, code conventions, commit style, and the PR process.

Issues and PRs are welcome. If you plan to pick up a larger item from the limitations list, please open an issue first to avoid duplicate work.

### Documentation

- [CONTRIBUTING.md](CONTRIBUTING.md) — contributing guide
- [CHANGELOG.md](CHANGELOG.md) — changelog and known limitations
- [os/README.md](os/README.md) — development documentation (Chinese)

### License and upstream relationship

- **License**: [MIT](LICENSE)
- **Upstream**: This repo **does not include** `deepseek-harness` source (MIT, Copyright 2026 DeepSeek AI). It only invokes your local checkout at runtime. Obtain that dependency yourself; this repo does not redistribute its code.

---

## 中文

### 这是什么？

[deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) 的 **Windows 原生桌面客户端（C# + WPF）**。

它覆盖 Web 客户端（`apps/web` + `packages/client`）的面向用户功能，并增加 Windows 原生增强。后端（LLM 调用、工具、会话逻辑）通过 `packages/host` 的 loopback HTTP/WebSocket 网关**原样复用**，仅用 C# 重写通信 carrier 与 UI 层。

> ⚠️ **状态：alpha。** 功能完整度较高但未达生产就绪，见[已知限制](#已知限制)。
>
> ⚠️ **非 DeepSeek 官方项目。** 这是独立维护的社区客户端，且**不是** Web 客户端的完全替代，见[与 Web 客户端的关系](#与-web-客户端的关系)。

### 与 Web 客户端的关系

WPF 客户端覆盖 Web 客户端的**绝大部分**面向用户功能，并在 Windows 原生交互（目录选择、文件打开、DPI 感知、多窗口、系统托盘、离线查看）上更强。

但**并非 100% 等价**：Canvas 时间轴交互、Todo/Queue Dock 写操作、查看器融合与增强（L2–L4）、托盘 balloon、字体与 bidi 排版适配仍为只读或未实现，另有少量项因上游缺失 RPC 而阻塞。

### 功能特性

- **传输** —— 双下行 WebSocket 流（事件流 + RPC 响应流），含重连世代语义
- **会话** —— workspace→session 树、分支切换、增量 upsert、悬停卡、拖拽排序
- **对话** —— 完整对话闭环、流式渲染、compaction / retry 行、富样式状态行
- **协作** —— 提问、审批、Jobs-Queue 面板、重连回放；Plan 与权限芯片走 `settings.mutate` + CAS 重试
- **配置** —— 设置页、模型选择、凭据管理、模型发现
- **子代理** —— 目录树，支持只读 / 续写模式
- **后台作业** —— 作业面板
- **可视化** —— 轨迹时间轴、产物、反馈、复制
- **本地存储查看器（`Dsh.Viewer`）** —— 离线浏览本地 `.jsonl`/`.zstd` 会话数据（L0–L4 已交付并测试：树形浏览、富 Markdown/工具渲染、会话内检索、统计、Markdown/JSONL 导出、6 语言 UI）
- **Windows 原生** —— 多窗口、系统托盘、全局热键、窗口置顶、窄屏响应式、深色标题栏、自动滚动
- **离线回放** —— 网关不可达时回读缓存的会话历史
- **零配置后端** —— 指定 `deepseek-harness` 检出目录（或设置 `DSH_HARNESS_DIR`），客户端自动安装依赖、构建 web bundle、启动 `pnpm dsh web` 并连接，无需手动搭建后端
- **国际化** —— 6 种语言（英、中、日、德、法、西）

### 环境要求

| | |
|---|---|
| **操作系统** | Windows（`Dsh.Wpf` 与 `Dsh.Viewer` 目标框架为 `net8.0-windows`，依赖 WPF 与 WebView2） |
| **SDK** | .NET 8 SDK，**含 Windows 桌面工作负载** |
| **外部依赖** | 本地 `deepseek-harness` 检出目录 —— **本仓库不包含** |
| **可选** | Node.js + pnpm（首次启动时客户端会为 web bundle 执行 `pnpm install` + `pnpm run build`） |

### 快速开始

```powershell
dotnet build dsh-wpf-port.slnx      # 构建
dotnet run --project Dsh.Wpf        # 运行桌面客户端
dotnet run --project Dsh.Viewer     # 运行本地存储查看器
dotnet test dsh-wpf-port.slnx       # 测试
```

首次运行时在「服务」面板中选择 `deepseek-harness` 目录。也可设置 `DSH_HARNESS_DIR` 环境变量；客户端还会探测 `~/deepseek-harness` 与 `~/dsh/deepseek-harness`。

**后端自动初始化。** 客户端需要一个运行中的 `deepseek-harness` 宿主。首次启动时会检测全新检出（缺少 `apps/web/dist`），自动执行 `pnpm install` + `pnpm run build`，在 `http://127.0.0.1:3080` 启动 `pnpm dsh web`，轮询直至网关响应后自动连接。你只需指定一次目录。

> **注意**：`dotnet run` 不会热重载。重新构建前请先关闭正在运行的 `Dsh.Wpf.exe`，否则会因 DLL 被锁定而构建失败（MSB3027）。

### 项目结构

```
Dsh.Contract/   契约层 —— 纯 DTO（信封、错误码、方法注册表、帧、投影）
Dsh.Client/     通信层 —— WpfApiClient、世代、帧路由
Dsh.App/        应用层 —— 服务、投影仓库、交互协调器
Dsh.Wpf/        UI 层 —— WPF 视图与视图模型
Dsh.Viewer/     本地存储查看器（独立只读模块）
os/             开发过程文档（中文）—— 见 os/README.md
```

### 测试

**367 个用例通过**（`Dsh.App.Tests` 218 + `Dsh.Contract.Tests` 95 + `Dsh.Viewer.Tests` 54）。

契约测试基于**真实 wire fixture** 而非 mock，因此能及时捕获上游协议变更。

### 已知限制

| 项目 | 状态 |
|---|---|
| Canvas 时间轴交互（拖拽聚焦 / 滚轮缩放） | 轴已自绘，缺交互层 |
| TodoDock / QueueDock 写操作 | 只读（`session.updateQueue` 契约已就绪，客户端未接线） |
| 查看器融合（L2）、跨会话全局检索、HTML/CSV 导出 | L0–L4 已完成并测试（树形浏览、富渲染、会话内检索/统计、Markdown/JSONL 导出） |
| 托盘作业/审批 balloon | 未实现 |
| 日期数字本地化、字体与 bidi 排版 | 未实现 |
| 作业 kill、凭据变更事件 | **被上游阻塞**（缺失 RPC） |

完整清单见 `os/17-landable-gap.md`。

### 参与贡献

构建前提、代码规范、提交约定与 PR 流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

欢迎提交 issue 与 PR。若打算认领限制清单中的较大条目，请先开 issue 说明思路，避免重复劳动。

### 文档

- [CONTRIBUTING.md](CONTRIBUTING.md) —— 贡献指南
- [CHANGELOG.md](CHANGELOG.md) —— 变更日志与已知限制
- [os/README.md](os/README.md) —— 开发文档（中文）

### 许可证与上游关系

- **许可证**：[MIT](LICENSE)
- **上游依赖**：本仓库**不包含** `deepseek-harness` 源码（MIT，Copyright 2026 DeepSeek AI），仅在运行时调用你本地的检出目录。请自行获取该依赖，本仓库不分发其代码。
