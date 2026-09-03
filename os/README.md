# 开发文档（os/）

> 📌 **本目录是中文开发文档**。收录**长期有效**的项目文档：功能集、架构、验收标准、契约同步规范、缺口清单与实现事实。
> 逐日自审日志、阶段性计划与设计书等**过程性文档未随公开仓库发布**。

## 项目定位

用 **C# + WPF** 实现覆盖 Web 客户端（`apps/web` + `packages/client`）绝大部分面向用户功能的 Windows 桌面客户端，并附本地存储查看器。后端（LLM / 工具 / 会话逻辑）通过 `packages/host` 的 loopback HTTP/WebSocket 网关**原样复用**，WPF 只重写通信 carrier 与 UI 层。

- **关系**：WPF 覆盖 Web 客户端**绝大部分**功能，并在 Windows 原生交互（目录选择、文件打开、DPI、多窗口、系统托盘、离线查看）上更强；但**并非 100% 等价**——部分交互项仍为只读或未实现，个别项 blocked-by-host。详见主 [`README.md`](../README.md) 的 Known limitations。
- **最大风险**：双 WebSocket 下行流的重连世代语义、大量 UI 状态机等价重写、磁盘格式契约同步。

> ⚠️ 部分文档记录的是"当时计划"而非"当前实现"。**判断现状请以代码与 [`20-current-implementation.md`](20-current-implementation.md) 为准。**

## 文档地图

| 文件 | 角色 | 何时读 |
|---|---|---|
| **`02-feature-set.md`** | **功能集单一事实源**：功能项（ID / Web 行为 / WPF 实现 / 优先级 / 验收 / 状态）+ 原生对比与缺口 | 理解项目边界 / 按 ID 认领任务 |
| **`03-local-storage-viewer.md`** | 独立子模块：离线查看本地会话数据的功能集与兼容契约 | 做查看器模块（`Dsh.Viewer`）时 |
| **`04-acceptance.md`** | 质量门禁：每条功能的可量化验收门槛 | 功能自测 / PR 前 |
| **`05-contract-sync.md`** | 专项规范：host RPC 契约变更 → 哪些功能项须回归 | 改契约相关代码前 |
| **`07-architecture.md`** | 工程结构、分层、模块/类清单、数据流、里程碑 | 编码前 |
| **`12-ui-design.md`** | UI 视觉体系：设计令牌、控件规范、布局、消息流、设置页 | 改 UI 前 |
| **`17-landable-gap.md`** | 按可实现性分类的缺口优先级视图 | 认领任务 / 排期时 |
| **`20-current-implementation.md`** | **代码反推的当前实现事实**：真实生效的阈值/常量及位置 | 调参 / 理解当前行为时 |

### 推荐阅读顺序

1. 主 [`README.md`](../README.md) → 项目是什么、怎么跑起来
2. [`07-architecture.md`](07-architecture.md) → 代码怎么组织
3. [`02-feature-set.md`](02-feature-set.md) → 功能边界与每项状态
4. [`17-landable-gap.md`](17-landable-gap.md) → 还有什么没做、认领哪项
5. 改代码前按需读 `04`（验收）、`05`（契约）、`12`（UI）、`20`（阈值事实）

## 外部参考（不在本仓库）

- 上游项目：`deepseek-harness`（MIT License，Copyright 2026 DeepSeek AI）——**外部依赖，本仓库不包含其源码**，需自行克隆到本地
- 协议契约源：`packages/host/apiproxy/src/api/*`（RPC 方法签名）、`packages/client/connection`（线协议）
