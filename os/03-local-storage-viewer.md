# 03 — 功能集：本地存储数据查看器（Local Storage Viewer）

> 配套：`README.md`（地图）、`02-feature-set.md`（主客户端功能集，域 I）。
> 本文件聚焦一个**独立于在线 agent 客户端**的模块：**离线查看本地持久化的 dsh 会话数据**。
>
> ## 📌 当前实现状态（2026-09-02 核对，以此为准）
>
> `Dsh.Viewer` 项目**已交付并可运行**，实际完成度：
>
> | 阶段 | 内容 | 状态 |
> |---|---|---|
> | **L0 基线** | 目录扫描、JSONL 逐行解析、消息行渲染 | ✅ 已交付 |
> | **L1 解码** | `SessionPathResolver`（`--<encoded-cwd>--` 扫描 + `~XXXX` 反转义）、`ZstdReader`（`.zstd` 透明解压，plain 直读）、`ChunkRowExpander`（chunk 行还原），已接入 `LoadSelected` | 🟡 代码已交付；**待补 L1 单元测试** |
> | **L2 融入主客户端** | 作为主客户端"会话档案"Region Tab + 与在线会话联动 | 🟡 部分：workspace→session 树浏览已具备（独立窗口内）；**融合进主客户端与在线联动待做** |
> | **L3–L4** | 增强渲染（Markdown/代码高亮/工具树/图片）、检索与导出（跨会话搜索/HTML/CSV/token 统计） | ⬜ 待做 |
>
> 下文 §2 是**目标能力盘点**（用于界定模块边界），**其中 L2 融合部分与 L3–L4 尚未实现**。
> ⚠️ **代码来源**：本模块的存储解码与渲染**基于本仓库 `Dsh.App.SessionFold` 自研**（与在线客户端共用同一折叠层），未引入任何外部代码。架构依据见 `07` §1。

## 1. 模块定位与价值

| 项 | 说明 |
|---|---|
| **是什么** | 直接读取磁盘 `~/.dsh/sessions/`，离线解析/浏览/检索/统计/导出会话日志，**不依赖运行中的 host / 模型 / 网络**。 |
| **与在线客户端区别** | 在线客户端经 RPC 实时驱动 agent；本模块只做**只读考古**——看历史、找问题、审计、导出报告。 |
| **为何需要** | Web 客户端只有"当前连接工作区"视图，无法便捷浏览跨项目/跨机器历史日志；本模块填补"运维/复盘/合规"空白。 |
| **与 Web 端对应** | Web 有 `session.export`（ZIP）和 `session.history`（分页），但**不提供跨会话全局扫描、本地文件级检索、离线统计**——本模块是超集。 |

> 关键决策：WPF 查看器**采用本仓库 `Dsh.App.SessionFold` 作为共享折叠层自研**，复用在线客户端已验证的帧解析与折叠逻辑，仅补"磁盘格式解码 + 目录扫描 + 离线渲染"三部分。架构依据见 `07` §1。

## 1.1 技术选型（ADR）

| 项 | 决策 |
|---|---|
| 问题 | 离线查看器（域 I / `03`）如何构建？ |
| 决策 | **基于本仓库 `Dsh.App.SessionFold` 共享折叠层自研**，不引入外部代码。 |
| 折叠逻辑 | **直接复用 `Dsh.App.SessionFold`**（在线/离线同一套，零重复） |
| 存储解码 | `SessionPathResolver` 等，**须与 `packages/core/session/src` 逐字符一致** |

## 2. 目标能力盘点

### 2.1 存储格式解码
| 文件 | 职责 | 等价 Web 端实现 |
|---|---|---|
| `SessionPathResolver.cs` | 扫描 `<root>/--<encoded-cwd>--/<encoded-sessionId>/session.jsonl[.zstd]`；`DecodeSegment` 反转 `~XXXX` 转义（对齐 `encodeSegment`/`projectKey`） | `session-persistence-jsonl/src/format.ts` |
| `ZstdReader.cs` | 对 `.zstd` 透明 `ZstdNet.DecompressionStream`；plain 直读 UTF-8 | JSONL 后端 zstd/none 模式 |
| `ChunkRowExpander.cs` | 把打包 `text-chunks`/`reasoning-chunks`/`tool-call-chunks` 还原成原始 `assistant/chunk`（忠实逆运算 `chunk-rows.ts` 的 `expandRow`） | `core/session/src/chunk-rows.ts` |
| `SessionLogReader.cs` | 逐行解析 JSONL：首行 `SessionHeader`，其余 `SessionEvent`；展开 chunk 行；记录 `UnknownButRequired` | `decodeStorageRecord` 读路径 |

### 2.2 投影与展示
| 文件 | 职责 |
|---|---|
| `SurfaceProjector.cs` | 投影为有序 surface 消息（user/message、assistant/message、tool/call、tool/result），折叠 chunk delta 为气泡，提取 usage、`<reasoning>`、工具参数 JSON、错误标记 |
| `StatsComputer.cs` | 统计 event/turn/step 数、user/assistant/tool 消息数、tool 错误数、`UnknownRequiredTypes`、会话时长 |
| `TranscriptExporter.cs` | 导出 Markdown 转录（含 header 元信息、角色标识、usage）与原始 JSONL |

### 2.3 查看器 UI 维度
- 三栏：左 `TreeView`（cwd 项目分组→会话列表）、中消息流（SurfaceMessage 气泡 + 可选 RawEvents DataGrid）、右 Raw JSON 详情。
- 工具栏：根目录浏览、刷新、搜索（文本/工具名/角色过滤）、Raw events 开关。
- 菜单：导出 Markdown / raw JSONL。
- 统计条：events/turns/steps/tools/errors 计数。
- `KnownEventTypes.cs`：与 `core/session/src/known-event-types.ts` 对齐（约 50 类型），含 chunk-row 与 surface 识别。

### 2.4 已知边界（功能集中需标注）
1. 仅 surface 投影：`tool-call-delta`/`block`/`usage`/`finish` 等 chunk 类型在简单投影中被忽略。
2. `UnknownButRequired` 暴露但不阻断（符合"model 可见 ⟺ 可日志重建"重建安全原则）。
3. 单文件会话：每次打开一个 `session.jsonl[.zstd]`，无跨会话全局检索/聚合。
4. 无 diff/代码高亮：文本等宽原样显示。
5. 统计不含 token 明细：未解析 `usage` 的 prompt/completion token 总量。

## 3. 功能集规划（复用 + 增强）

**P0=必含（对齐现有）；P1=增强；P2=可选。**

### 3.1 存储浏览（P0）
- [ ] 根目录选择：默认 `~/.dsh/sessions/`（`$env:DSH_HOME` 优先），支持 `FolderBrowserDialog` 或命令行参数。
- [ ] 项目/会话树：按 cwd 解码分组，下列会话；显示压缩标记、创建时间、事件数、turn/step/tool 计数。
- [ ] 编码正确性：`DecodeSegment` 必须正确处理 `.`/`..` 与 `~XXXX` 转义，与 `encodeSegment` 双向一致（**兼容性契约，不能漂移**）。

### 3.2 会话解码（P0）
- [ ] JSONL/zstd 双格式透明解压；plain 直读。
- [ ] chunk 行展开：忠实逆运算 `expandRow`，与 `decodeStorageRecord` 产出一致。
- [ ] Header 元数据：version（SESSION_FORMAT_VERSION）、id、createdAt、cwd、parentSession、seedLength、origin、delegationDepth、agentPreset。
- [ ] Unknown 类型报告：列出 `UnknownButRequired`，不静默丢弃。

### 3.3 消息投影与渲染（P0→P1）
- [ ] surface 投影（P0）：user/assistant/tool 气泡，role 着色，turn/step/时间/usage 元信息。
- [ ] Markdown 渲染（P1）：`Markdig`/`Westwind.Markdown` 渲染 assistant 文本与 `<reasoning>`。
- [ ] 代码高亮（P1）：`AvalonEdit`/`RichTextBox` 语法高亮工具参数 JSON 与代码块。
- [ ] 工具调用树（P1）：`tool/call`→`tool/result` 经 `callId` 关联，按 step 折叠（对齐 `ui-tool` 的 ToolCallTree）。
- [ ] 图片渲染（P1）：`user/message`/`tool/result` 的 `image` 块渲染为缩略图。

### 3.4 检索与过滤（P0→P1）
- [ ] 会话内搜索（P0）：按文本/工具名/角色实时过滤（CollectionView `Filter`）。
- [ ] 跨会话全局检索（P1）：扫描所有会话返回命中 (session, message)。
- [ ] 按时间/类型过滤（P1）：日期范围、事件类型（如只看 tool/error）。

### 3.5 统计与洞察（P0→P2）
- [ ] 基础统计（P0）：event/turn/step/tool/error 计数 + 时长。
- [ ] Token 用量聚合（P2）：解析 `assistant/message.usage`，汇总 prompt/completion token 与成本。
- [ ] 工具调用分布（P2）：按 tool name 聚合次数/错误率，柱状图。
- [ ] 轨迹视图（P2）：对齐 Web `ui-trajectory`。

### 3.6 导出（P0→P1）
- [ ] Markdown 导出（P0）：含 header 元信息 + surface 消息。
- [ ] Raw JSONL 导出（P0）：忠实回写事件。
- [ ] HTML 导出（P1）：带样式/高亮的独立报告。
- [ ] CSV 导出（P1）：工具调用明细（name, args, error, duration）。
- [ ] 批量导出（P1）：多选会话打包 ZIP。

### 3.7 与 WPF 主客户端集成（P1）
- [ ] 内嵌面板：作为主客户端 `Region` 的 Tab（"会话档案"），复用同一 `WpfApiClient` 时可从 host 拉取"当前运行会话"实时叠加。
- [ ] 跳转联动：查看器点击会话 → 主客户端打开该会话（若 host 在线）；离线则仅查看。
- [ ] 文件关联：`.dshsession`/导出 `.md` 双击用本程序打开。

## 4. 与 Web 端"离线查看"能力对比

| 能力 | Web 客户端 | WPF Local Viewer |
|---|---|---|
| 查看当前连接会话 | ✅（`session.history`） | ✅（直接读盘，更快） |
| 跨项目/跨机器扫描 | ❌（仅当前 host 工作区） | ✅（`Scan` 全局） |
| 离线查看（无 host） | ❌ | ✅ |
| 本地文件级全文检索 | ❌ | ✅（P1 跨会话） |
| 原生目录/文件打开 | 经 host `pickDirectory`/`openPath` | ✅（直接 `FolderBrowserDialog`/`explorer`） |
| 导出格式 | ZIP（host 侧） | Markdown / JSONL / HTML / CSV（P1） |
| 统计深读 | 投影 `tokenUsage` 等 | 本地聚合 + token 成本（P2） |

## 5. 实现建议与风险

### 实现策略（与 `07` §1 一致）
- **基于本仓库 `Dsh.App.SessionFold`** 作为统一折叠层：离线 JSONL 逐行 `FrameConverter.ReadSessionEvent` → `fold.Fold(jsonElement)`，复用与在线客户端完全一致的帧解析与折叠逻辑。
- 仅新增三部分：① 磁盘格式解码（`SessionPathResolver`/`ZstdReader`/`ChunkRowExpander`，须与 `packages/core/session/src` 逐字符一致）；② 目录扫描；③ 离线渲染 UI（`Dsh.Viewer`）。
- UI 层基于 `CommunityToolkit.Mvvm`，融入主客户端或保持独立可执行均可。

### 兼容性契约（最高优先级）
- `SessionPathResolver.DecodeSegment` 与 `ChunkRowExpander` 必须与 `packages/core/session/src` 的 `encodeSegment`/`expandRow` **逐字符一致**。dsh 后端**拒绝旧磁盘格式**，Viewer 应随仓库版本锁定。
- `KnownEventTypes` 需随 `known-event-types.ts` 同步；新增非 ignorable 类型必须纳入，否则 `UnknownButRequired` 误报。

### 依赖
- `ZstdNet`（zstd 解压）；`CommunityToolkit.Mvvm`（MVVM）——已用。
- P1 渲染：`Markdig`、`AvalonEdit`。

## 6. 分期（本功能集内部）

| 阶段 | 内容 | 状态 |
|---|---|---|
| **L0 基线** | 基于 `SessionFold` 自研：目录扫描 + 逐行 `FrameConverter`/`Fold` 渲染 `.jsonl`/`.json`，离线浏览消息行（无磁盘格式解码亦可跑通已投影 JSON）。 | ✅ 已交付（2026-08-19，`Dsh.Viewer` 最小可运行骨架） |
| **L1 磁盘格式解码** | 实现 `SessionPathResolver`/`ZstdReader`/`ChunkRowExpander`，与 `packages/core/session/src` 逐字符一致，处理 zstd/分块行。 | 🟡 **已实现（2026-08-24）**：`SessionPathResolver.Scan`（`--<encoded-cwd>--/<encoded-sessionId>/` 扫描 + `DecodeSegment` 反转 `~XXXX`）+ `ZstdReader`（ZstdNet `DecompressionStream` 透明解压，plain UTF-8 直读）+ `ChunkRowExpander.Decode`（对齐 `chunk-rows.ts` `expandRow`/`decodeStorageRecord`，`text-chunks`/`reasoning-chunks`/`tool-call-chunks` 展开为原始 `assistant/chunk`，其余逐字透传）；`Dsh.Viewer.LoadSelected` 已接入 zstd + chunk 展开。**待补**：`.zstd` 目录树分组 UI（L2）、L1 单元测试 |
| **L2 融入主客户端** | 作为主 WPF 客户端"会话档案"Region Tab；与在线会话联动。 | 🟡 **部分实现（2026-08-24）**：查看器左侧由扁平文件列表改为 **workspace→session TreeView**（`SessionPathResolver.Scan` 按解码 cwd 分组，`DecodeSegment` 还原路径标签，zstd 后缀标注），选中会话经 L1 管线渲染。**待补**：独立查看器融入主客户端 Region Tab（当前仍为独立 `Dsh.Viewer` 窗口）+ 与在线会话联动 |
| **L3 增强渲染** | Markdown/代码高亮/工具树/图片（复用主客户端渲染管线）。 | ⬜ 待实现 |
| **L4 检索与导出增强** | 跨会话搜索、HTML/CSV/批量导出、token 聚合统计。 | ⬜ 待实现 |

---

*文档生成日期：2026-08-15（2026-09-03 修订：统一"Viewer 基于本仓库 SessionFold 自研"表述）。*
*代码来源：本仓库 `Dsh.App.SessionFold`；配套：`02-feature-set.md` 域 I、`07-architecture.md` §1。*
