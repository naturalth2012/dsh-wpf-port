# 04 · 验收门禁（Acceptance Gates）

> 角色：**质量门禁**。本文件定义每条功能项的可量化验收门槛，以及"不达标时的修改策略"。
> 配套：功能项清单在 `02-feature-set.md`（单一事实源）；本文是判断"能否进入 🟢 已验收"的判据。
> 维护：任何门禁改动须同步更新本文件并在提交说明中注明。

---

## 1. 门禁总则（适用所有域）

每条功能项进入 🟢 已验收前，必须全部满足：

| 门禁 | 判据 | 机检方式 |
|---|---|---|
| **G1 契约一致** | 实现的 RPC 方法名 / 帧名 / 字段与本文件或 `02` 标注的 host 契约一致 | 对照 `05-contract-sync.md` 快照；host 变更须先回归 |
| **G2 验收列全过** | `02` 该行"验收"列描述的每条可观察行为均成立 | 必须在 `evidence/<feature-id>/` 有可复现证据（截图/录屏/日志/运行说明）；无证据不得标 🟢 |
| **G3 不退化** | 未破坏已验收功能项（状态回退须同步 `02` 状态列） | 回归已 🟢 项 |
| **G4 原生对比诚实** | "原生对比"列标注的 `优势`/`差异`/`等价` 在 WPF 实测成立 | 实测记录 |
| **G5 状态同步** | 状态流转已更新 `02` 状态列；对外可见变更记入 `CHANGELOG.md` | diff 审查 |

**状态升级强制流程（防雏形标 🟢）**：
1. 功能项从 🟡 → 🟢 前，必须**逐条核对 `02` 该行"验收"列**，确认每条可观察行为真实达成（不是"能跑"而是"验收语义达成"）。
2. 必须在本文件 §3 的 `evidence/<feature-id>/` 归档可复现证据（截图/录屏/日志/运行说明），**无证据不得标 🟢**。
3. 若实现仅是"雏形/占位"（如只显示而不满足验收语义），**只能标 🟡（进行中）**，不得标 🟢。
4. 升级动作必须留存验收记录（日期/证据/执行人）。

**不达标修改策略**：
- 仅 G2 单条失败 → 改实现，回到 🟡，不回退其他项。
- **虚标（雏形标 🟢）→ 回标 🟡，并在 `06` 记录"虚标纠正"**（承认实现不足，不掩盖）。
- G1 失败（契约漂移）→ 触发 `05` 回归，相关 `02` 行须回标 ⬜，禁止带漂移合并。
- G3 退化 → 该改动作废，回到上一稳定态。
- G4 虚标 → 该"优势/差异"断言删除或补实测，视为文档缺陷（记 `06`）。

---

## 2. 分域门禁（按优先级分层）

### 域 A — 连接与运行时（P0，G1 权重最高）
- **A2 双 WebSocket 流**：必须实测收到 `host/session-added` 与 `session/event` 两路并正确路由到所有者/全局；断线后 generation 自增（A4）。
- **A4 重连世代**：模拟断网 → 重连后 `pendingInteraction` / `jobsBySession` 为空，无幽灵 pending。
- **A6 投影仓库**：同一 key 两次 `session/projection` 帧（seq 升序）必须 higher-seq-wins；乱序到达须丢弃低 seq。
- **A7 错误解析**：注入 `agent-busy` / `settings-conflict` 等业务错误码，WPF 须分支处理而非抛未捕获异常。

### 域 B — 会话与 Workspace（P0）
- **B1 Workspace 树**：`workspace.list` 种子 + `host/workspace-*` 三类帧增量 upsert，无重复/丢失节点。
- **B4 离线浏览**：断线后可浏览已加载 history 尾页（J7 缓存支撑时）。
- **B10 目录选择**：走 `host.pickDirectory` 或 P/Invoke `OpenFileDialog`(Folder)；**禁止**依赖 koffi/COM 子进程（对比 `01` 原生优势）。

### 域 C — 对话核心（P0）
- **C1/C2 流式**：assistant 增量 chunk 实时渲染，think 段与内容段分离；断流可续。
- **C3 工具调用树**：`tool/call` + `step/*` 帧构建可折叠树，状态（running/done/error）正确着色。
- **C7 图像摄入**：选中本地图片 → 作为 multipart 进入 `session.prompt` 载荷，host 回显成功。

### 域 D — 实时协作（P0/P1，回复路径高危）
- **D1/D2 回复**：审批/问题回复走 `POST /api/respond` 且 **echo 稳定 rpcId**；错误回退"无法发送"。
- **D3 Plan 芯片**：`plan` 投影渲染 chips；批准/拒绝 → `commands/change` 镜像 Web。

### 域 E — 模型与配置（P1）
- **E1 模型选择**：`session.models` 列表渲染，选择写回。
- **E8 冲突 CAS**：`settings.update`/`settings.mutate`/`settings.replace` 带 `expectedRevision`；冲突时拉取新 revision 重放，不静默覆盖。
- **E10 写只 API Key**：`credentials.set` 仅写字段不回读；UI 显示掩码。

### 域 F — 子代理（P1）
- **F1 目录树**：只读会话的子代理树渲染，禁止写操作入口。
- **F5 查看 transcript**：打开子代理 transcript 视图，帧流式等价。

### 域 G — 后台作业（P1）
- **G 全部**：`session/jobs` 全量快照渲染；新 job 帧增量追加；完成态正确。

### 域 H — 高级可视化（P1/P2）
- **H3 Todo/Queue 面板**：`session/queue` 渲染 todo 列表与依赖序。
- **H6/H7 消息反馈 / Deliverables**：反馈按钮 → `agent/feedback`；产物文件行点击 → `host.openPath` 或导出。

### 域 I — 本地存储查看器（见 `03`）
- 门禁以 `03-local-storage-viewer.md` 的"兼容契约"为准；磁盘格式须与 `core/session/src` 同步，禁止读旧格式崩溃。

### 域 J — WPF 原生增强（Web 无）
- **J6 DPI**：`app.manifest` per-monitor-v2；高 DPI 屏无模糊。
- **J8/J9 零子进程**：目录选择 / 文件打开不 spawn PowerShell 或 koffi 子进程（Process Monitor 验证）。
- **J2 多窗**：多 `Window` 共享单连接，会话镜像独立不串数据。

---

## 3. 验收证据归档

每个 🟢 项须留存验收记录：功能 ID、验收日期、证据（截图/录屏/日志路径）、执行人。验收证据属本地过程性产物，**不随公开仓库发布**（`evidence/` 已被 `.gitignore` 排除）。

---

*本文件是质量门禁判据；功能项定义见 `02-feature-set.md`；契约回归触发见 `05-contract-sync.md`。*
