# 05 · 契约同步规范（Contract Sync）

> 角色：**专项规范**（强制性检查位）。本文件规定：当 host `apiproxy` 契约变更时，哪些 `02-feature-set.md` 功能项必须回归，以及回归动作。
> 目的：防止 Web 端契约演进后 WPF 客户端" silently 漂移"——这是本项目最高风险的一类缺陷（双 WebSocket 世代、投影 CAS、磁盘格式三大契约风险）。

---

## 1. 契约来源与快照

**权威来源**：`packages/host/apiproxy/src/api/*.ts` 的 RPC 方法、`/api/events.mux` 与 `/api/events.host` 帧名、`session/projection` / `settings` revision 语义、`core/session` 磁盘格式。

### WebSocket 下行流契约（回源 `client/connection/src/websocket-downlink.ts` + `api-path.ts`，实测 426 确认）

- **载体**：WebSocket（`ws` 库 `WebSocketServer({ noServer:true })`），**非 SSE**。路径 `/api/events.mux` + `/api/events.host`（`MUX_EVENTS_PATH`/`HOST_EVENTS_PATH`）。
- **帧格式**：每个 WS text message 是 `JSON.stringify(ServerRequest)`，即 `{ type:"server-request", rpcId, method, payload }`。
- **关键**：`method = payload.type`（帧 type）；`payload` 是完整帧体，**含 `type` 判别字段**。
- **单向**：下行 WS 只出不进；客户端一旦发消息，host 关闭 1008 `'downlink only'`。上游流量始终走 HTTP。
- **信任栅栏**：非可信 upgrade 返回 `403 Forbidden`（`rejectWebSocketUpgrade`）。
- **容错**：流级错误发 `{ type:'stream/error', error:{code:'internal',...} }` 后关闭。
- ⚠️ 注意：`apiproxy/src/fetch/*` 的 SSE（`sseResponse`/`readSse`）是 **in-process SSE carrier**（进程内测试用），**不是**浏览器/WPF 客户端用的下行流。

**本文件维护一份契约快照指纹**（见第 4 节）。每次 host 变更后，比对指纹，命中则触发对应回归集。

---

## 2. 触发—回归映射表

| 契约变更点 | 命中功能项 | 回归动作 | 禁止行为 |
|---|---|---|---|
| `session.create/list/history/fork/rename` 签名变 | B1–B4, B5–B7 | 重跑 B 域门禁（见 `04`） | 禁止静默兼容旧字段 |
| `workspace.archiveSession` 签名变 | B6 | 重跑 B 域门禁 | 禁止改回 session 域 |
| `session.prompt` / `agent/request` / `assistant/message` / `tool/call` / `turn/*` / `step/*` 帧变 | C1–C7, C9, C12–C17 | 重跑 C 域流式与工具树 | 禁止假定字段顺序 |
| `/api/events.mux` 或 `/api/events.host` 帧名/路由变 | A2, A4 | 重测双 WebSocket 双路路由 + generation | 禁止复用旧帧名常量 |
| `session/projection` seq 语义变 | A6, D3, E 域投影 | 重测 higher-seq-wins | 禁止低 seq 覆盖高 seq |
| `approval/*` / `question/*` 帧 / `plan` / `goal` / `permissions` 投影变 | D1–D5 | 重测回复走 `POST /api/respond` + rpcId echo | 禁止改回 unary 路径 |
| `session.models` / `settings.*` / `credentials.*` / `agentPreset.*` 变 | E1–E11 | 重测 CAS、写只 Key、自定义 provider | 禁止忽略 `settings-conflict` |
| `host.pickDirectory` / `host.listDirectory` / `host.createDirectory` / `host.openPath` / `session.export` 变 | B10, J8, J9, I 域导出 | 重测原生 API 路径 | 禁止回退子进程方案 |
| `session/jobs` 帧变 | G | 重测全量快照 + 增量 | 禁止假定 job 顺序 |
| `core/session` 磁盘格式变（SCHEMA_VERSION / SESSION_FORMAT_VERSION） | I1–I6（见 `03`） | 重测查看器兼容 + 旧格式拒绝 | 禁止读旧格式崩溃 |
| `credentials.*` 落地变 | E8, J5 | 重测 Credential Manager 集成 | 禁止明文落盘 |

---

## 3. 回归流程（接到 host 变更时）

```
1. 比对第 4 节指纹 → 命中映射表
2. 将命中功能项在 02 状态列回标 ⬜（未开始）或 🟡（需重验）
3. 执行 04 对应域门禁
4. 全过后回标 🟢，并更新 `02` 状态列
5. 若 host 变更导致 WPF 无法实现，记 01 风险 + 06 决策索引
```

**硬性规则**：host 契约变更未在本文件记录回归结论前，**禁止**将 Phase 相关功能项标 🟢。

---

## 4. 契约快照指纹（更新于每次 host 同步）

> 格式：`文件:符号@语义摘要`。host 契约同步后应刷新此节。

```
apiproxy/sessions.ts     : session.create/list/history/fork/rename/search/models/selectModel/prompt/attachment/updateQueue/cancel @ 2026-08-15 baseline
apiproxy/workspace.ts    : workspace.list/create/rename/delete/insertBefore/insertSessionBefore/archiveSession @ 2026-08-15 baseline
apiproxy/host.ts         : pickDirectory/listDirectory/createDirectory/openPath @ 2026-08-15 baseline
apiproxy/settings.ts     : settings.describe/openDocument/update/replace/mutate(expectedRevision CAS) @ 2026-08-15 baseline
apiproxy/events.ts       : WS /api/events.mux + WS /api/events.host（WebSocket downlink）帧路由 @ 2026-08-15 baseline
apiproxy/approvals.ts    : approval.* / POST /api/respond @ 2026-08-15 baseline
apiproxy/questions.ts    : question/requested + question/resolved 帧 / POST /api/respond @ 2026-08-15 baseline
apiproxy/goals.ts        : goal / plan 投影 @ 2026-08-15 baseline
apiproxy/jobs.ts         : session/jobs 全量快照 @ 2026-08-15 baseline
apiproxy/skills.ts       : skills.list/get/load @ 2026-08-15 baseline
apiproxy/credentials.ts  : credentials.get/set(写只) @ 2026-08-15 baseline
apiproxy/agent-presets.ts: agentPreset.list/get @ 2026-08-15 baseline
apiproxy/subagents.ts    : subagent.list/history/prompt/interrupt（map 键为单数 subagent.*）@ 2026-08-15 baseline
apiproxy/downloads.ts    : downloads.* @ 2026-08-15 baseline
apiproxy/llm.ts          : llm.* @ 2026-08-15 baseline
apiproxy/session-search.ts: session.search @ 2026-08-15 baseline
core/session             : SCHEMA_VERSION / SESSION_FORMAT_VERSION @ 2026-08-15 baseline
```

---

*本文件是契约回归触发源；功能项见 `02-feature-set.md`；门禁见 `04-acceptance.md`。*
