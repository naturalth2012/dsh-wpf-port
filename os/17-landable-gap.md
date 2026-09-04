# 17 — 可落地缺口清单（按可实现性分类）

> 配套：`02-feature-set.md`（功能单一事实源）、主 `README.md`（Known limitations）。
> 本文档把 WPF 客户端相对 Web 客户端的全部缺口，按「可实现性」重新归类，回答「哪些能落地、哪些卡住」，作为排期与对外「已知限制」说明的依据。

## 0. 结论速览

- 缺口分三类：**纯 UI/本地逻辑缺口**（可直接落地）、**契约缺失缺口**（blocked-by-host）、**已实现待验收**（补证据）。
- 真正「结构上无法在前端落地」的只有 3 项（H6 / G3 / C12）；**H7 契约已就绪（`session.updateQueue`）仅客户端未接线，可落地**；其余要么可做、要么已实装待验收（P2-4/5/6/7）。
- 性价比最高的动作是「补验收证据」：零代码即可把一批 🟠 推成 🟢，立刻提升完成度账面。

## 1. 判断框架

| 缺口类型 | 卡点 | 能否落地 |
|---|---|---|
| 纯 UI / 本地逻辑缺口 | 数据已经在帧 / 本地，只是没写呈现 | ✅ 可直接实现 |
| 契约缺失缺口 | host 根本不暴露对应 RPC / 事件 / 投影 | ❌ 前端无法落地，需等 host 或回源 |
| 已实现待验收 | 代码已接，缺截图 / 门禁证据 | ⚠️ 不是「落地」问题，是「证明」问题 |

## 2. 可直接落地的缺口（纯前端，无 host 阻塞）

这些是当前最该投入、且确定能做完的。

| 优先级 | 功能项 | 现状 | 为什么可实现 |
|---|---|---|---|
| P1 | H2 时间轴交互（拖拽聚焦 / 滚轮缩放） | `Canvas` 时间轴 + 拖拽 Ellipse hit-test + ToolTip + 时间刻度 + hover 高亮 + 图例已实装（`Dsh.Wpf/Controls/TrajectoryTimeline.cs`，27d342a）；仅缺拖拽聚焦和滚轮缩放两层交互 | `Canvas` + `OnMouseMove`/`OnRender` 已支持扩展；纯前端交互，无契约依赖 |
| P1 | I 域本地查看器 **L2 融合 / L3 渲染 / L4 检索导出** | **L0 已交付；L1 解码已实现**（`SessionPathResolver`/`ZstdReader`/`ChunkRowExpander` 已实装，待补单元测试）；**L2 融合进主客户端、L3–L4 待做** | 独立模块读本地 `.jsonl`，不依赖 host 在线流；契约源在本地 `core/session`，可精确对齐 |
| P1 | C15 重试状态行（倒计时）/ C16 终端失败状态 / C13 上下文披露 | **C13 已实现**（ContextTemplate + `context` DataTrigger，2026-08-24）；C16 安全核心已实现（AUTH 不回显）；C15 静态文案已有、**倒计时未做**（delay 仅数百 ms 价值有限，暂缓） | 数据层（SessionFold）已生成对应行；C13 仅缺折叠交互（当前整块披露）；C15 倒计时需 SessionFold 暴露 delay + VM 定时器，暂缓 |
| P2 | J1 托盘 balloon（作业完成 / 审批触发） | 仅连接 / 重连触发 | 纯本地 `ShowBalloonTip`，接 `Jobs` / `PendingApproval` 事件即可 |
| P2 | J7 会话历史离线回放 | **已实现（2026-08-24）**：`OfflineCache.SaveHistory`/`TryLoadHistory` + `LoadHistoryAsync` 网络不可达时回读缓存 | 缓存 tail 页原始 JSON 事件（独立文件，7 天过期）；离线时显示缓存内容 + `Fold.OfflineReplay` 标记。**限制**：仅缓存 tail 页，离线向上翻更早历史不可用 |
| P2 | G2 作业详情 tooltip | 状态色已做，详情缺 | 纯 `DataTemplate` tooltip，数据在 `JobInfo` 里 |
| P3 | K3 日期 / 数字格式、K4 字体 / bidi 排版 | 未实现 | 纯本地 `CultureInfo` / 字体回退，无契约依赖 |

## 3. 被 host 阻塞、当前无法落地的缺口

这几项不是前端努力就能做，必须 host 先动（或回源确认），否则只能保持只读 / 降级。

| 功能项 | 具体阻塞 | 可能的出路 |
|---|---|---|
| H6 TodoDock 编辑 | `Dsh.Contract` 无 `todo.*` 写 RPC，host 只推 `todos` 全量快照 | 等上游暴露写 RPC；或用户本地 checkout 自行 patch host |
| H7 QueueDock 操作（编辑 / 删除 / 严格 steer） | **契约已就绪**：`session.updateQueue`（`RpcMethodMap.cs:22`）支持 `action: edit|remove|steer`；`QueuedInboxItem.Id` 即 `itemId`。**客户端未接线**（`SyncQueue` 仅只读展示，零调用 `session.updateQueue`） | **可落地**：补 DTO + 服务方法 + VM 命令 + QueueDock 行内编辑/删除/steer 按钮 |
| G3 作业 kill | 无 `job.cancel` RPC | 同上 |
| C12 命令目录事件订阅 | 无 `skill.changed` / `preset.changed` 事件帧 | 只能维持「软失效降级」，实时刷新需等帧 |
| P2-4 消息反馈 DTO | 已实装：`messageFeedback.list`/`put`/`delete` 三 RPC（`Dsh.Contract/Methods/MessageFeedbackModels.cs`）；UI 反馈按钮已接线，待验收证据 | 不再阻塞；归入"已实现待验收" |
| P2-5 产物行 DTO | 已实装：`SessionFold.Deliverables` 按 render intent 提取 `locations[].path` + turnTail chip + `OpenDeliverableCommand`→`host.openPath`；待验收证据 | 不再阻塞 |
| P2-6 轨迹 DTO | 已实装：`SessionFold.Trajectory`/`TrajectoryStep` 事件顺序回放 + `TrajectoryTimeline` Canvas 渲染（已升级为富 UI，含 hit-test + ToolTip + 时间刻度）；待验收证据 | 不再阻塞 |
| P2-7 统计条数据源 | 已实装：`SessionStatsProjection`/`TokenUsage` 投影键已注册；`ProjectionStore.Get<T>` 加 camelCase options（f06b5f4）；`SessionStatsFormatter.FormatRich` 输出 ring 分数 + detail chips（133c536）；待验收证据 | 不再阻塞 |

**关键区分**：H6 / G3 / C12 是「host 压根没提供接口」，属**结构性阻塞**；**H7 是契约已就绪但客户端未接线**（`session.updateQueue` 已在 `RpcMethodMap`），非阻塞、可落地。P2-4 / 5 / 6 / 7 已实装（见上表注），不再阻塞。

## 4. 已实现待验收（不是「落地」，是「证明」）

下面这些功能**代码已写完并接线**，但卡在 🟠「待验收」——缺 GUI 截图证据和 `04-acceptance.md` 门禁验收。它们不该被当成「未落地」，而是「已落地、未验证」。

| 域 | 待验收项 |
|---|---|
| B 会话/Workspace | B1 增量 upsert、B2 会话悬停卡、B7 Fork、B9 搜索 debounce、B11 拖拽排序、B12 复制路径 |
| C 对话核心 | C5 busyEnter 偏好 |
| D 实时协作 | D3 Plan 芯片、D5 权限芯片 |
| H 高级可视化 | H2 基础时间轴、H3 产物 turnTail、H4 内联文件提及、H5 消息反馈 |
| C/E 其他 | C17 富统计条、E11 凭据徽标点 |

**落地动作是「补证据」而非「写代码」**：补 10 来张截图存入本地 `evidence/`（不随公开仓库发布），即可把一大批 🟠 推成 🟢。

## 5. 落地优先级建议

按「价值 × 可实现性」排序：

1. **先做「补验收证据」**（§4 的 🟠 项）——零代码、零风险，立刻提升完成度；
2. **再做「纯前端 P1 缺口」**：H2 拖拽/缩放（已完成 eba1161）→ **C13 上下文披露（已完成 2026-08-24）** → I 域 L1–L6 解码管线 → C15 倒计时（暂缓）；做完后 H 域和 I 域从「只读降级」变「完整」；
3. ~~**回源核验 P2-4 / 5 / 6 / 7 契约**~~ **已实装完成**（f06b5f4 / 133c536 / 6366e56 / 27d342a 等提交），归入"待验收"；
4. **接线 H7 `session.updateQueue`**（契约已就绪，QueueDock 编辑/删除/steer 可落地）；**结构性阻塞项（H6 / G3 / C12）**：明确标注「blocked-by-host」，作为对外 README 的「已知限制」，不投入前端精力硬做；
5. **P2 / P3 增强（J1 / J7 历史回放 / K3 / K4 / G2）**：最后做，属打磨。

## 6. 状态维护

- 每完成一批：更新 `02-feature-set.md` 对应行状态；对外可见的变化同步主 `README.md` 的 Known limitations 与 `CHANGELOG.md`。
- 状态链遵循 `02-feature-set.md`：`⬜ 未开始 → 🟡 开发中 → 🟠 待验收 → 🟢 已验收`。

---

*文档生成日期：2026-08-23*
*单一事实源：`02-feature-set.md`。*
