# 证据索引与决策来源 —— AI 本地代理网关

真相源：`ai-gateway.structurizr.dsl`。本文件说明每条决策**出自谁、哪一轮**，以及每条外部事实的出处。2026-10-15 修订；旧版在 git `cc01023`。

置信度：high = 你原话或你当面选定 / 官方文档；medium = 我的推断；low = 未核实。

---

## 1. 你的原话（第 0 轮，原始需求，置信 high）

- 技术栈：C# / .NET 8（或最新）、WPF、内嵌 Kestrel、**"SQLite 或 JSON 加密存储"**。
- 代理地址 `http://localhost:8000`；端点 **`/v1/messages` → `/v1/chat/completions`**；**必须支持 SSE 流式**；客户端包含 Claude Code。
- 用 Windows DPAPI 加密 Key。
- 上游点名：OpenAI / Claude / DeepSeek。

## 2. 三轮决策

| 轮次 | 日期 | 决策 | 怎么定的 | 备注 |
| --- | --- | --- | --- | --- |
| 1 | 10-14 | D1 双向矩阵、D2 SQLite 单库、D3 同进程 Kestrel、D4 MVP 只含 tool_use | 上一个模型出选项，你选了标"（推荐）"的项 | **D1、D2 的推荐项比你的原话更重**（原话单向、"SQLite 或 JSON"），第 3 轮已修订 |
| 2 | 10-14 | D5 轻量极简纯本地、D6 代理密钥机制 | 你的补充陈述 | **D5 出现在 D1/D2 之后，但 D1/D2 从没按它复核** |
| 2 | 10-14 | D7 全局路由、D8 只存哈希、D9 写死回环、D10 只做定时比较 | 你选推荐档（均为最轻档） | 保留 |
| 3 | 10-15 | D12 注入器 = 管道处理器（H） | review 后你选定 | 我先推荐过"公共端口"接法，随后自我推翻 |
| 3 | 10-15 | **D1 修订**：只收 Anthropic 入站，出站直通 + 翻译 | review 后你选推荐项 | 推荐项已对照原话收窄，不是扩大 |
| 3 | 10-15 | **D2 修订**：JSON + DPAPI 字段，日志只在内存 | 同上 | 同上 |
| 3 | 10-15 | D11 3+1 个项目 | 同上 | |
| 3 | 10-15 | D14 M1 = DeepSeek 直通 | 同上 | |
| 3 | 10-15 | D13 `IRealKeyVault` 只写；DECIDED-01 修订为写入即替换内存快照 | review 结论 | 推理见 `findings` §4 与 `understanding` §5 |

**一条教训**：你几乎总是选标了推荐的那一项，所以**推荐项就是决定**。第 1 轮的偏重推荐原样进入了架构；第 3 轮的推荐都已逐条对照你的原话，只往原话方向收。

## 3. 外部证据（2026-10-15 当场获取）

| 事实 | 出处 | 获取方式 | 置信 |
| --- | --- | --- | --- |
| 网关必须转发 `anthropic-version`（去掉会坏）与 `anthropic-beta`（去掉可能 400） | https://code.claude.com/docs/en/llm-gateway-protocol | WebFetch 摘要（旧址 docs.anthropic.com/en/docs/claude-code/llm-gateway 301 跳转到 code.claude.com） | high（官方），但为摘要非逐字 |
| 推理请求发往 `/v1/messages?beta=true` | 同上 | 同上 | 同上 |
| `count_tokens` 可选，缺失时 Claude Code 按字符估算上下文 | 同上 | 同上 | 同上 |
| `ANTHROPIC_AUTH_TOKEN` → `Authorization`；`ANTHROPIC_API_KEY` → `x-api-key`；`CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS=1` 可关预发布能力 | 同上 | 同上 | 同上 |
| DeepSeek `https://api.deepseek.com/anthropic`：thinking 支持（忽略 budget_tokens）、tools / tool_choice / 图片支持、`anthropic-beta` 与 `cache_control` 忽略、未知模型名映射到默认模型；文档未提 `count_tokens` | https://api-docs.deepseek.com/guides/anthropic_api | WebFetch 摘要 | high（官方），摘要 |
| cc-switch：14.4 万星、2026-10-14 有推送、Windows 原生；本机路由 `127.0.0.1:15721`、格式转换、配置里只写占位密钥 `PROXY_MANAGED`、热切换、故障转移；数据在 `~/.cc-switch/cc-switch.db`（SQLite） | https://github.com/farion1231/cc-switch README 第 344、363、484、511–513 行 | `gh api` 取 README 原文 + 关键词检索 | high |
| cc-switch 是否加密存 Key、本机路由是否校验占位密钥 | 同仓库源码与 `docs/user-manual/en/4-proxy/` | **未查** | low |
| claude-code-router：3.8 万星，Anthropic⇄OpenAI 等多协议翻译 | https://github.com/musistudio/claude-code-router | `gh api` | high |
| 旧 DSL 在第 229 行（元素样式 `dashed true`）解析失败 | Structurizr CLI v2025.11.09 `validate` | 本机实跑 | high |

## 4. 节点置信度

| 节点 | 置信 | 依据 |
| --- | --- | --- |
| Claude Code、DPAPI、Kestrel、SSE、两类上游 | high | 原话 |
| 直通转发器 | high | 原话点名的上游里两家原生说 Anthropic 协议（§3） |
| 翻译器、工具调用增量重编码 | high（机制）/ medium（拆成两个组件） | 原话"→ /v1/chat/completions"；拆分是我的判断 |
| 代理密钥验证器与注入处理器分开 | medium | 你说"验证并替换"；拆开是为了让失败路径碰不到解密 |
| 激活快照、路由解析 | medium | D3、D7 的直接推论 |
| 托盘常驻 | medium | D3 的推论：关窗即断 |
| 本机不受信来源（威胁方） | medium | 浏览器访问回环端口的常识；Chrome 正在收紧，未逐浏览器核实 |
| 运行日志只在内存 | high | D2 修订 |

## 5. 开放问题

| ID | 问题 | 状态 |
| --- | --- | --- |
| OPEN-01 | 入站 `thinking` 怎么处理 | **已决**：直通原样携带；翻译路径丢弃（D4） |
| OPEN-02 | DPAPI 密文的来源标记 | **已决**：`protector_id` + `key_version` 进 JSON 结构（计划 S2） |
| OPEN-03 | Dapper vs EF Core | **作废**：D2 改 JSON，无 ORM |
| OPEN-04 | `count_tokens` 依赖强度 | **已决**：官方文档写明可选（§3） |
| OPEN-05 | 是否需要 OpenAI 协议入站 | **已决**：不需要（D1 修订） |
| OPEN-07 | 首个上游 | **已决**：DeepSeek 直通（D14）；需要你提供 DeepSeek Key |
| OPEN-11 | 发布形态：依赖框架（目标机需装 ASP.NET Core + WindowsDesktop 两个运行时）还是自包含（体积大） | **开着**：计划 S10 实测两种产物体积后由你选 |
| OPEN-12 | DeepSeek 是否接受 `?beta=true`、是否支持 `count_tokens`、错误体是否回显 Key | **开着**：M1 验收实测（计划 S8） |
| OPEN-13 | IHttpClientFactory 自带日志处理器是否给鉴权头打码 | **已决（S5 实测）**：不打码，Trace 级原样记录真实 Key；`RedactLoggedHeaders` 只打码格式化文本，结构化字段仍是明文 ⇒ 上游客户端用 `RemoveAllLoggers()`，护栏⑦守住 |

## 6. 刻意不画的东西

OAuth / Claude 订阅接入、多用户、云同步、局域网共享、Windows 服务形态、开机自启（托盘常驻已覆盖"别关窗即断"；自启要不要，S10 再问你）。
