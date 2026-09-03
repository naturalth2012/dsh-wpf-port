# 贡献指南

感谢你考虑为 dsh-wpf-port 做贡献！本项目是 `deepseek-harness` 的 **Windows 原生 WPF 客户端**。

> ⚠️ **开始前请先读**：本项目**不包含** `deepseek-harness` 的源码。它是一个**客户端**，需要你本地已有 harness 检出才能运行。详见下方「运行前提」。

---

## 1. 运行与构建前提

| 要求 | 说明 |
|---|---|
| **操作系统** | Windows（`Dsh.Wpf` 与 `Dsh.Viewer` 目标框架为 `net8.0-windows`，依赖 WPF 与 WebView2） |
| **SDK** | .NET 8 SDK，**含 Windows 桌面工作负载** |
| **外部依赖** | 本地 `deepseek-harness` 检出目录（运行时指定，或设 `DSH_HARNESS_DIR` 环境变量） |
| **可选** | Node.js + pnpm（首次启动 harness web 服务时，客户端会自动 `pnpm install` + `build`） |

```powershell
# 构建
dotnet build dsh-wpf-port.slnx

# 运行桌面客户端
dotnet run --project Dsh.Wpf

# 运行本地存储查看器（离线浏览会话数据）
dotnet run --project Dsh.Viewer

# 测试
dotnet test dsh-wpf-port.slnx
```

> **注意**：`dotnet run` 不会自动热重载。重新构建前请先关闭正在运行的 `Dsh.Wpf.exe`，否则会因 DLL 被锁定而构建失败（MSB3027）。

### 指定 harness 目录

客户端按以下顺序探测（见 `Dsh.App/Services/HarnessLauncher.cs`）：

1. 环境变量 `DSH_HARNESS_DIR`
2. `~/deepseek-harness`
3. `~/dsh/deepseek-harness`

若均未命中，可在「服务管理」面板手动选择目录。

---

## 2. 项目结构

```
Dsh.Contract/      契约层：DTO、事件帧、RPC 定义（纯数据，无逻辑）
Dsh.Client/        传输层：双 WebSocket 下行流、RPC 信封、重连世代
Dsh.App/           领域层：SessionFold（会话折叠/状态机）、渲染、服务启动
Dsh.Wpf/           UI 层：MainViewModel（partial 拆分）、控件、XAML、i18n
Dsh.Viewer/        独立离线查看器：读取本地 .jsonl 会话数据（只读）
```

分层方向：`Contract ← Client ← App ← Wpf`。新增功能时请尽量把逻辑放在 `Dsh.App`，让 `Dsh.Wpf` 只做绑定与呈现。

---

## 3. 代码规范

- 仓库根目录有 **`.editorconfig`**，请让你的编辑器读取它（主流编辑器均原生支持）。
  - 4 空格缩进、UTF-8、CRLF
  - 私有字段用 `_camelCase`
- **注释写"为什么"而非"是什么"**。本项目的一个特点是关键决策处都有完整的根因注释（例：`DownstreamStreams.cs` 解释 host 为何只在信封带 `rpcId`）。改动时请保留并更新这些注释，它们记录了踩过的坑。
- **新增/修改阈值常量**时，同步更新 `os/20-current-implementation.md`。

### 异常处理

- **禁止**空 `catch { }` 或裸 `catch (Exception)` 吞掉异常而不记录。
- 捕获**预期的具体异常类型**（`IOException`、`JsonException`、`UnauthorizedAccessException` 等），并用 `when` 过滤。
- 让真正的 bug（空引用、OOM）暴露出来，不要静默吞掉。
- 参考范例：`Dsh.Client/DownstreamStreams.cs` 的 `catch (JsonException)`。

### 国际化

UI 字符串**不要硬编码**。请加到 `Dsh.App/Resources/*.resx`（现有 6 种语言：英/中/日/德/法/西），新增 key 需 6 种语言齐全。

---

## 4. 测试

- 测试项目：`Dsh.App.Tests`、`Dsh.Contract.Tests`（当前 **313 个用例**）。
- **契约测试请基于真实 wire fixture**，不要用 mock —— 这能真正捕获上游协议变更（见 `Dsh.Contract.Tests/WireFormatTests.cs`）。
- 提交前请确保：`dotnet test` 全部通过，且 `dotnet build` 无警告。

CI 会在 `windows-latest` 上以 Release 配置运行构建与测试（见 `.github/workflows/ci.yml`）。

---

## 5. 提交与 PR

### 提交信息

采用 [Conventional Commits](https://www.conventionalcommits.org/) 风格：

```
feat: 新增双击消息在独立窗口查看
fix: 修复控制台输出乱码（Markdig GFM 表格误判）
refactor: 拆分 SessionFold 的 trajectory 逻辑
docs: 更新 os/20 阈值事实
test: 补 SessionFold 边界用例
chore: 升级依赖
```

作用域可选，例如 `fix(wpf): ...`。

### PR 流程

1. Fork 仓库并创建分支（建议 `feat/xxx` 或 `fix/xxx`）。
2. 确保 `dotnet build` 0 警告、`dotnet test` 全绿。
3. 提交 PR，在描述中说明：
   - **改了什么** / **为什么改**
   - 是否影响契约（`Dsh.Contract`）—— 若影响，需确认与 host 端 `packages/host/apiproxy/src/api/*.ts` 一致
   - 是否需要更新 `os/` 文档
4. CI 通过后会进行评审。

### 涉及契约变更时

`Dsh.Contract` 必须与上游 `deepseek-harness` 的 `packages/host/apiproxy/src/api/*.ts` 保持一致。修改契约时：

1. 先回源确认上游真实字段名/类型
2. 更新 `Dsh.Contract`
3. 更新 `Dsh.Contract.Tests` 的 fixture
4. 见 `os/05-contract-sync.md`

---

## 6. 文档（`os/` 目录）

`os/` 目录收录**仍长期有效**的开发文档（中文），`os/README.md` 是地图：

- **功能集事实源**：`02-feature-set.md`（项目边界）
- **缺口清单**：`17-landable-gap.md`（认领任务前必读）
- **实现事实**：`20-current-implementation.md`（真实生效的阈值/常量）
- **架构**：`07-architecture.md`；**验收**：`04-acceptance.md`；**契约同步**：`05-contract-sync.md`
- **查看器规范**：`03-local-storage-viewer.md`；**UI 体系**：`12-ui-design.md`

> ⚠️ 该目录为中文开发文档，且部分内容记录的是"当时计划"而非"当前实现"。
> **判断现状请以代码和 `os/20-current-implementation.md` 为准。**
> 过程性文档（逐日自审日志、差距台账、阶段性设计书、内部变更日志等）**未包含在本仓库中**。

改动涉及功能/架构/契约时，请同步更新相关 `os/` 文档，并在根目录 `CHANGELOG.md` 登记对外可见的变更。

---

## 7. 已知待办（欢迎认领）

当前真实未实现项（详见 `os/17-landable-gap.md`）：

- **H2** Canvas 时间轴交互（拖拽聚焦 / 滚轮缩放）
- **H6** TodoDock 写操作、**H7** QueueDock 操作（`session.updateQueue` 契约已就绪，客户端未接线）
- **I 域**：查看器 L1 单元测试、L2 融入主客户端（Region Tab + 在线联动）、L3 增强渲染、L4 检索与导出
- **J1** 托盘作业/审批 balloon
- **K3** 日期数字本地化、**K4** 字体与 bidi 排版

**blocked-by-host**（需上游先加 RPC，本仓库无法单独完成）：G3 作业 kill、C12 凭据变更事件。

认领前建议先开 issue 说明思路，避免重复劳动。

---

## 8. 许可证

本项目采用 [MIT 许可证](LICENSE)。你提交的代码即表示同意以此许可证发布。
