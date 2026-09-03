# 变更日志

本项目的所有重要变更都记录在此。

格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

> 📌 本文件是对外发布的变更记录。发布前的内部迭代细节（含逐批次根因分析）未随公开仓库发布。

---

## [Unreleased]

暂无。

---

## [0.1.0-alpha] — 2026-09-02

首个公开发布的 alpha 版本。

> ⚠️ **alpha 状态说明**：功能完整度较高，但仍有未实现项与已知限制（见下文及 README「Known limitations / 已知限制」）。
> **请勿视为生产就绪（stable）。**
>
> 📌 **关于提交历史**：本项目自 2026-08-15 起开发，公开发布前累计 231 次提交。
> 公开仓库以本次 `v0.1.0-alpha` 为起点（发布前的内部迭代历史未包含），
> 后续变更将逐条记录在本文件中。

### 已实现

#### 对话核心
- 主聊天闭环（提问 → 流式回答 → 工具调用 → 结果）
- 流式渲染与增量合并，长会话性能有界
- 会话压缩（compaction）与重试（retry）行
- 富样式状态行（C17）
- 撤销、重新生成、停止生成
- 双击消息/轨迹步骤在独立窗口查看完整内容
- HTML / Markdown 预览（HTML 走系统浏览器）

#### 实时协作
- 双 WebSocket 下行流（事件流 + RPC 响应流），含重连世代语义
- 用户提问、审批、Jobs-Queue 面板、重连回放
- Plan 芯片（D3）与权限芯片（D5）—— 走 `settings.mutate` + CAS 重试
- 断线重连与状态恢复

#### 会话与工作区
- 会话树（workspace → session）、新建/重命名/归档/删除
- 会话历史向上翻页加载
- 会话搜索（命中片段高亮）
- **离线回放（J7）**：网络不可达时回读本地缓存

#### Windows 原生增强
- 多窗口、系统托盘、全局热键、窗口置顶
- 深色标题栏、窄屏响应式布局、会话自动滚动
- 服务自启动引导：自动探测目录 → `pnpm install` + `build` → 启动服务 → 轮询就绪

#### 本地存储查看（Dsh.Viewer）
- **L0** 基线：目录扫描、JSONL 解析、会话树与消息列表
- **L1** 解码：`SessionPathResolver`（`--<encoded-cwd>--` 扫描 + `~XXXX` 反转义）、`ZstdReader`（`.zstd` 透明解压）、`ChunkRowExpander`
- **L2 融合（部分）**：workspace → session 两级树浏览（独立窗口）；融入主客户端 Region Tab 与在线联动待做

#### 国际化
- 6 种语言：英语、中文、日语、德语、法语、西班牙语

#### 其他
- 设置面板、凭据管理、模型发现
- 313 个测试用例（`Dsh.App.Tests` 218 + `Dsh.Contract.Tests` 95）

### 已知限制

以下功能**尚未实现**（详见 [`os/17-landable-gap.md`](os/17-landable-gap.md)）：

| 项 | 说明 |
|---|---|
| **H2** | Canvas 时间轴交互（拖拽聚焦 / 滚轮缩放）—— 轴已自绘，缺交互层 |
| **H6** | TodoDock 写操作（当前仅右面板只读列表） |
| **H7** | QueueDock 编辑/删除/steer（`session.updateQueue` 契约已就绪，客户端未接线） |
| **I 域 L1 测试 / L2 融合 / L3–L4** | 查看器单元测试；融入主客户端；增强渲染；跨会话搜索与导出 |
| **J1** | 托盘作业/审批 balloon 通知 |
| **K3 / K4** | 日期数字本地化、字体与 bidi 排版适配 |
| **G3 / C12** | 作业 kill、凭据变更事件 —— **blocked-by-host**（需上游先加 RPC） |
| 内联文件提及（H4） | 已实现路径解析，但渲染仍为降级 |

### 技术说明

- **依赖外部 `deepseek-harness`**：本仓库不包含其源码，运行时需指定本地检出目录（环境变量 `DSH_HARNESS_DIR`，或通过服务管理面板选择）。
- **仅支持 Windows**：`Dsh.Wpf` 与 `Dsh.Viewer` 目标框架为 `net8.0-windows`。
- 当前实现的关键阈值与常量记录见 [`os/20-current-implementation.md`](os/20-current-implementation.md)。

---

## 相关文档

- [`README.md`](README.md) —— 项目概览与已知限制
- [`CONTRIBUTING.md`](CONTRIBUTING.md) —— 贡献指南
- [`os/README.md`](os/README.md) —— 开发过程文档地图
- [`LICENSE`](LICENSE) —— MIT
