# 02 — WPF 客户端功能集（规则化单一事实源）

> 配套：`README.md`（地图）、`03-local-storage-viewer.md`（查看器子模块）。
> 本文件是 **WPF 客户端实现的主依据**：把 Web 客户端（`packages/client`）经 host 协议暴露的全部面向用户功能，规则化为可验收功能项，并内联"原生对比 / WPF 优势 / Web 缺口"。
> 协议细节来自 `packages/host/apiproxy/src/api/*`（HTTP POST unary/respond + 双 WebSocket 下行流 mux/host）。

## 0.1 状态核对记录（2026-08-19）

本文档主体写于 08-15~08-18，部分功能项的状态标记已**滞后于实际代码**。下面列出经代码核对后"实际已完成、但原标注仍为 ⬜/🟡/🔴"的项，避免后续误判为缺口：

| 原标记 | 功能项 | 实际状态 |
|---|---|---|
| 🟡/⬜ | C17 本地化 `.resx` + 语言切换 | ✅ `Localization` + `AppSettings.Language` + 工具栏切换（P2-8） |
| 🟡/⬜ | C16 产物行（Deliverables） | ✅ `SessionFold.Deliverables` + 右侧面板渲染（P2-5） |
| 🟡/⬜ | C12 轨迹视图（Trajectory） | ✅ `SessionFold.Trajectory` + 右侧面板（P2-6） |
| 🟡/⬜ | C10/C11 Todo 折叠条 | ✅ TodoList/TodoEdit 模板（P0） |
| ⬜ | D1 用户提问（Questions） | ✅ `PendingQuestion` 模态 + `answerQuestion` |
| ⬜ | D2 审批（Approvals） | ✅ `PendingApproval` 模态 + `resolveApproval` |
| ⬜ | D4 后台任务面板（Jobs/Queue） | ✅ `RightPanel` Jobs/Queue 列表（P1-10） |
| ⬜ | F1/F2 子代理树/编辑 | ✅ Subagent 节点 + 只读/续写（P1-12） |
| ⬜ | G2 模型选择 | ✅ `ModelChoices` + 切换（P1-6） |
| ⬜ | H1/H2 产物/轨迹视图 | ✅ 右侧面板绑定（P2-5/6） |
| 🟡 | H3/H4/H5/H6/H7 反馈/复制/统计 | ✅ 反馈命令 + 复制 + 统计格式化 |
| ⬜ | I1/I2/I4/I5 Jobs/Question 可视化 | ✅ 对应面板（P1/P2） |
| 🟡 | J2/J3/J4 多窗/托盘/全局键 | ✅ `NewWindow` + `TrayIcon` + `GlobalHotKey`（P1-14/P2-15） |
| 🟡 | J5/J6 会话树/分支切换 | ✅ 树 + 分支切换（P1） |
| ⬜ | K1/K2 语言切换/资源 | ✅ `.resx` + `LocExtension`（P2-8） |
| 🟡 | F7 搜索高亮 | ✅ `SnippetHighlighter` + `Segments` 渲染（P2-14） |
| 🟡 | D3 重连回放 | ✅ 重连后 `LoadHistoryAsync` 回放（P2-2） |

### 0.1.2 状态复核（2026-08-21）

> 以下为对 WPF 前端代码的只读复核结果，列出"实际已接线但原标记偏保守"与"标记偏乐观需修正"两类不一致项。

**实际已接线（状态应为 🟠 待验收）：**

| 功能项 | 证据 | 建议状态 |
|---|---|---|
| B1 Workspace 增量 upsert | `OnScopeHostFrame`→`UpsertWorkspaceNode`/`RemoveWorkspaceNode`/`ReorderWorkspaceNodes` | 🟡→🟠 |
| B2 会话悬停卡 | `MainWindow.xaml` 会话项 ToolTip 卡 + `SessionItem` 数据（行号随重构会变） | 🟡→🟠 |
| B7 Fork 会话 | `ForkSessionAsync` + 菜单 + `session.fork` RPC | 🟡→🟠 |
| B11 拖拽排序 | TreeView 拖拽 + `InsertWorkspaceBefore`/`InsertSessionBefore` + host echo 修正 | 🟡→🟠 |
| B12 悬停复制路径/标题 | `CopySessionPath/Title/WorkspacePath` + 右键菜单 | 🟡→🟠 |
| C5 busyEnter 偏好持久化 | `AppSettings.BusyEnterAction` + ComboBox + 保存 | 🟡→🟠 |
| H3 产物 turnTail + `host.openPath` | `Deliverables` chip + `OpenDeliverableCommand` | 🟡→🟠 |
| H5 消息反馈点赞/点踩 | `👍/👎` 按钮 → `PutMessageFeedback`（`messageFeedback.put`） | 🟡→🟠 |

**标记偏乐观、需下修（实现为降级/非富样式）：**

| 功能项 | 实际 | 建议 |
|---|---|---|
| C17 StatsLine 富样式 UI | 仅为普通 `TextBlock` 状态栏文本，无 ring/TTFT 富样式 `StatsLine` | 保持 🟡，注明"富 UI 未做" |
| H2 时间轴 Canvas | `Canvas` 时间轴/拖拽聚焦/滚轮缩放**不存在**，仅只读 `ListBox` | 保持 ⬜（UI 未实现） |
| H6 TodoDock | 仅右面板只读 `ListBox`，无 Composer 折叠 Dock/计数/编辑 | 保持 🟡，注明"Dock/编辑未做" |
| H7 QueueDock | 仅右面板只读 `ListBox`（`SyncQueue` 快照），无编辑/删除/严格 steer | 保持 🟡，注明"Dock 操作未做" |
| E11 凭据徽标点 | 仅文本三态（`StatusLabel`），无绿/红"点"徽标 | 保持 🟡，注明"点样式未做" |
| B9 搜索高亮 debounce | 高亮真接线，但按钮触发无 250ms 防抖、无 cap-20 截断 | 🟡→🟠，注明"debounce/cap 待补" |

**确认未实现（与文档 ⬜ 一致）：** K4 字体/bidi、I1-L1 解码管线。（D3/D5/H4 已分别在 2026-08-21 实现或复核确认，见各自行。）

**真正仍为缺口 / 待优化（本次已部分补齐）：**
- 🔴 **I 域本地存储查看器（L0-L4）**：`Dsh.Viewer` 此前为空骨架；本次已交付 L0 基线（目录扫描 + 复用 `SessionFold` 渲染 `.jsonl`/`.json`），L1 解码管线（SessionPathResolver / ZstdReader / ChunkRowExpander）已实现（待补单元测试）；L2 融合主客户端、L3 渲染增强、L4 检索导出仍待实现（见 `03`）。
- 🟡 **C13/C14 compaction/retry 特殊行 UI**：数据层已生成 `Row("compacted"/"retry")`，但 `ChatEntryTemplate` 缺少对应 DataTrigger → 落入默认模板渲染错乱；**本次已修复**（新增 `MetaTemplate` + 触发器）。
- 🟡 **K4 始终置顶开关**：原仅有启动期 bring-to-front；**本次已修复**（持久化 `AppSettings.Topmost` + 工具栏切换 + `Topmost` 双向绑定）。
- 🟡 **D5 窄屏响应式**：原布局固定；**本次已修复**（窗口 <980px 自动折叠右侧面板，用户手动固定后禁用）。
- 🟡 **A 域 `/clear`、C19 内联文件提及悬浮**：仍为降级（降级方案已存在）。
- ⬜ **离线缓存/弱网重连（OfflineCache）**：`OfflineCache` 类存在但未接入连接层自动回放。
- ⬜ **自动更新/CI 管线**：无。



## 0. 优先级与约定

- **P0** 最小可用（会话对话闭环）、**P1** 完整体验（设置/协作/可视化）、**P2** 增强与高级（原生优势、洞察）。
- 依赖标记：`→` 前置功能项或契约模块。

| 域 | Web 来源包 | WPF 目标  |
|---|---|---|
| A. 连接与运行时 | `runtime`, `connection` | `WpfApiClient` + `SessionRuntime` 等价层  |
| B. 会话与 Workspace | `ui-workspace`, `runtime` | 侧栏树  |
| C. 对话核心 | `ui-conversation`, `ui-tool`, `ui-input-trigger`, `ui-commands` | 主聊天视图 + Composer  |
| D. 实时协作交互 | `ui-user-questions`, `ui-plan`, `ui-conversation`(approval) | 模态/接管控件  |
| E. 模型与配置 | `ui-model-selection`, `ui-settings-models`, `runtime`(bindSettings) | 设置页 + Composer 片段  |
| F. 子代理 | `ui-subagent` | 目录树 + 只读/续写  |
| G. 后台作业 | `runtime`(jobsBySession), `events`(session/jobs) | 作业面板  |
| H. 高级可视化 | `ui-trajectory`, `ui-deliverables`, `ui-message-feedback` | 轨迹视图 + 产物行  |
| I. 本地存储查看 | `03-local-storage-viewer.md` | 独立只读模块  |
| J. WPF 原生增强 | — | 系统托盘/多窗/全局键等  |
| K. 本地化 i18n | `ui-*`（各包 `locales.ts` 双语字典） | `.resx` + 语言切换（等价覆盖） |

**核心判断**：WPF 对 Web 客户端是"**超集 + 替换载波**"——所有模型/工具/会话功能等价（走同一 host），在原生交互/窗口/系统集成上更强；唯一需注意 Web 的"多标签页共享 host 状态"在 WPF 变"单进程多窗口"，需自实现会话级状态隔离。

---

## A. 连接与运行时（P0，前置全部）

| ID | 功能 | Web 行为 | WPF 实现 | 验收 | 状态 | 原生对比 |
|---|---|---|---|---|---|---|
| A1 | HTTP unary 客户端 | `POST /api` + `POST /api/respond` | `HttpClient` 封装 `call<R>(method, params)` / `respond(rpcId, payload)` | 任意 RPC 回显 `rpcId` 与结果 | 🟢 | 等价  |
| A2 | 双 WebSocket 下行流 | mux（全会话聚合）+ host（会话增删/状态） | 两个 `ClientWebSocket`：mux 帧→所有者，host 帧→全局 | 收到 `host/session-added`/`session/event` 并路由 | 🟡 | 等价（最高风险：世代重建；`DownstreamStreams` 连接/读取 + 断线重建已实现于 `ConnectionScope.RunLoopAsync`（P1-14/16 重构后），待验收证据补全）  |
| A3 | loopback 信任栅栏 | 仅 `127.0.0.1` 开放高权限 API | 同机连接天然满足；仍发 `application/json` 以兼容 415/信任检查 | 凭据/目录类 API 可调用 | 🟢 | **优势**：本机天然满足  |
| A4 | 重连世代（generation） | 断线清状态，重开流+重拉 history | `ConnectionGeneration` 计数器；断线清空 `pendingInteraction`/`jobsBySession`，重连重订阅 | 断线恢复后无残留 pending 态 | 🟡 | 等价（计数已实现；`ReconnectPolicy` 提取 + 重连后状态重拉已闭环于 `ConnectionScope.RunLoopAsync`，待验收证据补全）  |
| A5 | 时区采样 | `Intl.DateTimeFormat().resolvedOptions().timeZone` | 采 `TimeZoneInfo.Local.Id` 附到 prompt/subagent.prompt | 非空白 zone，缺省本地失败提示 | 🟢 | **差异**：用 `TimeZoneInfo.Local.Id` 替代 `Intl`  |
| A6 | 投影值仓库 | `ProjectionValueStore` per-session，higher-seq-wins | `ConcurrentDictionary<string,(seq,value)>` + 等价订阅 | `session/projection` 按 seq 覆盖 | 🟢 | 等价（须逐字节对齐）  |
| A7 | 转义/错误解析 | Zod 双层解析 | `System.Text.Json` + 显式 `RpcError` 映射 | 业务错误码（`agent-busy`/`settings-conflict` 等）可分支 | 🟢 | 等价  |

---

## B. 会话与 Workspace（P0）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 | 原生对比 |
|---|---|---|---|---|---|
| B1 | Workspace 树 | `workspace.list` 基线 + `host/workspace-*` 帧增量 | `TreeView`/虚拟化列表，按 `workspace.list` 种子，`host/workspace-changed`/`workspace-removed`/`workspace-order-changed` upsert | P0 | 🟠 | **优势**：可直接在资源管理器定位会话目录（`canOpenPath`=true）（`WorkspaceNode` 树已实现；`OnScopeHostFrame`→`UpsertWorkspaceNode`/`RemoveWorkspaceNode`/`ReorderWorkspaceNodes` 增量 upsert 真接线，待验收证据）  |
| B2 | 会话行（活动/等待状态） | 蓝运行点、琥珀等待点、悬停卡 | `DataTemplate` + 状态徽标；`pendingInteraction` 分类 | P0 | 🟠 | 等价（`SessionStatusDotConverter` 蓝/琥珀状态点 + 悬停卡已实现：`MainWindow.xaml` 会话项 ToolTip 卡 + `SessionItem.UpdatedAtText/KindText/KindKey` 稳定枚举 DataTrigger，待验收证据） |
| B3 | 新建会话 | `session.create({workspaceId})` 复用 blank | 复用 `blank && cwd==path` 否则 create | P0 | 🟢 | 等价（`NewSessionAsync` 先 `FindReusableBlankSession` 复用当前工作区 blank 会话，无才 `Create`；`SessionItem.Blank` 标记，避免每次点击新建）  |
| B4 | 打开/选择会话 | `session.list`/`session.history` 分页 | 选中→加载 history 尾页 + 订阅 mux | P0 | 🟢 | **优势**：可本地缓存离线浏览  |
| B5 | 重命名会话 | `session.rename`（`title-invalid` 可失败） | 对话框预填，失败原地报错 | P1 | 🟢 | 等价  |
| B6 | 归档会话 | `workspace.archiveSession`（无确认） | 右键→归档 | P1 | 🟢 | 等价（`ArchiveSessionCommand` + 会话右键菜单；契约修正：payload 仅 `{sessionId}`，registry 全局集，帧 `host/archived-sessions-changed`）  |
| B7 | Fork 会话 | `session.fork` | 右键→fork→打开子会话 | P1 | 🟠 | 等价（`ForkSessionAsync` + 右键菜单 + `session.fork` RPC 已实现，待验收证据）  |
| B8 | 删除 workspace | `workspace.delete` + `host/workspace-removed` | 确认框（陈述保留边界） | P1 | 🟡 | 等价（`DeleteWorkspaceAsync` + 工作区节点右键"删除工作区" + 确认框陈述保留边界，wire 测试；`host/workspace-removed` 帧路由待补）  |
| B9 | 会话内搜索 | `session.search` 250ms debounce，cap 20 | 侧栏搜索框→结果列表 | P1 | 🟠 | 等价（`SearchSessionsAsync` + 侧栏搜索框 + 结果列表已实现；**P2-13 搜索高亮**：`SnippetHighlighter` 命中段高亮 `HighlightBackground`+Bold；**2026-08-21 补 250ms debounce**：`OnSearchQueryChanged`+`DispatcherTimer` 输入停顿触发，空框即时清空；cap-20 由 host 侧截断、`HasMore` 提示，客户端不重复截断；待验收证据）  |
| B10 | 目录选择（Add workspace） | `host.pickDirectory`（Windows: IFileOpenDialog） | 走 `host.pickDirectory` 或 WPF `OpenFileDialog`(Folder) P/Invoke；占用 `conversation.hero.workspace.directoryFlow` 洞 | P1 | 🟡 | **优势**：直调 Windows API，省子进程+koffi，per-monitor-v2 天然（`AddWorkspaceAsync` 走 `host.pickDirectory` + `workspace.create`，顶部按钮已实现，待验收证据）  |
| B11 | 拖拽排序 | `workspace.insertBefore`/`workspace.insertSessionBefore` | 拖拽→乐观序→unary echo 修正 | P2 | 🟠 | 等价（**P2-1 已实现**：`ISessionService.InsertWorkspaceBefore/InsertSessionBefore` + TreeView 拖拽→RPC→host echo 修正；待验收证据）  |
| B12 | 悬停复制路径/标题 | 剪贴板写 | `Clipboard.SetText` + 状态提示 | P2 | 🟠 | **优势**：原生剪贴板（**P2-2 已实现**：`CopySessionPath/Title/WorkspacePathCommand` + 右键菜单；待验收证据）  |

**Web 缺口弥补（B 域）**：Web 多标签页共享 host 状态需多 tab 协同；WPF 用单连接 + 多 `Window` 扇出（每窗独立 `Session` 镜像，共享同一 `WpfApiClient` 流）。

---

## C. 对话核心（P0）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 | 原生对比 |
|---|---|---|---|---|---|
| C1 | 会话主视图（Chat） | 分组 step-summary 流 + 流式尾部隔离 + turn 状态 | `ItemsControl`/虚拟化 + 等价折叠 | P0 | 🟢 | **优势**：`FlowDocument` 更精细排版 diff/code  |
| C2 | 流式消息尾部 | assistant 增量 chunk → think/内容分离 | `session/event` 帧增量渲染，合并 | P0 | 🟢 | 等价  |
| C3 | Think 行 | 默认折叠，流式跟随；展开全推理 | `Expander` + 流式尾部单行滚动 | P0 | 🟢 | 等价  |
| C4 | 发送消息（Queue） | `session.prompt`；idle 时 Enter/Cmd+Enter 入队 | Composer `TextBox` + 队列发送 | P0 | 🟢 | 等价  |
| C5 | 运行中 Steer | busy 时 Enter=Queue/Cmd+Enter=Steer（偏好） | `busyEnter` 偏好（存 `$DSH_HOME/settings.yaml`）；`session.prompt(mode:'steer')` | P1 | 🟠 | **优势**：IME/撤销栈比 `<textarea>` 可控（`SendSteerCommand` 以 steer 插队；`AppSettings.BusyEnterAction` + ComboBox + `OnBusyEnterActionChanged` 持久化已实现，待验收证据）  |
| C6 | 图像摄入 | 粘贴/整页拖入；`imageLimits` 校验 | `Clipboard.GetImage()` + `DragDrop`；校验拒绝整批 | P1 | 🟢 | **优势**：原生剪贴板比 `navigator.clipboard` 可靠（不需 https）  |
| C7 | 工具调用树 | `tool.call` 递归 root/child，`tool.call.toolview` 按名分发 | 等价：递归 `subCalls` + 原子分发 | P0 | 🟢 | 等价  |
| C8 | 工具卡片（terminal/read/diff/search/web/todo/question/code） | `ui-tool` 内置 + 业务包注册 | 对应 `ToolRow`；未注册名→通用 JSON 卡 | P0/P1 | 等价  |
| C9 | 工具结果打开文件 | Host `openFile` 回调（cwd 解析） | `host.openPath` 或 `Process.Start` WPF 版 | P1 | 🟢 |（`OpenFolderCommand` 走 `host.openPath` + 会话右键"打开文件夹"，基于 `SessionItem.Cwd`）  |
| C10 | `/` 命令触发 | `/` 检测 + 候选菜单；**契约澄清**：斜杠命令是 `session.prompt` 特例（content 单文本块以 `/` 开头即命令，mode 无关），**非** `command.*` RPC | Composer 内 `/` 检测 + 候选菜单（`UpdateCommandMatches`/`ApplyCommand`，焦点留 textbox） | P1 | 🟡 | **优势**：可调系统输入法候选（已实现，待验收证据）  |
| C11 | `@` 子代理引用 | 仅插入字面 `@label` | 同 C10；插入文本 | P2 | 🟡 | 等价（**P2-3 已实现**：`UpdateCommandMatches` 检测 `@` 前缀建候选 + `ApplyCommand` 插 `@` 文本；待验收证据）  |
| C12 | 命令目录缓存 | **契约澄清**：目录源为 `skill.list`（`/&lt;name&gt;`）+ `agentPreset.list`（`/id|name`），非 `command.list`（host 无 command 域） | `LoadCommandCatalogAsync` per-session 合并 skill+preset 到 `CommandCatalog`，切会话重载 | P1 | 🟡 | 等价（已实现，软失效/事件订阅待补，待验收证据）  |
| C13 | 上下文注入披露 | 折叠，含 producer 名 | 折叠 `DisclosureRow` 等价 | P2 | 🟡 | 等价（**2026-08-24 已实现**：`SessionFold` 已生成 `role="context"` 行（含 producer 标签，`BuildContextDisclosure`）；XAML 新增 `ContextTemplate`（Accent 左描边 + `Fold.ContextLabel` 标签，6 语言）+ `ChatEntryTemplate` 注册 `context` DataTrigger，不再误渲染为助手气泡。**折叠展开交互未做**（目前整块披露，折叠需额外状态））  |
| C14 | Compaction 检查点 | 折叠行 + 计数 + 展开摘要 | 检查点节点 + 展开摘要 | P2 | ⬜ | 等价  |
| C15 | 重试状态行 | 合并跨 retry turn，倒计时（客户端锚定时钟），∞/有限 | 状态行 + 客户端倒计时（避免时钟偏移） | P2 | 🟡 | 等价（`SessionFold.HandleRetry` 已生成 `role="retry"` 行，含 attempt/max/delay/∞，静态"正在重试"文案（MetaTemplate 居中条）已呈现。**客户端倒计时未做**：`delayMs` 通常仅数百 ms，倒计时一闪而过价值有限；且需 SessionFold 暴露 RetryDelayMs + ChatEntry 加 RetryAt + VM DispatcherTimer 三处改动。已记录为增强项，暂缓）  |
| C16 | 终端失败状态 | 内联持久 + 错误码（不回显 AUTH 片段） | 错误行 + `attachmentErrorText` 映射 | P1 | 🟡 | 等价（**核心安全已实现**：`SessionFold.EndTurn` 对 `reason.kind=="error"` 生成错误行，`code=="AUTH"` 特判 `Fold.TerminalAuth` 不回显凭据片段。**注**：02 原文的"attachmentErrorText 映射"实为 Web 端图片附件预检错误码映射（`details.reason`：`MODEL_DOES_NOT_SUPPORT_IMAGES` 等），与 LLM terminal failure 的 `reason.error.code` 是两回事，不适用于本行；LLM 错误码完整映射表需 host 错误码全集（不可得），当前对非 AUTH 显示 `message`（空则 `Fold.TerminalFailed`）已满足）  |
| C17 | 统计条（tokens/turns/steps/时长/吞吐） | `tokenUsage`+`sessionStats` 投影 | `StatsLine`：ring 上下文占用 + TTFT/tok-s | P1 | 🟠 | **契约缺口已解除（2026-08-18）**：回源 host 确认 apiproxy 把投影单元变化推为 `session/projection` 帧，`session-stats`/`token-meter` 插件经 Loader 装配，`sessionStats`/`tokenUsage` 键已注册并推送，与客户端 `ProjectionKeys` 一致。**2026-08-21 富 `StatsLine` 已实现**：`SessionStatsFormatter.FormatRich` 计算 ring 分数 + detail，VM 暴露 `HasRichStats/StatsRingFraction/StatsRingText/StatsDetailText`，状态栏富 UI（圆章百分比 + detail chip），无投影时降级文本。**2026-08-24 对齐 Web `StatsLine.tsx` 完整字段集**：pipe 分隔 5 组（counts 轮数·步数 | durations LLM·工具调用 分离 | speeds 首token平均·tok/s | cacheHit 缓存命中率 | tokens 输入·输出 分离），`FormatRich` 用 `projectedTokens ?? pressureTokens` 算 ring，新增 `CacheHitPercent`（对齐 Web 整数百分率）/`BilledInputTokens`/`FormatTokenCount`，`FormatDuration` 对齐 Web（<60s 用 `x.xs`）；新增 `Stats.Counts/Llm/ToolCall/TtftAverage/TokensPerSecond/CacheHit/Tokens` × 6 语言。`FormatRich` 单测更新。待验收证据 |
| C18 | 消息操作（复制/时钟/分支） | 末条 IconActions；branch 限完成 turn 末节点 | 复制/分支按钮；fork 走 B7 | P1 | ⬜ | 等价  |
| C19 | 用户消息不可编辑 | Web 限制 | 同限制 | — | ⬜ | 等价  |

---

## D. 实时协作交互（P0/P1）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 | 原生对比 |
|---|---|---|---|---|---|
| D1 | 审批接管 Composer | `approval/requested` 帧（稳定 rpcId）→ `PendingApproval` 占 Composer，amber 条+理由+命令；`allowed-once`/`rejected`→`POST /api/respond` 带 rpcId | `ApprovalPanel` 模态/接管；`respond(rpcId, ApprovalResponsePayload)` | P0 | 🟢 | **优势**：系统级模态防误操作（`InteractionCoordinator` 状态机 + ApproveOnce/Reject）  |
| D2 | 问题回答（ask-user） | `question/requested` 帧→逐题单选/多选/推荐/自定义；整批 `QuestionResponsePayload` | `UserQuestionPanel`：进度导航 + 批量 `respond(rpcId,...)`；IME Enter 不前进 | P1 | 🟢 | **优势**：模态比浮层聚焦（`QuestionUi` + SubmitAnswers）  |
| D3 | Plan 模式芯片 | `plan` 投影 `pending? !active : active`；`/plan off` 退出 | Composer 旁 `Plan ×` → `command.execute('/plan off')` | P1 | 🟠 | **2026-08-21 已实现**：`RefreshPlanAndPermissions` 读 `PlanProjection`（`_projections.Get(ProjectionKeys.Plan)`）→ `IsPlanMode`；XAML plan chip 点击 `TogglePlanCommand` 填 `/plan off` 到 composer（回车发送，符合斜杠命令经 prompt 特例的契约）。`plan` 投影键是否挂载由 host 决定，未挂载时芯片隐藏。待验收证据  |
| D4 | Plan 评审呈现 | `plan-review` intent→审批卡式 | 问题卡识别 intent→复用 D2 布局 | P1 | 🟡 | 等价（`QuestionUi` 暴露 `Intent.Kind`/`Approve` + `IsPlanReview`；XAML 渲染 Plan 评审批准卡 + 批准按钮；提交时 plan-review 无选项自动以 approve 文本为答案。wire 测试 +2，待验收证据）  |
| D5 | 权限选择芯片 | `permissions` 投影→Menu 预设名；`/permission <preset>` 或 `danger-full-access` 二次确认 | Composer 底排 `PermissionSelect` 等价 | P2 | 🟠 | **2026-08-21 已实现**：读 `PermissionSelect` 投影 → `PermissionOptions`/`PermissionCurrent`/`HasPermissionSelect`；XAML chips 渲染预设（`Surface3` chip + `GhostButton`），点击 `SetPermissionCommand` 填 `/permission <value>` 到 composer；`danger-full-access` 二次确认由 host 侧处理。待验收证据  |

**关键契约**：approval/question 是 server-request，其 `rpcId` 是稳定逻辑 id；客户端回复**不是** unary 方法，而是 `POST /api/respond` 携带 `RpcReceipt` 信封，最终 outcome 由 `approval/resolved`/`question/resolved` 帧落地。

---

## E. 模型与配置（P1）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 | 原生对比 |
|---|---|---|---|---|---|
| E1 | 模型选择（Composer 片段） | `session.models` 两级 Model/Effort；`session.selectModel` | Composer `model` 座位下拉；提交 `ModelSelection` | P1 | 🟢 | 等价；**优势**：原生下拉性能（`LoadModelsAsync`/`SelectModelAsync`）  |
| E2 | `/model` 命令 | `popupSelect` 贡献 | C10 命令源之一 | P1 | 🟡 | 等价（`/model` 为客户端本地命令：`TryHandleLocalModelCommand` 处理 `/model [查询]`，加入 `CommandCatalog`「本地」组；`/model <名>` 直接切换模型。待验收证据）  |
| E3 | 无路由 composer block | `routable=false`→锁输入 | 同 block 机制 | P1 | 🟡 | 等价（`ModelsResult.Routable` 已有；`IsRoutable` 属性绑定 Composer 输入 `IsEnabled` + 红色提示条。待验收证据）  |
| E4 | 设置页（Models） | `llm.providers`+`settings.describe`+`credentials.describe` 三域快照 | `SettingsPage`：provider 行 + 编辑器卡 | P1 | 🟢 | **优势**：可写 AppData 本地偏好缓存  |
| E5 | API Key 录入（写只） | `credentials.set` 派生 `<ROUTE>_API_KEY`；不回显 | `PasswordBox`→`credentials.set`；校验 printable ASCII | P1 | 🟢 | **优势**：`PasswordBox` 不进 DOM  |
| E6 | 自定义 provider（pi-ai） | `settings.mutate` 整 profile + `credentials.set` | 创建卡：id/baseURL/protocol/≥1 model | P2 | ⬜ | 等价  |
| E7 | 端点模型探测 | `llm.discoverModels` 当前表单值（含未存 key） | "Fetch models" → 选择器（已配置默认不勾） | P2 | 🟢 | 等价  |
| E8 | 设置 mutation + 冲突 | `settings.mutate` 带 `revision`；`settings-conflict` 拒绝 | 每次写带 revision；冲突→重读重试 | P1 | 🟢 | 等价（须逐字节对齐）  |
| E9 | 首次引导 | `settings.onboarding` 排序 | 首启对话框（loopback 写 `ui-onboarding.welcomeNoticeVersion`） | P2 | ⬜ | 等价  |
| E10 | 设置/凭据事件订阅 | `settings/document-updated`、`credentials/updated`、`updated` | 转发 owner 事件→失效缓存 | P1 | 🟡 | 等价（`AppendEvent` 检测 owner 事件 type：`settings/document-updated`→`LoadSettingsAsync`、`credentials/updated`→`LoadCredentialsAsync`、`llm/adapters-updated`→`LoadModelsAsync`。待验收证据）  |
| E11 | 凭据描述 | `credentials.describe`（configured/source/writable，无值） | 徽标（绿点有/红点缺/无标记原生认证） | P1 | 🟠 | **2026-08-21 已实现**：`CredentialEntry.StatusBrush`（Configured→`Accent` 绿点 / 缺失→`Danger` 红点）+ 设置行 `Ellipse` 状态点 + 引用名 + `StatusLabel` 文本。待验收证据  |

---

## F. 子代理（P1）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 | 原生对比 |
|---|---|---|---|---|---|
| F1 | 子代理目录树 | `subagent.list`→头部动作；活动/时长/token 汇总 | `TreeView` 惰性展开；`hasChildren` 预占箭头 | P1 | 🟢 | **优势**：多窗口并排对比主/子代理  |
| F2 | 只读子代理（one-shot） | 始终只读 Composer | 同（只读副本） | P1 | 🟢 | 等价  |
| F3 | 续写子代理（continuable，父可用） | 普通输入→`subagent.prompt`；独立 Stop→`subagent.interrupt` | 普通输入路由 + 停止走 interrupt | P1 | 🟢 | 等价  |
| F4 | 续写子代理（父不可用/停） | 只读 + 复制恢复路径 | 只读 + 复制提示 | P2 | 🟢 | 等价  |
| F5 | 查看子代理 transcript | `subagent.history` 分页（不激活 Agent） | 只读会话视图走 `subagent.history` | P1 | 🟢 | 等价  |
| F6 | 中断运行中 | `subagent.interrupt` fire-and-return | 停止按钮→interrupt | P1 | 🟢 | 等价  |
| F7 | `@` 引用插入 | 仅字面 `@label` | C11 | P2 | ⬜ | 等价  |

---

## G. 后台作业（P1）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 |
|---|---|---|---|---|
| G1 | 作业面板 | `session/jobs` 帧全量快照（registry 无持久事件） | per-session 列表，由 `session/jobs` 帧 last-wins | P1 | 🟢 |
| G2 | 作业状态/详情 | `kind`/`label`/`status`/`detail`/`startedAt`/`finishedAt` | `DataTemplate` 状态色 + 详情 tooltip | P1 | 🟡 |（`JobStatusColorConverter` 状态色已实现；详情 tooltip 待补）  |
| G3 | 作业 kill | （Web 经 continuation/owner，无独立按钮记录） | 可选：`job` 控制（依赖 host `jobs` 域，若暴露） | P2 | ⬜ |

---

## H. 高级可视化（P1/P2）

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 | 原生对比 |
|---|---|---|---|---|---|
| H1 | Trajectory 事件账本 | 可选 tab：turn/step/User/Assistant/Tool/Subtool；inspector | 独立 `Tab` + 虚拟行 + 选中详情面板 | P2 | 🟡 | 等价（**P2-6 数据模型已实现**：`SessionFold.Trajectory`/`TrajectoryStep` 事件顺序回放；步骤跳转 Tab UI 待补）  |
| H2 | 时间轴 Overview | 左→右投影 start/duration；TTFT vs decode | `Canvas` 时间轴 + 拖拽聚焦 + 滚轮缩放 | P2 | 🟠 | **2026-08-21 富化时间轴**：从 `FrameworkElement` 升级到 `Canvas`（子 Ellipse 提供 hit-test + ToolTip），**ToolTip 显示 `[kind] #i/total seq=N +X.Xs\n<Text>`**；加时间刻度（首点/中点/末点的 `t=0` / `+X.Xs` 标签 + 短刻度线）；圆点放大到 10px + Surface1 描边；hover 高亮外环（Accent 描边 2px）；图例与基线按主题 token。**仍缺：拖拽聚焦、滚轮缩放**（Overview 交互层）  |
| H3 | 产物文件行（ProducedFiles） | 末 turn 列出成功 mutation 文件（render-intent 识别）；点击 `openFile`；"Show in folder"（`canOpenPath`） | turnTail 行 + chip 打开；文件夹交 `host.openPath` | P1 | 🟠 | **优势**：资源管理器定位（**P2-5 派生逻辑已实现**：`SessionFold.Deliverables` 按 render intent 提取 `locations[].path`；turnTail chip 渲染 + `OpenDeliverableCommand`→`host.openPath` 真接线，待验收证据）  |
| H4 | 内联文件提及 | prose 中 `inline-code` 路径→可点击 | 渲染时识别 `chatFileMentions` 词汇 | P2 | 🟢 | **已实现（本次复核确认，非新增）**：`MarkdownSegmenter.IsFilePath`（L174 识别 `C:\`/`/`/`.`/含分隔符或点）→ `MarkdownRenderer` L240 升级 `InlineKind.File` → `AssistantMessageControl.MakeHyperlink(isFile:true)` → `MdNavigate.Open`（HTML 内嵌预览 / 本地默认应用 / `OpenFileCommand` 回退）；`MarkdownRendererTests` L130 回归测试 `Backtick_file_path_is_clickable_file_mention`  |
| H5 | 消息反馈（Like/Dislike+note） | `messageFeedback.list` 懒加载；`put`/`delete` 带 version CAS | 等价；重试 `version-conflict` | P2 | 🟠 | 等价（**P2-4 契约已实现**：`MessageFeedbackModels` 三 RPC DTO + wire 测试；UI `👍/👎` 按钮→`SetMessageFeedbackAsync`→`messageFeedback.put` 已接线，待验收证据）  |
| H6 | Todo 面板 | `todos` 投影（stand plan）→折叠带计数 | Composer 上 `TodoDock` 等价 | P1 | 🟡 |（`TodoStatusColorConverter` + Todo Tab 只读 `ListBox` 已实现；**TodoDock 折叠计数/编辑未实现**——仅只读显示）  |
| H7 | Queue 面板 | `session/queue` 快照→排队消息行（编辑/删除/严格 steer） | Composer 下 `QueueDock` 等价 | P1 | 🟡 |（`SyncQueue` 快照 + 队列 Tab 只读 `ListBox` 已实现；**契约已就绪**：`session.updateQueue`（`RpcMethodMap.cs:22`，`action: edit|remove|steer`）+ `QueuedInboxItem.Id`=itemId，**客户端未接线**——编辑/删除/steer 可落地，非 blocked-by-host）  |

---

## I. 本地存储查看器（独立模块，详见 `03-local-storage-viewer.md`）

> **实现路径指引（与 `03`/`07` §1.1 一致）**：本模块基于本仓库 `Dsh.App.SessionFold` 自研——离线 JSONL 逐行 `FrameConverter.ReadSessionEvent` → `fold.Fold(jsonElement)`，复用在线客户端同一折叠层。下方状态以"L0-L4"分期标注（分期定义见 `03` §6）。

| ID | 功能 | 优先级 | 状态 |
|---|---|---|---|
| I1 | 根目录浏览 + 项目/会话树（读 `~/.dsh/sessions/`） | P0 | 🟡 L0 基线已交付（`Dsh.Viewer` 目录扫描 + SessionFold 渲染）；**L1 解码管线 2026-08-24 已实现**：`SessionPathResolver.Scan`（`--<encoded-cwd>--/<encoded-sessionId>/session.jsonl[.zstd]` 路径解码 + `DecodeSegment` 反转 `~XXXX` 转义）+ `ZstdReader`（ZstdNet 透明解压）+ `ChunkRowExpander`（text/reasoning/tool-call-chunks → 原始 `assistant/chunk`，对齐 `chunk-rows.ts` expandRow）+ `LoadSelected` 接入 zstd 与 chunk 展开。完整树 UI 分组仍待 L2 |
| I2 | JSONL/zstd 双格式 + chunk 展开 + surface 投影 | P0 | 🟡 **L1 解码管线已实现（2026-08-24）**：SessionPathResolver（`--cwd--/sessionId/` 路径解码）+ ZstdReader（ZstdNet 透明解压）+ ChunkRowExpander（text/reasoning/tool-call-chunks → 原始 `assistant/chunk`）；**workspace→session 树分组已实现（2026-08-24）**：左侧由扁平文件列表改为 TreeView（`SessionPathResolver.Scan` 分组，`DecodeSegment` 解码 cwd/sessionId 标签，zstd 后缀标注），选中会话即经 L1 管线渲染。**待做**：L1 单元测试；surface 投影/检索属 L3-L4 |
| I3 | 基础统计 + Markdown/JSONL 导出 | P0 | ⬜ L3/L4 |
| I4 | Markdown 渲染 + 代码高亮 + 工具调用树 + 图片渲染 | P1 | ⬜ L3 |
| I5 | 跨会话全局搜索 + HTML/CSV/批量导出 + 内嵌主客户端 Tab | P1 | ⬜ L2/L4 |
| I6 | token 成本聚合 + 工具分布图 + 轨迹视图 | P2 | ⬜ L4 |

**兼容契约（最高风险）**：`DecodeSegment`/`ChunkRowExpander` 必须与 `core/session/src` 的 `encodeSegment`/`expandRow` 逐字符一致；`KnownEventTypes` 随 `known-event-types.ts` 同步。

---

## J. WPF 原生增强（Web 无，应纳入）

| ID | 功能 | 说明 | 优先级 | 状态 |
|---|---|---|---|
| J1 | 系统托盘图标 + 通知 | 后台作业完成/等待审批时 balloon | P2 | 🟡 |（`TrayIcon` 已实现：最小化到托盘 + 显示/退出 + 连接/重连 balloon；**2026-08-24 新增审批 balloon**：`OnApprovalRequested` 收到 `ApprovalRequestedFrame` 时 `App.Tray.NotifyBalloon`（`Tray.ApprovalTitle/BodyTool/BodyReason` 6 语言，按 `ApprovalId` 去重）——窗口最小化到托盘时用户仍能察觉审批请求。作业完成 balloon 未做：`Jobs` 为全量快照，完成判定需 diff，暂缓）  |
| J2 | 多独立窗口跨屏 | 每会话独立 `Window`，共享单连接；Web 多 tab 受同源限制 | P1 | ⬜ |
| J3 | 全局快捷键 | 新建会话/聚焦 Composer（无焦点也可）；Web 仅页内 | P2 | ⬜ |
| J4 | 文件关联 | `.dshsession` 双击打开查看器 | P2 | ⬜ |
| J5 | Windows 凭据管理器集成 | `credentials.set` 可选落地 Credential Manager | P2 | ⬜ |
| J6 | DPI 感知 | `app.manifest` per-monitor-v2；Web 依赖浏览器 | P0（天然） | 🟢 |（`App` 构造函数 `SetProcessDpiAwarenessContext` P/Invoke，进程级 per-monitor-v2）  |
| J7 | 离线缓存 | 会话历史本地磁盘缓存，断线可浏览 | 树浏览 `OfflineCache.Save`/`TryBuildFromCache`（Sessions.cs）+ **历史消息回放 `OfflineCache.SaveHistory`/`TryLoadHistory`（2026-08-24 已实现）** | P2 | 🟡 | 树浏览可离线打开（既有）；**历史回放已实现**：`LoadHistoryAsync` 成功时 `SaveHistory(sessionId, events)` 缓存 tail 页原始 JSON 事件（独立文件 `offline-history-{id}.json`，7 天过期），网络不可达（`HttpRequestException`）时回读缓存折叠渲染 + 加 `Fold.OfflineReplay` 离线标记行。**限制**：仅缓存 tail 页，离线向上翻更早历史（loadOlder）不可用（无数据源） |
| J8 | 目录选择零子进程 | 直接 `OpenFileDialog`(Folder) P/Invoke，替代 host 子进程 koffi COM | P1 | 🟡 | **优势**：由 B10 `host.pickDirectory`（loopback 原生 IFileOpenDialog）达成，零 koffi/子进程（待验收证据） |
| J9 | 文件打开零 PowerShell | `Process.Start`/`explorer /select,` 替代 `Invoke-Item` | P1 | 🟡 | **优势**：由 C9 `host.openPath`（host 原生打开）达成，客户端零 PowerShell/Invoke-Item（待验收证据） |
| J10 | 服务自启动引导 + 状态管理 | 检测 host 未启动→选目录→`pnpm dsh web` 后台启动→轮询就绪→自动连接；**查看状态**（gateway 探测 + `netstat` 端口 PID）；**停止服务**（杀进程树 + 端口监听者）；**启动日志/操作日志**（窗口内状态栏实时显示 pnpm 输出 + `▸` 操作记录） | `HarnessLauncher`（探测/定位/启动/轮询/`FindListenerPid`/`Stop`）+ 服务状态面板 + 启动日志状态栏 | P1 | 🟡 | **优势**：Web 客户端需手动起服务；本客户端连接失败即引导启动，且可随时查看/停止/看日志（`HarnessLauncherTests` +3，待验收证据） |
| J11 | 配置本地持久化 | 重启不丢用户选择（服务目录/网关 URL） | `AppSettings`（`%APPDATA%\dsh-wpf-port\settings.json`，`Load`/`Save`） | P1 | 🟡 | **优势**：避免每次启动重选 deepseek-harness 目录（`AppSettingsTests` +3，待验收证据） |
| J12 | 应用图标 | 窗口/任务栏/托盘统一图标 | `Assets/app.ico`（WPF Resource 编译，pack URI 加载：`MainWindow.Icon` + `TrayIcon.LoadAppIcon`） | P1 | 🟢 | 等价（图标已应用；PNG 源图存 `Assets/`）  |

---

## K. 本地化 / i18n（等价覆盖 Web，语言策略见 `06` §5）

> Web 已内建命名空间化双语字典（每个 `ui-*` 包 `locales.ts` 的 `zh`/`en` 成对 + `README.i18n.yaml` 配对校验）。WPF 等价实现，不新增语言种类。

| ID | 功能 | Web 行为 | WPF 实现 | 优先级 | 状态 |
|---|---|---|---|---|---|
| K1 | 命名空间字典 | `locales.ts` 的 `NS` + `Key` 联合 + `zh`/`en` 两字典 | `.resx` 6 语言（zh-CN/en/de/es/fr/ko）按命名空间组织，Key 单一事实源 | P1 | ✅ | 等价（**P2-8 实现**：`Strings.resx` 中英为基，de/es/fr/ko 已补齐翻译；`ResourceManager` 读取；覆盖面随功能扩展持续补全。**2026-08-19 主页面全量补全**：新增 ~90 键 × 6 语言，含 Tab/右键菜单/状态点/Composer/搜索/悬停卡片/服务面板/审批提问/轨迹/设置/动态操作反馈；`LocalizationCompletenessTests` 扩至 ~150 键 × 6 语言完整性门禁） |
| K2 | 语言切换 | UI 内切换 locale，即时生效 | 动态 `CultureInfo` + `ResourceDictionary` 换肤，运行时切换 | P1 | ✅ | 等价（**P2-8 实现**：`Localization` + `{Loc}` MarkupExtension 运行时切换自动刷新 + `AppSettings.Language` 持久化 + 工具栏语言按钮；**2026-08-19 补全后端动态文本**——托盘菜单、代码块按钮、Job 状态、删除/目录对话框、主页面全部写死文本（Tab/右键菜单/Composer/搜索/状态栏/审批提问/设置）均走 `Loc.Get`/`Loc.Format`，`MainViewModel.OnLanguageChanged` 强制刷新 VM 本地化属性；**状态点/颜色触发器改比对稳定枚举**（`SubagentNodeUi.Status`、会话 VM `KindKey`），切语言后颜色不漂移） |
| K3 | 日期/数字/时区格式 | `Intl` 按 locale 格式化 | `CultureInfo.CurrentCulture` 等价 | P2 | ⬜ | 待实现（低优先级） |
| K4 | 字体/排版适配 | CSS 随语言调整 | `FlowDocument` 字体回退 + 双向文本支持 | P2 | 🟡 | **2026-08-24 已实现（字体回退）**：`Localization.UiFont` 按语言返回字体回退串（zh→`Microsoft YaHei UI, 微软雅黑, Segoe UI`、ko→`Malgun Gothic, …`、ja→`Yu Gothic UI, …`、拉丁→`Segoe UI`）；`MainViewModel.ApplyUiFont()` 在语言切换 + MainWindow 构造时应用到窗口根 `FontFamily`。**bidi**：当前 6 语言（zh/en/fr/de/es/ko）无 RTL，WPF `TextBlock`/`TextBox` 内置 Unicode bidi 重排序，天然支持；阿拉伯/希伯来语若加入需显式 `FlowDirection`，当前集不含 |

---

## 协议还原度矩阵

| 功能群 | 依赖 RPC / 下行帧 | 还原度 | 备注  |
|---|---|---|---|
| 会话核心 | `session.create/list/history/fork/rename/search`、`workspace.archiveSession`、`session/projection`、`session/queue`、`session/jobs` | 100% | 纯数据契约  |
| 对话流 | `session/prompt`、`agent/request`、`assistant/message`、`tool/call`、`turn/*`、`step/*` | 100% | 流式帧等价  |
| 交互控件 | `approval/*`、`question/*` 帧、`plan`、`goal`、`permissions` 投影、`commands/change` | 100% | 帧驱动  |
| 模型/设置 | `session.models`、`settings.*`、`credentials.*`、`agentPreset.*` | 100% | 含 revision 冲突  |
| 原生能力 | `host.describe`、`host.pickDirectory`、`host.listDirectory`、`host.createDirectory`、`host.openPath` | 100% + 可本地化 | WPF 可直调 Windows API 或走 host，行为一致  |
| 双 WebSocket 下行流 | `WS /api/events.mux` + `WS /api/events.host` | 必须精确复刻 | 世代重建/重连/背压为最高风险  |

---

## 依赖图

```
A(连接/运行时) ──► B(会话/Workspace) ──► C(对话核心)
                                      └─► F(子代理) ──► H5(trajectory)
A ──► D(实时协作: approval/question/plan)
A ──► E(模型/配置: settings/credentials/llm)
C ──► H(可视化: deliverables/todo/queue/feedback)
I(本地查看器) 依赖 core/session 磁盘格式契约（独立于 A 在线流）
J(原生增强) 叠加于 B/C/D/E/F 之上
```

## 验收分层

- **Phase 0（A1–A7 + B1–B4 + C1–C4 + C7 + D1）**：最小可用——连接 host、看会话列表、发消息、看流式工具树、答审批。
- **Phase 1（B5–B10 + C5–C6,C9,C12,C17 + D2–D4 + E1–E3,E8,E10–E11 + F1–F6 + H3,H6,H7 + J2,J8,J9）**：完整体验。
- **Phase 2（C13–C16 + D5 + E4–E7,E9 + G + H1–H2,H4–H5 + I4–I6 + J1,J3–J7）**：增强与高级。

## 风险标注

1. **双 WebSocket 重连世代算法**（A2/A4）为最高风险——需等价 generation 计数 + 帧路由 + 状态清空。
2. **投影 higher-seq-wins**（A6）与 `settings-conflict` CAS（E8）必须逐字节对齐 host 语义，否则多窗/多端不一致。
3. **磁盘格式契约**（I 域）随 dsh 升级可能变；查看器须与 `core/session/src` 同步。
4. **审批/问题回复路径**（D1/D2）走 `POST /api/respond` 而非 unary，易错；必须 echo 稳定 rpcId。

---

## 状态图例

功能项"状态"列使用以下标记（自动化检查 + 人工验收）：

| 标记 | 含义 |
|---|---|
| ⬜ 未开始 | 已规划，尚未编码 |
| 🟡 开发中 | 编码进行中 |
| 🟠 待验收 | 开发自测通过，待按 `04-acceptance.md` 门禁验收 |
| 🟢 已验收 | 门禁全过 |
| 🔴 受阻 | 依赖缺失（含 blocked-by-host：上游未提供所需 RPC） |

> 状态变更前须确认 `05-contract-sync.md` 所列 host RPC 契约未漂移；若已变更须先按该文件回归。
> 对外可见的完成项与限制摘要见主 `README.md` 的 Known limitations 一节。

---

*文档生成日期：2026-08-15*
*单一事实源：本文件；子模块见 `03-local-storage-viewer.md`，验收门禁见 `04-acceptance.md`，契约回归见 `05-contract-sync.md`。*
