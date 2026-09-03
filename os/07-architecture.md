# 07 — 技术架构与实现规划

> 角色：**怎么做**。承接 `02`（功能项）与 `05`（契约），把"实现"落到工程结构、分层、模块职责、类清单与数据流。
> 契约权威源：`packages/host/apiproxy/src/api/*`（`rpc-map.ts` 方法注册表、`rpc.ts` 四象限线协议、`events.ts` 帧联合、`sessions.ts`/`settings.ts`/`workspace.ts` 等接口签名）。
> 契约变更触发 `05-contract-sync.md` 回归。

---

## 1. 分层决策与技术选型（先决结论）

本项目全部代码**基于本仓库自研**，与任何第三方 .NET 客户端实现无关。核心选型：

| 层 | 选型 | 依据 |
|---|---|---|
| 通信层 | host/apiproxy 的 HTTP unary/respond + **双 WebSocket 下行流**，自建 `WpfApiClient` | 与 Web 客户端同源协议，DTO 按 `api/*.ts` 权威源生成 |
| 会话折叠 | `SessionFold`：按当前 `core/session` 的 `SessionEvent` 类型集折叠 | 在线/离线共用统一折叠层，零重复、零漂移 |
| 会话聚合 | 投影驱动，而非本地全量折叠 | 与 Web 客户端的 projection 语义一致 |
| UI 层 | WPF + `CommunityToolkit.Mvvm` | Windows 原生交互能力 |

### 1.1 架构决策记录（ADR）：本地存储查看器的技术选型

| 项 | 决策 |
|---|---|
| 问题 | 离线查看器（域 I / `03`）如何构建？ |
| 决策 | **基于本仓库 `Dsh.App.SessionFold` 共享折叠层自研**，不引入外部代码。 |
| 理由 | ① `SessionFold` 是在线/离线共用的统一折叠层，复用即零重复、零漂移；② 仅需补"磁盘格式解码 + 目录扫描 + 离线渲染"三部分，磁盘解码须与 `packages/core/session/src` 逐字符一致。 |
| 现状 | `Dsh.Viewer` 已交付 L0-L2（目录扫描 + `FrameConverter`/`Fold` 渲染 + 解码管线）；L3-L6 见 `03`。 |

---

## 2. 目标架构总览

```
┌─────────────────────────── dsh-wpf-port（新）──────────────────────────┐
│                                                                          │
│  WPF UI 层（View + ViewModel，MVVM）                                      │
│   ├─ Shell/  （主窗、托盘、多窗、主题、Region 宿主）                        │
│   ├─ Workspace/  （侧栏树、会话行、搜索、目录选择）                          │
│   ├─ Conversation/  （聊天视图、流式尾、Think 折叠、工具树、Composer）        │
│   ├─ Interaction/  （审批面板、问题面板、Plan 芯片、权限芯片）                │
│   ├─ Settings/  （模型、设置、凭据、Agent 预设）                            │
│   ├─ Subagent/  （子代理目录树、只读/续写视图）                             │
│   ├─ Insights/  （轨迹视图、时间轴、产物、反馈、Todo/Queue 面板）            │
│   └─ Viewer/  （本地存储查看器，独立只读模块，见 `03`）                      │
│                                                                          │
│  应用层（Application Services，无 UI 依赖）                                 │
│   ├─ ISessionService / IWorkspaceService / ISettingsService / ...         │
│   ├─ IProjectionStore（per-session 投影仓库，higher-seq-wins）              │
│   └─ IInteractionCoordinator（approval/question 接管状态机）                │
│                                                                          │
│  通信层（Client，等价 `packages/client/connection`）                        │
│   ├─ WpfApiClient（HTTP unary/respond + 双 ClientWebSocket）               │
│   ├─ ConnectionGeneration（重连世代）                                      │
│   ├─ FrameRouter（mux/host 帧 → 订阅者）                                   │
│   └─ RpcEnvelope（四象限信封编解码 + rpcId 回显）                           │
│                                                                          │
│  契约层（Contract，纯 DTO，零 UI/网络依赖）                                 │
│   └─ 由 api/*.ts 生成：方法注册表 + 请求/响应 DTO + 帧联合 + 错误码          │
└──────────────────────────────────────────────────────────────────────────┘
                          │ HTTP POST /api/<method>  ·  /api/respond
                          │ WS   /api/events.mux  ·  /api/events.host
                          ▼
        ┌──────────────────────────────────────┐
        │  dsh host（packages/host，原样复用）    │
        │  apiproxy + webserver + agent/core     │
        └──────────────────────────────────────┘
```

---

## 3. 解决方案与工程结构

### 3.1 目标 `csproj` 划分

| 项目 | 目标框架 | 依赖 | 职责 |
|---|---|---|---|
| `Dsh.Contract` | `net8.0` | 无（仅 `System.Text.Json`） | 纯 DTO：方法注册表、请求/响应、帧联合、错误码、投影类型 |
| `Dsh.Client` | `net8.0` | `Dsh.Contract` | 通信层：`WpfApiClient`、世代、帧路由、信封 |
| `Dsh.App` | `net8.0` | `Dsh.Client` | 应用服务 + 投影仓库 + 交互协调器（无 UI） |
| `Dsh.Wpf` | `net8.0-windows` | `Dsh.App`、`CommunityToolkit.Mvvm` | WPF UI（View + ViewModel） |
| `Dsh.Viewer` | `net8.0-windows` | `Dsh.App`（只读） | 本地存储查看器（见 `03`） |

> 分层铁律：`Contract` 不依赖 `Client`；`Client` 不依赖 `App`；`App` 不依赖 `Wpf`。方向单向，便于单元测试（`App` 可配 mock `Client`）。

### 3.2 目录树

```
dsh-wpf-port/
├── os/                # 开发操作系统（本文档所属，中文契约）
├── Dsh.Contract/
│   ├── Dsh.Contract.csproj
│   ├── Rpc/            # 信封 + rpcId + 错误码（对应 rpc.ts）
│   │   ├── RpcEnvelope.cs
│   │   ├── RpcId.cs
│   │   ├── RpcResult.cs
│   │   └── RpcError.cs  # 完整错误码联合（对应 RpcErrorDetailsMap，~45 项）
│   ├── Methods/        # 每个 domain 一个文件（对应 api/*.ts）
│   │   ├── Sessions.cs / Workspace.cs / Host.cs / Settings.cs
│   │   ├── Credentials.cs / Llm.cs / Goals.cs / Skills.cs
│   │   ├── AgentPresets.cs / Subagents.cs / SessionSearch.cs
│   │   └── RpcMethodMap.cs  # 方法名 → 请求/响应类型（对应 rpc-map.ts）
│   ├── Frames/         # 下行帧联合（对应 events.ts）
│   │   ├── MuxFrame.cs   # session/event·subscribed·queue·jobs·projection + approval/question
│   │   └── HostFrame.cs  # host/session-*·workspace-*·archived-*·remote-event·agent-error
│   └── Projections/    # 投影类型（对应 session-projection 各键）
│       └── ProjectionMap.cs  # plan/permissions/goal/todos/imageLimits/sessionListMetadata/...
├── Dsh.Client/
│   ├── Dsh.Client.csproj
│   ├── WpfApiClient.cs        # call<R>/respond + 双 WebSocket 生命周期
│   ├── ConnectionGeneration.cs
│   ├── FrameRouter.cs         # mux/host 帧分发 + 订阅簿记
│   ├── ReconnectPolicy.cs     # 断线清状态 + 双流重建 + host.describe 握手
│   └── JsonEnvelopeCodec.cs   # System.Text.Json 信封/载荷解析 + 错误映射
├── Dsh.App/
│   ├── Dsh.App.csproj
│   ├── Services/  # ISessionService / IWorkspaceService / ISettingsService / ...
│   ├── ProjectionStore.cs     # ConcurrentDictionary<SessionId, Cell> higher-seq-wins
│   ├── InteractionCoordinator.cs  # approval/question 接管状态机 + rpcId 簿记
│   └── TimeZoneSampler.cs     # TimeZoneInfo.Local.Id（对应 A5）
├── Dsh.Wpf/
│   ├── Dsh.Wpf.csproj
│   ├── App.xaml / App.xaml.cs # DI 组装、主题、app.manifest(per-monitor-v2)
│   ├── Shell/  Workspace/  Conversation/  Interaction/
│   ├── Settings/  Subagent/  Insights/
│   └── Infrastructure/  # Region 宿主、Dispatcher 同步、托盘/全局键/文件关联
└── Dsh.Viewer/
    └── （见 03-local-storage-viewer.md 的模块规划）
```

---

## 4. 通信层设计（Dsh.Client，最高风险区）

### 4.1 四象限信封（严格对应 `rpc.ts`）

```
ClientRequest  { type:"client-request",  rpcId, method, payload }
ServerResponse { type:"server-response", rpcId, result }        // POST 响应体
ServerRequest  { type:"server-request",  rpcId, method, payload } // WebSocket 下行帧
ClientResponse { type:"client-response", rpcId, result }         // POST /api/respond
```

**C# 映射要点**：
- `RpcResult<T> = { ok:true; value:T } | { ok:false; error:RpcError }`，用 `OneOf` 或判别 `bool ok` + 双字段。
- `rpcId` 是 branded string（`Branded<"rpc-id">`），C# 用 `readonly record struct RpcId(string Value)` 防裸 `string` 混用。
- 响应**必须回显**请求 rpcId，不得新铸（`02` A1 验收）。

### 4.2 `WpfApiClient` 职责

| 方法 | 通道 | 语义 |
|---|---|---|
| `Task<ResponseValue<K>> Call<K>(method, payload, ct)` | `POST /api/<method>` | unary；30s 超时；`application/json` |
| `Task<RpcReceipt> Respond(rpcId, result, ct)` | `POST /api/respond` | 客户端应答；回显 rpcId |
| 下行订阅 | WS `/api/events.mux` + `/api/events.host` | 双流；帧 → `FrameRouter` |

### 4.3 双 WebSocket 下行流与世代（`02` A2/A4）

```
就绪条件：mux 流 + host 流均 open，且 host.describe 成功
断线处理：任一 WS 断开 → ConnectionGeneration++ → 清空
          pendingInteraction / jobsBySession / 投影缓存（A4）
重建：reopen 双流 → 重拉各会话 history 尾页 → 重订阅投影
```

- `ConnectionGeneration` 是递增计数，所有下行帧带世代标记；旧世代帧到达即丢弃。
- `FrameRouter` 按 `frame.type` 分派到会话 owner 或全局监听器；`session/subscribed.lastSeq` 用于对齐尾页。

### 4.4 错误码（`Dsh.Contract/Rpc/RpcError.cs`）

完整枚举对应 `RpcErrorDetailsMap`（约 45 项），关键业务分支：
`agent-busy` / `settings-conflict` / `title-invalid` / `fork-unavailable` / `workspace-not-found` / `session-not-found` / `unknown-command` / `command-error` / `credential-rejected` / `model-discovery-failed` / `subagent-*`。每个 `RpcError` 带 `details`（如 `settings-conflict` 的 `{ns, expected, actual}`）。

---

## 5. 应用层设计（Dsh.App）

### 5.1 投影仓库（`ProjectionStore`，对应 `02` A6）

```
ConcurrentDictionary<SessionId, ConcurrentDictionary<string, ProjectionCell>>
ProjectionCell = (seq: long, value: JsonNode)
写入规则：仅当 incoming.seq >= existing.seq 才覆盖（higher-seq-wins）
种子：history 尾页的 projections block（asOfSeq + values）
清空：世代重建时整库清空
```

### 5.2 交互协调器（`InteractionCoordinator`，对应 `02` D 域）

管理 `approval/requested` / `question/requested` 帧的接管状态：
- 收到 requested 帧 → 记录 pending（rpcId 为稳定逻辑 id）→ 通知 UI 接管 Composer
- 用户批准/拒绝/作答 → `Respond(rpcId, ApprovalResponsePayload | QuestionResponsePayload)`
- 收到 `approval/resolved` / `question/resolved` → 清除 pending，回传 outcome
- **禁止**把应答当 unary 方法；**必须** echo 稳定 rpcId

### 5.3 时间区采样（`TimeZoneSampler`，对应 `02` A5）

`TimeZoneInfo.Local.Id` 附到 `session.prompt` / `subagent.prompt` 的 `clientTimeZone` 字段（对应 `sessions.ts` 的 `user-rpc` source）。

---

## 6. 契约 DTO 生成方案

`Dsh.Contract` 是**手写还是生成**？给定契约源是 TS（`api/*.ts` 接口 + `rpc-map.ts`），推荐：

| 方案 | 优点 | 缺点 | 建议 |
|---|---|---|---|
| **手写 C# record + 单测对齐** | 无工具链依赖；类型可精确用 `record struct`/`OneOf` 表达 | 易漂移；工作量大 | Phase 0 首选 |
| **生成器（TS → C#）** | 契约变更自动同步（呼应 `05` C3 自动校验） | 需自研/引入工具；TS 联合类型→C# 多态复杂 | Phase 1 后引入 |

**结论**：Phase 0 手写 `Dsh.Contract` 并配**契约一致性单测**（把 `rpc-map.ts` 方法键、`RpcErrorDetailsMap` 键、`events.ts` 帧 type 字面量镜像成 C# 常量表，测试断言无遗漏）；Phase 1 后评估自研 TS→C# 生成器，把 `05` 的 C3 自动校验做实。

### 6.1 STJ 契约绑定规则（端到端联调实证，2026-08-15）

联调期间通过**实测 + 写测试**定位并修复的 System.Text.Json 绑定陷阱，以下为**必须遵守的硬规则**（避免将来重蹈覆辙）：

| 规则 | 原因 | 落地 |
|---|---|---|
| **每个 DTO 必须强类型**，禁止 `object`/`object[]` 返回 | STJ 不能把 JSON 对象反序列化为 `object[]`（报 "could not be converted to System.Object[]"），`object` 被强转 `JsonElement` | 每个 RPC 方法有对应 response DTO（如 `SessionListResponse`、`SessionCreated`）|
| **`null` payload 必须 fallback 为 `{}`** | host 所有 payload schema 都是 `z.object({...})`，字面 JSON null 全被 Zod 拒（"invalid payload"）| `WpfApiClient.Call` 内 `payload ?? new { }` |
| **枚举 + `[JsonPropertyName]` 必须配自定义 Converter** | STJ 默认 `JsonStringEnumConverter` 不读 `[JsonPropertyName]`，会写 enum 名（host 拒 `"ClientRequest"`）| `RpcErrorCode`、`RpcMessageType` 都配 `BuildIndex` 反射 Converter |
| **`object?` 反序列化后是 `JsonElement`** | STJ 对 `object` 目标强转 JSON 片段，需手动判别（如按 `ok`）| `Result` slot 手动 `TryGetProperty("ok")` 分支 |
| **DTO 字段名必须对齐 host wire**（如 `sessionId` 非 `id`） | 用 `[JsonPropertyName]` 显式标注 | `SessionSummary`、`SessionListResponse` 等 |
| **每次 GUI 报错 → 先写 wire-format 测试复现，再重新编译，最后才让用户点 GUI** | 测试驱动定位远快于 GUI 试错 | `WireFormatTests` 用 host 真实字节 |
| **API 类开发必须先 test 后调用**：涉及 host RPC/帧/投影的任何新方法或 DTO，必须先在 `Dsh.Contract.Tests` 写 wire 格式测试并通过，再在主程序（`SessionService`/`MainViewModel`）调用 | 避免"改主程序 → 反复点 GUI → 才发现契约错"的昂贵循环；测试驱动一次定位 | 每个新 RPC 方法配 wire 测试；主程序调用仅依赖已测试通过的 DTO |

---

## 7. UI 层设计（Dsh.Wpf，工作量主体）

### 7.1 MVVM 约定

- 每个 `02` 功能项对应一组 `View` + `ViewModel`；`ViewModel` 通过构造函数注入 `Dsh.App` 服务。
- 投影/流式数据用 `ObservableCollection` / `INotifyPropertyChanged` 绑定；WS 帧在 `TaskScheduler.FromCurrentSynchronizationContext()`（UI Dispatcher）上 marshal。
- Region 宿主替代 Cordis slot：静态注册（`Shell` 定义 `ContentControl` 命名 Region，`App.xaml.cs` 注册 VM 映射），首版不做运行时动态挂载第三方插件（`01` §3.2）。

### 7.2 关键控件映射（对应 `02` 域）

| Web（ui-* 包） | WPF 实现 | 功能项 |
|---|---|---|
| 侧栏树 | `TreeView` + 虚拟化 | B1/B2 |
| 聊天流 + 流式尾部 | `ItemsControl` + 虚拟化 + `FlowDocument` | C1/C2 |
| Think 折叠 | `Expander` | C3 |
| 工具调用树 | 递归 `TreeView`/`ItemsControl` + `DataTemplate` 按 `tool.call.toolview` 名分发 | C7/C8 |
| Composer | `TextBox`（IME/撤销可控）+ 队列 | C4/C5 |
| 审批接管 | 模态 `ApprovalPanel` | D1 |
| 问题回答 | `UserQuestionPanel`（逐题 + 批量 respond） | D2 |
| 模型选择 | 原生下拉 + 两级 Model/Effort | E1 |
| 设置页 | `SettingsPage` + `PasswordBox`（写只 Key） | E4/E5 |
| 子代理树 | `TreeView` 惰性展开 | F1 |
| 轨迹/时间轴 | `Tab` + `Canvas` 时间轴 + 拖拽缩放 | H1/H2 |
| Todo/Queue 面板 | `TodoDock` / `QueueDock` | H6/H7 |

### 7.3 原生增强（`02` J 域）

`app.manifest` per-monitor-v2（J6）、系统托盘 + balloon（J1）、多 `Window` 共享单 `WpfApiClient`（J2）、全局快捷键（J3）、文件关联 `.dshsession`（J4）、Credential Manager（J5）、目录选择 `OpenFileDialog`(Folder)（J8）、`Process.Start`/`explorer /select`（J9）。

---

## 8. 数据流（一次 prompt 的完整路径）

```
用户输入 Composer → ISessionService.Prompt
  → WpfApiClient.Call("session.prompt", {sessionId, mode, content, clientTimeZone})
  → POST /api/session.prompt → host agent 入队
  → host 经 mux WS 推 session/event 帧（assistant/message 增量、tool/call、step/*）
  → FrameRouter 按 sessionId 路由 → SessionFold 折叠 → ObservableCollection 更新
  → 投影帧 session/projection → ProjectionStore(higher-seq-wins) → 绑定刷新
  → 需要审批：approval/requested 帧 → InteractionCoordinator 接管 → 用户批 → Respond(rpcId)
```

---

## 9. 实现里程碑（承接 `01` §5，细化为可执行步骤）

| 里程碑 | 交付物 | 对应 `02` 状态 | 门禁 |
|---|---|---|---|
| **M0 契约层** | `Dsh.Contract`（信封/错误码/方法注册表/帧/投影）+ 契约一致性单测 | A7（错误解析） | `04` G1 契约一致 |
| **M1 通信层** | `Dsh.Wpf` + `WpfApiClient` + 世代 + 帧路由 | A1–A6 | `04` 域 A 门槛（A2/A4 实测） |
| **M2 MVP 对话** | 侧栏 + 聊天 + Composer + 流式 + 工具树 + 审批 | B1–B4, C1–C4,C7, D1 | `04` 域 B/C/D |
| **M3 完整协作** | 问题/Plan/权限/子代理/作业 | D2–D4, F, G | `04` 域 D/F/G |
| **M4 模型配置** | 设置页/凭据/provider/CAS | E | `04` 域 E |
| **M5 高级可视化** | 轨迹/时间轴/产物/反馈/Todo/Queue | H | `04` 域 H |
| **M6 原生增强** | 托盘/多窗/快捷键/关联/凭据/DPI/离线缓存 | J | `04` 域 J |
| **M7 健壮性** | 重连压测/冷会话边界/DPI/行为对照 | — | `05` C3 契约回归 |

---

## 10. 与其它文档的关系

| 文档 | 本文提供 |
|---|---|
| `02-feature-set.md` | 每个功能项 → 具体类/控件/里程碑（§7/§9） |
| `03-local-storage-viewer.md` | `Dsh.Viewer` 项目规划（§3.1） |
| `04-acceptance.md` | 每个里程碑的门禁引用 |
| `05-contract-sync.md` | DTO 生成方案（§6）与 C3 自动校验落地 |

---

*文档生成日期：2026-08-15；契约权威源以 `packages/host/apiproxy/src/api/*` 为准。*
