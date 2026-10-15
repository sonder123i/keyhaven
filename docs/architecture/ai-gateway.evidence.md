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
| **DeepSeek 已改模型名**（S8 当天核实）：现名 `deepseek-flash` 与 `deepseek-v4-pro`；`claude-opus*` 映射到 `deepseek-v4-pro`，`claude-sonnet*` / `claude-haiku*` / 未知名映射到 `deepseek-flash`；旧名 `deepseek-v4-flash` 等仍被接受。本文早先写的 `deepseek-chat` / `deepseek-reasoner` 已过时 | https://api-docs.deepseek.com/guides/anthropic_api · https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code · https://api-docs.deepseek.com/quick_start/pricing | curl 取原页面后抽取文字（第二次抓定价页被机器人校验拦下）；S8 实测两个名字都能用（§7 T1 / T6） | high |
| Claude Code 对 5xx 与 401 重试约 3 分钟，`x-should-retry: false` 让它只发一次 | 本机实测（§7） | 桩服务 + 真 Claude Code 2.1.300 | high |
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
| OPEN-11 | 发布形态：依赖框架还是自包含 | **已决（S10 实测四种产物后你选定）**：依赖框架 win-x64（0.37 MB / 9 文件，热启动 587–644 ms）。自包含最轻 79.3 MB，且"单文件"要么旁边留 6 个原生 DLL、要么首跑往 `%TEMP%\.net` 解 211 MB。同轮你定了不做开机自启 |
| OPEN-12 | DeepSeek 是否接受 `?beta=true`、是否支持 `count_tokens`、错误体是否回显 Key | **已决（S8 实测，§7）**：接受 `?beta=true`；支持 `count_tokens`（但 Claude Code 没调用过）；Key 错误时只回显末 4 位，其余错误不回显 |
| OPEN-13 | IHttpClientFactory 自带日志处理器是否给鉴权头打码 | **已决（S5 实测）**：不打码，Trace 级原样记录真实 Key；`RedactLoggedHeaders` 只打码格式化文本，结构化字段仍是明文 ⇒ 上游客户端用 `RemoveAllLoggers()`，护栏⑦守住 |

## 6. 刻意不画的东西

OAuth / Claude 订阅接入、多用户、云同步、局域网共享、Windows 服务形态、开机自启（托盘常驻已覆盖"别关窗即断"；自启要不要，S10 再问你）。

## 7. M1 验收实测（S8，2026-10-15）

**环境**：
- Claude Code 2.1.300（npm 全局安装）；网关为 Debug 构建；上游是 DeepSeek `https://api.deepseek.com/anthropic`。
- Claude Code 用 `-p --output-format stream-json` 跑，配置目录指向临时目录，不碰 `~/.claude`。只设 `ANTHROPIC_BASE_URL` 与 `ANTHROPIC_AUTH_TOKEN`，即 S7 配置片段的原样内容；没有设任何模型相关的环境变量。
- Claude Code 与网关之间插了一个记录中继，只记路径、状态码、耗时与事件计数，不记请求内容与任何密钥。
- DeepSeek Key 经 UI 存入，`config.json` 里只有密文；验收后在临时文件与配置里搜不到它的明文。

| # | 项 | 做法 | 现象 | 结论 |
| --- | --- | --- | --- | --- |
| T0 | 没有激活上游 | 直接发请求 | **修复前**：Claude Code 把 503 当临时故障，重试 10 次，**174 秒**后才显示错误。**修复后**：1 次请求，3 秒显示「No upstream is active in the gateway.」 | 已修（RISK-17） |
| T1 | 普通对话 | 问一句话的问题 | 200；Claude Code 发的模型名是 `claude-opus-5-5`，DeepSeek 回 `deepseek-flash`；回答分 5 个 `text_delta` 到达，首字节 0.91 秒 | ✅ 直通、`?beta=true` 被接受、真流式 |
| T2 | 工具调用 | 让它用 Write 建文件，再用 Read 读回 | 3 轮往返；磁盘上的文件内容恰为 `gateway ok`；历史里的 thinking 块原样回传给 DeepSeek，也被接受 | ✅ |
| T3 | thinking | 观察全部请求 | Claude Code 默认就带 `thinking: {type: adaptive}`；DeepSeek 回 thinking 块与 `thinking_delta`（T2 第一轮有 104 个） | ✅ 不需要任何环境变量 |
| T4 | 图片 | 用 Read 读一张写着 7429 的 PNG | 工具结果里的 image 块到达上游；回答 7429 | ✅ |
| T5 | 中途中止 | 长文生成到第 22 秒时杀掉 Claude Code | 中继记下客户端断开，连接立即关闭；同时并发的另一个请求正常完成 | ✅（网关取消上游请求这一环由 S6 测试覆盖） |
| T6 | 一键切换 | 在 UI 里切到 `deepseek-v4-pro` | 下一个请求响应里的 model 是 `deepseek-v4-pro` | ✅ |
| T7 | `count_tokens` | 用 curl 经网关调用 | 200 `{"input_tokens":86}`；但所有 Claude Code 运行中都**从未调用**它 | DeepSeek 支持；对 M1 无影响 |
| T8 | 上游错误体会不会回显 Key | 用合法 Key 发坏请求；另用假 Key 发请求 | 合法 Key：422，正文里没有任何 Key 片段；假 Key：401「Your api key: ****ECHO is invalid」，**只回显末 4 位** | RISK-12 关闭 |
| T8b | 存的上游 Key 是错的 | 激活一个存了假 Key 的临时上游 | **修复前** 11 次请求、178 秒才显示；**修复后** 1 次请求、3 秒，Claude Code 显示「Failed to authenticate. API Error: 401 Authentication Fails, Your api key: ****ECHO is invalid」 | 已修（RISK-17） |
| T9 | 吊销代理密钥 | 在 UI 里吊销后再发请求 | 下一个请求就是 401「Missing, unknown or revoked proxy key.」；1 次请求、2 秒 | ✅ |

**S8 发现并修复的问题（RISK-17）**：Claude Code 对 5xx 和 401 都会重试约 3 分钟（10 次，指数退避）才把错误显示出来，除非响应带 `x-should-retry: false`。用桩服务实测：

| 桩服务的响应 | Claude Code 的请求次数 | 耗时 |
| --- | --- | --- |
| 503 + `x-should-retry: false` | 1 次 | — |
| 501，不带该头 | 8 次，到 100 秒时仍在重试 | ≥ 100 秒 |
| 401 + `x-should-retry: false` | 1 次 | — |
| 400 | 2 次 | 3 秒 |
| 403 | 1 次 | 2 秒 |

网关的修法：
- 网关自己产生的配置类错误一律带 `x-should-retry: false`：没有激活上游（503）、OpenAI 上游（501）、请求体不是 JSON（400）、Key 解不开（503）。代理密钥无效的 401 也一样。
- 上游返回 401、且上游自己没给重试提示时，网关补上 `false`。
- `x-should-retry` 加进响应头白名单，上游自己的提示原样透传。
- 上游不可达（502）保持可重试。

**`-p` 模式覆盖不到的**：交互式会话（按 Esc / Ctrl+C 中止、长会话的上下文压缩），需要你在自己的终端里用一次。
