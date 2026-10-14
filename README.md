# AI 本地代理网关（Windows WPF）

> 本文件是新会话的入口。架构细节**不在这里**，在 `docs/architecture/`，此处只交代到哪、读到哪一步、以及为什么。

## 这是什么

C#/.NET 8 WPF 桌面应用。本机 `127.0.0.1:<port>` 起一个 Kestrel 代理，把 Anthropic `/v1/messages` ⇄ OpenAI `/v1/chat/completions` 双向互转（含 SSE 流式），供 Claude Code 等客户端使用。真实上游 API Key 用 **Windows DPAPI + SQLite** 加密存本地；对外只发放可撤销的**代理密钥**，验过后在内存里替换成真实 Key 再转发。

**产品定位：轻量、极简、纯本地。** 这条是硬约束，不是我加的。

## 现在在哪一步

阶段 1–3 已完成（代码库探索 → 需求澄清 → 架构设计）。**尚未写任何实现代码，`E:\key` 里除本文档外没有源码。**

| 阶段 | 状态 |
| --- | --- |
| 1 探索 / 2 澄清 / 3 架构 | ✅ 完成，见下 |
| 4 实现计划（拆成带依赖顺序的故事） | ✅ 完成 —— **18 个故事在 `docs/plan/ai-gateway.implementation-plan.json`**（JSON 已校验：无悬空 `blockedBy`、无依赖环、每故事 ≤7 任务且都标 `fits-one-run`） |
| 5 编码 | ⬜ 未开始，**下一步 = S1**（解决方案骨架 + `.gitattributes` + `global.json`） |

## 读这些文件（按顺序）

1. `docs/architecture/ai-gateway.architecture-understanding.md` —— 先读这份。需求基线 D1–D10、**不做清单**、两条架构脊柱、端口与安全梯子的定稿理由。
2. `docs/architecture/ai-gateway.structurizr.dsl` —— 真相源。L1 Context / L2 Container / L3 Component（19 组件）。用 Structurizr DSL viewer 打开可看图。
3. `docs/architecture/ai-gateway.dependency-impact-findings.md` —— 程序集依赖、耦合度、影响半径、三条会自然长出来的后门边。
4. `docs/architecture/ai-gateway.request-flow.dot` —— 一次请求的生命周期，代理密钥在哪一步被换成真实 Key。
5. `docs/architecture/ai-gateway.dependencies.dot` —— 依赖图，含 9 条 `FORBIDDEN-*` 红色禁止边（禁令，不是现状）。
6. `docs/architecture/ai-gateway.evidence.md` —— 每个节点凭什么存在；**哪些是用户明示、哪些是我的推断**（这点很重要，别把我的判断当他的决定）。
7. `docs/architecture/ai-gateway.violations.csv` —— 机器可读的禁令与未闭环项。

## 十条已定决策（不要再讨论，要改先说代价）

D1 双向矩阵 Anthropic+OpenAI（2+2 个 codec，不是 N×M）· D2 DPAPI+SQLite 单库 · D3 同进程内嵌 Kestrel（唯一可部署单元）· D4 MVP 只含 tool_use 双向 · D5 轻量纯本地，UI 只有模型管理/代理密钥管理/一键切换/基础运行日志 · D6 代理密钥验证后内存替换真实 Key · D7 **路由只来自全局激活档，密钥不含路由**（代价：多客户端不能同时用不同模型）· D8 **代理密钥只存不可逆 SHA-256，不可回显** · D9 **监听地址写死 `127.0.0.1`，只有端口可改** · D10 **鉴权失败只做 `FixedTimeEquals`，无计数/退避/锁定**

## 开工前置：已清（2026-10-14 逐条实测，不是"应该没问题"）

| 原阻塞 | 现状 | 实测证据 |
| --- | --- | --- |
| ⛔ 没有 .NET SDK | ✅ 已装 `8.0.426`（`winget install Microsoft.DotNet.SDK.8`，机器级，进 `C:\Program Files\dotnet\sdk`） | 不止看 `--list-sdks`：临时建 `dotnet new wpf` 并 `dotnet build` → `net8.0-windows\Probe.dll`，**0 警告 0 错误**，跑完即删。WPF 要的 WindowsDesktop targeting pack 随 SDK 一起到位。 |
| ⛔ `E:\key` 不是 git 仓库 | ✅ `git init -b main` 已执行，`.gitignore` 由 `dotnet new gitignore` 生成（8433 字节） | 仓库内 `user.email` 覆盖为 `276609447+sonder123i@users.noreply.github.com`（`gh api user` 当场核实的 login+id），**全局微信邮箱未改动**。**尚无首次提交**，等点头再做。 |
| ⛔ 没有 Graphviz，`.dot` 从未实渲 | ✅ 已装 `16.1.0`（`C:\Program Files\Graphviz\bin\dot.exe`；**新开的 shell 才在 PATH 里**，当前会话用全路径） | 两个视图都真渲过：`request-flow.dot` 一次通过；**`dependencies.dot` 原本渲染失败** —— `trouble in init_rank` + `triangulation failed` + `lost ViewModels Storage edge`。病因：FORBIDDEN 禁止边带着 rank 约束，而其中有 `AnthropicCodec⇄OpenAiCodec`、`Core⇄StreamRelay` 两个 2-环和两个 3-环，dot 的分层器要求约束图是 DAG。修法：给那一整块加 `constraint=false`（禁令本来就不该参与布局）→ 现在 stderr 全空。**源文件是 10 条边、9 个编号**（FORBIDDEN-01 画了正反两条），下文"9 条"指编号数。 |

`.dsl` 仍未过 Structurizr 解析器（本机无 Structurizr CLI，只有 JDK 25）—— 这条没解决，但它不阻塞编码。

**本轮新发现的阻塞（README 以前没写）：本机没有 Claude Code。** `where claude` 无、无 `~/.claude`（只有 `~/.codex`）。OPEN-04 原计划"装好 SDK 后拿真 Claude Code 打 `count_tokens` 探针"的前提不成立 —— 要么先装客户端，要么探针退化成裸 HTTP 请求（那只能证明端点在不在，证明不了 Claude Code 是否依赖它）。

**环境余量**：C 盘 250G 已用 90%（剩 26G，SDK 吃掉约 2.5G）；E 盘剩 129G。无代理干扰，`api.nuget.org` 可达，`~/AppData/Roaming/NuGet/NuGet.Config` 只有 nuget.org 一个源。

## 仍然开着的（都需要实测或一次 spike，不需要产品判断）

- `OPEN-04` Claude Code 对 `/v1/messages/count_tokens` 的依赖强度 —— **未实测**，我按"必须存在、可返回估算"设计。SDK 已就绪，但**本机没有 Claude Code**（见上），所以这条现在有两步：先装客户端（或退化为裸 HTTP 探针，只能证端点存在、证不了依赖），再打探针。
- `OPEN-01` 入站 `thinking` 字段怎么降级（Claude Code 会发这个字段，D4 让 thinking 出局但它不由我们决定是否存在）。
- `OPEN-03` Dapper vs EF Core（一次 2 小时 spike 决出，别赌）。
- `OPEN-05` 首期是否真有 OpenAI 协议的入站客户端（双向矩阵是 D1 选型带进来的，无使用证据）。
- `OPEN-07` 第一个接的真实 Provider 是 DeepSeek 还是 OpenAI。
