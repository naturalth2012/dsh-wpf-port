# 20 — 当前实现事实（从代码反推）

> **性质**：本文件**从代码反推**记录当前真实生效的阈值与常量，**不是设计文档**。
> **用途**：调参、理解当前行为、排查"文档说 A 但代码是 B"的困惑。
> **核对日期**：2026-09-02（commit `7a2923e`）。
>
> ⚠️ **与 `archive/18`、`archive/19` 的关系**：那两份是**设计提案**，其中多个阈值**从未落地**（见下表"文档值"列）。**以本文件的"代码实际值"为准**。

---

## 1. 渲染与性能阈值

| 常量 | 代码实际值 | 位置 | 文档曾建议 | 是否落地 |
|---|---|---|---|---|
| `MaxRenderBytes` | **256 KB** | `Dsh.Wpf/Controls/AssistantMessageControl.cs:59` | 16 KB（`archive/19` P0） | ❌ 未落地 |
| `IncrementalThreshold` | **4 KB** | `AssistantMessageControl.cs:74` | 32 KB（`archive/16` §记录） | ❌ 不一致 |
| `DefaultMaxBlocks` | **200** | `AssistantMessageControl.cs:56` | 200 | ✅ 一致 |
| `MaxConsoleLines` | **200** | `AssistantMessageControl.cs:310` | — | — |

**说明**：
- `MaxRenderBytes`：单条消息超过此值渲染为"内容已截断（N 字符）→ 查看完整"门。
- `IncrementalThreshold`：流式追加超此阈值且新文本以已渲染文本为前缀时，切换纯文本增量追加（避免重复 Markdig 解析）。
- ⚠️ 这两个值与 `archive/19` 的 16KB / `archive/16` 的 32KB **均不一致**。若需按设计调优，属**待决策项**（见 §5）。

## 2. 会话窗口与分页

| 常量 | 代码实际值 | 位置 | 文档曾建议 | 是否落地 |
|---|---|---|---|---|
| `SessionFold.MaxRows` | **3000** | `Dsh.App/SessionFold.cs:426` | 20000（`archive/18` §4.3） | ❌ 未落地 |
| `MaxTranscriptRenderLines` | **600** | `Dsh.Wpf/MainViewModel.cs:137` | 20000（`archive/18` §4.3） | ❌ 未落地 |
| `TrimSlack` | 见 `:442`（超 `MaxRows + slack` 才裁剪） | `SessionFold.cs:442` | — | ✅ |
| `maxMessages`（历史/翻页） | **500**（硬编码） | `MainViewModel.Service.cs:72, 251` | `PAGE_MESSAGES = 100`（`archive/18` §4.2） | ❌ **常量从未引入** |

### ⚠️ 已知不一致（待决策）

`archive/18:76` 设计要求 `MaxTranscriptRenderLines == SessionFold.MaxRows`，理由是"Transcript 完整镜像 fold，无头/尾窗错位"。

**代码中两者为 600 ≠ 3000**，后果：
- VM 在 fold 达 600 行时就裁剪 `Transcript` 渲染窗口
- `SessionFold.MaxRows = 3000` **实际永不触发**（fold 从未达到 3000 就被 VM 裁了）

这**不是崩溃性 bug**，但使 `MaxRows` 形同虚设，且与 `archive/18` 设计意图不符。调优前需先决定以哪个为准。

## 3. 其他阈值

| 常量 | 值 | 位置 | 说明 |
|---|---|---|---|
| `MaxHarnessLogLines` | 500 | `MainViewModel.cs:132` | 服务启动日志上限 |
| `NotificationDurationMs` | 5000 | `MainViewModel.cs:708` | 通知条自动消失时间 |
| `InitTimeoutMinutes` | 15 | `MainViewModel.cs:153` | 首次 `pnpm install + build` 超时（说明首启构建可能很久） |
| `MaxTrajectorySteps` | 3000 | `SessionFold.cs`（与 `MaxRows` 同值） | 轨迹步数上限 |

## 4. 服务目录探测（2026-09-02 变更）

`HarnessLauncher.TryLocateDefaultDirectory()` 解析顺序：

1. **环境变量 `DSH_HARNESS_DIR`**（若设置且非空）
2. `~/deepseek-harness`（UserProfile）
3. `~/dsh/deepseek-harness`（UserProfile）

**不再硬编码任何机器专属绝对路径**（原先硬编码的若干 `D:\` 开发机目录已于 `7a2923e` 移除，原因：开源会泄露本地目录结构，且对他人无效）。

判定依据：`LooksLikeHarnessCheckout(dir)` —— 含 `pnpm-workspace.yaml` 即视为 harness 检出。

### 4.1 自动初始化与启动流程

选定目录后，`HarnessLauncher` 走「探测 → 初始化 → 启动 → 探活 → 连接」五步（实现见 `Dsh.App/Services/HarnessLauncher.cs` + `Dsh.Wpf/MainViewModel.Service.cs`）：

1. **探测（`TryLocateDefaultDirectory`）**：见上。UI 在「服务」面板提供目录选择，选中后存入 `AppSettings.HarnessDirectory`。
2. **初始化（`InitializeAsync`）**：若 `NeedsBuild`（缺少 `apps/web/dist`，且 `apps/web-dist` 回退也不存在），依次执行 `pnpm install` 与 `pnpm run build`。超时由 `InitTimeoutMinutes`（默认 15 分钟，`MainViewModel.cs:153`）控制 —— 首次构建可能很久。
3. **启动（`Launch`）**：后台执行 `pnpm dsh web`，标准输出/错误经 `OnLog` 回传 UI（上限 `MaxHarnessLogLines` 500 行），失败信息写入 `InitFailedLog` 供排查。
4. **探活（`WaitForReadyAsync`）**：轮询网关 `http://127.0.0.1:3080` 的 `host.describe`，直至返回成功或超时。
5. **连接**：探活通过后由 `MainViewModel.Service.cs` 自动发起 WPF 客户端连接，无需用户再操作。

**亮点**：用户只需指定一次目录，后续 install / build / start / connect 全自动，无需手动搭建后端。这是面向开源用户的核心卖点，也是 README「零配置后端」特性的技术依据。

## 5. 待决策项汇总

| # | 项 | 当前值 | 建议值 | 影响 |
|---|---|---|---|---|
| 1 | `MaxRenderBytes` | 256 KB | 16 KB？ | 超长消息是否更早折叠（用户体验 vs 卡顿） |
| 2 | `IncrementalThreshold` | 4 KB | 32 KB？ | 流式切纯文本的时机（排版突变感知） |
| 3 | `MaxTranscriptRenderLines` vs `MaxRows` | 600 / 3000 | 统一？ | 见 §2「已知不一致」 |
| 4 | `maxMessages` | 500 硬编码 | 引入 `PAGE_MESSAGES`？ | 翻页粒度与事件量 |

> 以上均为**调优决策**，非缺陷。修改前建议先实测长会话表现，避免凭文档值盲改。

---

## 维护约定

- 修改上述任何常量时，**同步更新本文件的对应行**。
- 若某设计文档（如 `archive/*`）的阈值最终被采纳落地，请在该文档顶部标注"✅ 已落地（见 `20`）"，避免后人误判。
- 本文件是**代码事实的镜像**，代码改了它就必须改。
