# 证据索引与假设登记 —— AI 本地代理网关

真相源：`ai-gateway.structurizr.dsl`。本文件说明每个节点/边凭什么存在。
置信度定义沿用插件的 `architecture-evidence-model.md`：high / medium / low / unknown。

---

## 阶段一：代码库探索结论

| 探针 | 命令 | 结果 | 架构含义 |
| --- | --- | --- | --- |
| 会话工作区内容 | `ls -la C:/Users/k2767/Documents/Qoder/2026-10-14/a369e3b0` | `total 0`，完全为空 | ⚠️ 这是**会话工作区**，不是项目根。它曾是本模型六份资产的临时落盘处，现已移出 |
| **项目根** | `ls -la E:/key` + 实写/删除探针 + `git rev-parse` | 存在但**空**；写探针成功；非 git 仓库；E 盘余量 138.1 GB | ✅ **项目根 = `E:\key`**。本目录 `E:\key\docs\architecture\` 即由会话工作区**移动**（非复制）而来，避免两处真相。待办：纳入版本控制需先 `git init` |
| 项目知识 | `SearchKnowledge(mode=search)` | "No knowledge has been generated for the current workspace yet" | 无历史架构记忆，无需处理与旧文档的冲突 |
| git 仓库 | 会话目录与 `E:\key` 双重探活 | **两者都不是仓库** | 无法用 `git log`/`git diff` 做变更证据；依赖分析只能建立在设计意图上 |
| .NET SDK | `dotnet --list-sdks` | **空**；`C:\Program Files\dotnet\sdk` 不存在 | ⛔ **硬阻塞**：无法 `dotnet new/build`，DSL 里任何容器都无法实例化 |
| .NET 运行时 | `dotnet --list-runtimes` | `Microsoft.NETCore.App 8.0.21 / 8.0.28`、`Microsoft.WindowsDesktop.App 8.0.28` | WPF 目标框架 `net8.0-windows` 的**运行时**已就位，只缺 SDK |
| Graphviz | `command -v dot` | 无输出 | DOT 图未实渲，布局效果未验证 |
| Node | `node -v` | v24.15.0 | 与本项目无关（C# 栈） |

**因为项目根为空，本模型没有任何 `current` 状态节点。** 这是阶段一最重要的结论，比图本身重要。

## 阶段二：需求基线的证据来源

D1–D4 全部来自 2026-10-14 的澄清问答（`AskUserQuestion`，用户当面选定），证据类型为 **human-assumption（架构意图）**，置信度 **high**（因为是决策本身而非推断），但**不来自代码**。

| ID | 决策 | 来源 | 置信 | 是否可逆 |
| --- | --- | --- | --- | --- |
| D1 | 双向矩阵 Anthropic + OpenAI | 用户选定「双向矩阵（推荐）」 | high | 可逆，但会退化 codec 层 |
| D2 | DPAPI + SQLite 单库 | 用户选定「DPAPI + SQLite 单库（推荐）」 | high | **半可逆**：见 OPEN-02 |
| D3 | 同进程内嵌 Kestrel | 用户选定「同进程内嵌 Kestrel（推荐）」 | high | 可逆，代价 = `ActivationState` 改 IPC（影响半径表：大） |
| D4 | MVP 仅 tool_use / function calling 双向 | 用户在该多选题**只勾了这一项** | high | 可逆（加东西便宜） |
| **D5** | 轻量 / 极简 / 纯本地；UI 只保留 模型管理·代理密钥管理·一键切换·基础运行日志；拒绝统计图表·多用户·云同步 | 用户 2026-10-14 **第二轮补充陈述**（证据类型：user-statement） | high | 可逆，但方向是"再加东西"，与定位冲突 |
| **D6** | 代理密钥机制：真实 Key 本地 DPAPI+SQLite；UI 生成代理密钥；入站验代理密钥 → 内存替换真实 Key → 转发 | 用户 2026-10-14 **第二轮补充陈述**（证据类型：user-statement） | high | **半可逆**：哈希方案与"能否回显"一旦写入第一条记录即固化 |
| **D7** | 路由全部来自全局激活档，代理密钥不含路由信息 | 用户第二轮澄清选定（推荐档） | high | 代价：所有入站客户端共享同一当前模型，**不能同时用不同模型** |
| **D8** | 代理密钥只存不可逆 SHA-256，生成后不可回显 | 用户第二轮澄清选定（推荐档） | high | 代价：忘了只能重新生成 + 重填全部客户端配置；固化点已跨越 |
| **D9** | 监听地址写死 `127.0.0.1`，只有端口可在 UI 改 | 用户第二轮澄清选定（推荐档） | high | 代价：放弃跨机共享这条产品可能 |
| **D10** | 失败防护只做 `FixedTimeEquals`，无计数/退避/锁定 | 用户第二轮澄清选定（推荐档） | high | 代价：一旦将来开放 LAN，此档不足以应对外部猜解 |

**D4 与 D5 有一处正面冲突并已按 D5 解决**：D4 让"请求日志 + token/成本统计"出局，D5 又把"基础运行日志"列为 UI 四类核心之一。我的处理是**拆开**：`runLog`（一行一请求、只记指纹）回归 MVP，**统计图表 / 成本核算 / 请求体响应内容**留在不做清单。这是我做的取舍，不是用户明示的 —— 若理解错，请指出（`UNKNOWN-03`）。

原始请求文本里另外三条是**用户直接陈述**（证据类型 document/user-statement，置信 high）：
- 技术栈 C# / .NET 8（或最新）、WPF、内嵌 Kestrel、SQLite 或 JSON 加密存储
- 代理服务地址形态 `http://localhost:8000`，端点 `/v1/messages` → `/v1/chat/completions`，**必须支持 SSE 流式**，客户端包含 Claude Code
- 需使用 Windows DPAPI 加密 Key

## 节点证据与置信度

### 高置信（用户直接陈述）
`gateway`、`appExe`、`configDb`、`dpapi`、`httpEndpoints`(端点名)、`anthropicCodec`、`openAiCodec`、`canonicalModel`、`streamRelay`(SSE 要求)、`secretProtector`(DPAPI 要求)、`claudeCode`、`operator`。

### 中置信（由协议规范推导，非用户陈述）
| 节点 | 凭什么 | 为何不是 high |
| --- | --- | --- |
| `toolCallBridge` | Anthropic `tool_use` 块生命周期 vs OpenAI 分片 `tool_calls` 的语义差 | 用户勾了 tool_use 双向，但"需要一个独立组件"是我的推断 |
| `routeResolver` | 请求体 `model` 字段与真实 Provider 模型名必然不一致 | 推断自协议形状，用户未提"路由" |
| `activationState` | D3（同进程）的直接后果 | 推断，非明示 |
| `httpEndpoints` 里的 `/v1/messages/count_tokens` | Claude Code 实际会调用它 | ⚠️ **未经实测**，见 OPEN-04 |

### D6 派生节点：机制是用户明示，实现细节是我推的

这点必须分开写，否则图会比文本显得更确定：

| 内容 | 置信 | 凭什么 |
| --- | --- | --- |
| 「验代理密钥 → 内存替换真实 Key → 转发」这条链路 | **high** | 用户明示（D6 原文） |
| UI 支持生成代理密钥 | **high** | 用户明示 |
| 代理密钥**用单向哈希存储**、生成后**不可回显** | **high（D8 已决）** | 第二轮澄清选定「只存哈希，不可回显（推荐）」 |
| 裸 SHA-256、**无盐、不用慢哈希** | **medium（我的推理，用户未明说）** | D8 选了哈希，但"无盐是否可接受"是我给的论证：密钥由程序生成、高熵随机，不存在离线猜解面。若你不同意这条论证，加盐只需多一列，不影响任何其他组件 |
| 比对使用 `CryptographicOperations.FixedTimeEquals` | **high（D10 已决）** | 第二轮澄清选定「只做定时安全比较（推荐）」 |
| 鉴权器**零可变状态**：无失败计数、无退避、无锁定 | **high（D10 已决）** | 同上。连带好处：天然满足 `RISK-05` 的无状态要求 |
| 鉴权器与注入器**拆成两个组件** | **medium（我的判断，用户未要求）** | 用户说的是"验证并替换"，一句话本可对应一个中间件。拆开的理由在脊柱②。**若你认为这算过度设计可以合并** —— 但合并后 `FORBIDDEN-07` 消失，暴露面从 1 处变成"整个请求管道" |
| 运行日志**只记前缀指纹** | **medium（我的补强）** | D5 要日志 + D6 有密钥 ⇒ 日志是泄露面。这条是我补的约束，用户未提 |
| **每请求现查哈希、不加内存缓存** | **medium（我的推导）** | 由「吊销要即刻生效」推出；用户未提。已作为 `DECIDED-01` 登记，因为它是**加了就静默失效**的那类约束 |
| `/health` 免鉴权 | **low（我的建议）** | 为方便 UI 自检端口活着。按 D9 之后仍有价值，但**可删** |
| 路由**不**读密钥属性 | **high（D7 已决）** | 第二轮澄清选定「全部跟随全局激活档（推荐）」 |
| 监听地址**写死**回环、只有端口可改 | **high（D9 已决）** | 第二轮澄清选定「只绑 127.0.0.1，端口可改（推荐）」 |

### 低置信 / 已标 `LowConfidence`
| 节点 | 状态 | 说明 |
| --- | --- | --- |
| `openAiClient`（入站 OpenAI 协议客户端） | **inferred** | 被 D1 的"双向矩阵"带进来，但没有任何证据表明首期存在这类客户端。若无，则 L1 侧只有 `claudeCode` |
| `viewModels` 的 `CommunityToolkit.Mvvm` | **选型待定** | 用户未指定 MVVM 框架 |
| `repositories` 的 `Dapper 或 EF Core` | **选型待定** | 用户只说"SQLite"。见 OPEN-03 |
| `failoverPolicy` | **Phase 2** | D4 已出局且进不做清单 |
| `runLog` 的 MVP 归属 | **已由 D5 提回 MVP** | 原名 `requestAudit`（含统计/成本，Phase 2）已重命名并收窄为"一行一请求 + 只记指纹"，统计/成本仍在不做清单 |

### 无 code 证据的通用声明
本模型 `sourceRefPolicy = design-intent`：所有 `sourceRefs` 都是**未来文件的意图位置**（例如 `AiGateway.Mapping.Anthropic/AnthropicEventWriter.cs`），**当前不可解析**。这与插件里"教学用 illustrative 模型"同类，不是现状证据。代码落地后需逐节点补真实 `file:line` 并把 `target` 改 `current`。

## 边证据

| 边集 | 类型 | 置信 | 依据 |
| --- | --- | --- | --- |
| `claudeCode → gateway`（/v1/messages + SSE） | runtime-call, HTTP/SSE | high | 用户明示"Claude Code 等客户端使用" |
| `gateway → openAiApi / anthropicApi` | runtime-call, HTTPS | high | 用户明示 OpenAI/Claude/DeepSeek |
| `appExe → configDb` | data-access, SQL | high | D2 |
| `secretProtector → dpapi` | runtime-call, P/Invoke | high | 用户明示 DPAPI |
| codec → canonicalModel | compile-time | medium | 由 D1 的"两段式避免 N×M"推出；用户接受了我的选项说明 |
| `Hosting → 具体 codec` | config-wiring | medium | .NET DI 惯例推断 |
| `Providers → 远端 API` | runtime-call, HTTPS | **high（且刻意红色标注）** | 这是全图**唯一**该携带真实 Key 的边。`request-flow.dot` 里它是唯一的 `penwidth=3` 红边，DOT 与不变式 `guard` 节点共同指向这一条 |
| `ProxyKeyAuth → repositories` | compile-time（取哈希） | high 机制 / medium 细节 | D6 明示要验证；但"查哈希"而非"查密文"是我的选择（`OPEN-09`） |
| `CredInjector → Providers` | in-process（写鉴权头） | medium | 拆成独立注入点是我的判断，非用户明示（见上表 D6 派生节点） |
| `HttpEndpoints → Db`（红色） | **forbidden-edge** | n/a | `FORBIDDEN-05/08`，画出来只为被测试拦 |
| `ProxyKeyAuth → Security`（红色） | **forbidden-edge** | n/a | `FORBIDDEN-07`，D6 新增，防"顺手取真实 Key"这条后门边 |
| `RunLog → Security`（红色） | **forbidden-edge** | n/a | `FORBIDDEN-06`，D5 与 D6 交叉点上新增 |
| `AnthropicCodec → OpenAiCodec`（红色） | **forbidden-edge** | n/a | 画出来是为了被架构测试拦截，**不代表存在** |

## 开放问题（阶段四开工前需要决）

| ID | 问题 | 为什么现在不能定 | 便宜的决法 |
| --- | --- | --- | --- |
| OPEN-01 | 入站 `thinking` 配置怎么处理：丢弃 / 透传给原生 Anthropic Provider / 报错？ | D4 让 thinking 出局，但 Claude Code 会发这个字段，不能"没有" | 先定"透传给原生 Anthropic、其余丢弃并回一条警告日志"，实现时再验 |
| OPEN-02 | DPAPI 密文的**迁移与识别**：secret 表是否需要 `protector_id` + `key_version`？ | 一旦写入第一行 DPAPI 密文，事后补列能救，但事后**没有旧密文的来源标记**就救不了 | **现在就加两列**，成本几乎为零 —— 这是本表里唯一"现在不做以后贵"的项 |
| OPEN-03 | Dapper vs EF Core | 纯实现选型，不影响 L3 边界（`repositories` 端口不变） | 一次 2 小时 spike 决出，别赌 |
| OPEN-04 | Claude Code 对 `count_tokens` 的依赖强度 | 需要真实客户端实测 | 装好 SDK 后第一件事就是拿真 Claude Code 打一次探针 |
| OPEN-05 | 入站是否真需要 OpenAI 协议（D1 的双向是否被用满） | 无使用证据 | 若确定不要，`openAiCodec` 的**入站侧**可砍，出站侧保留 |
| OPEN-06 | 端口 8000 冲突与 LAN 暴露开关 | — | **已决（D9）**：地址写死回环、无 LAN 开关，端口可在 UI 改 |
| OPEN-07 | 首个要接的真实 Provider 是 DeepSeek 还是 OpenAI | 影响 `upstreamClients` 的 profile 形态优先级 | 与 OPEN-04 同批实测 |
| OPEN-08 | 代理密钥是否各自钉死一个模型 | — | **已决（D7）**：路由全部来自全局激活档。接受代价：多客户端不能同时用不同模型 |
| OPEN-09 | 代理密钥生成后能否事后回显 | — | **已决（D8）**：只存不可逆 SHA-256，不可回显。接受代价：忘了就重新生成 |
| OPEN-10 | 鉴权失败是否退避/锁定 + 监听范围 | — | **已决（D10 + D9）**：只做 `FixedTimeEquals`；地址写死回环 |

> 第二轮澄清把代理密钥机制带来的三条语义分叉全部收敛到**最轻档**，且四题一致选推荐项 —— 说明「轻量」这条定位是真实的决策约束，不只是口号。剩下的 OPEN-01～05、07 全是**需要实测或需要一次 spike** 的技术问题，不再有需要替你做主的产品判断。

## 本模型刻意不知道的事（不画成通路）

- OAuth / Claude Pro-Max 订阅制接入 —— 未提，未画。
- 多用户 / 团队协作 / 配置云同步 —— DPAPI 与"跨机同步"天然冲突，属未探测区，且已进不做清单。
- **局域网多机共享 —— 已按 D9 明确排除（不是"还没做"，是不做）**：监听地址写死回环。
- Windows 服务/开机自启形态 —— D3 选了同进程 GUI，服务形态与之冲突，未探测。
- 上游响应的**非流式**路径细节（`stream: false`）—— 结构上存在，但未展开。
- Claude Code 的具体版本与其请求体的真实字段集合 —— **未实测，OPEN-04**。
