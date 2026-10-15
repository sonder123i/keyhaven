# 证据索引与决策来源 —— AI 本地代理网关

真相源：`ai-gateway.structurizr.dsl`。本文件说明每条决策**出自谁、哪一轮**，以及每条外部事实的出处。2026-10-15 修订；旧版在 git `cc01023`。

**落地状态（2026-10-15）**：M1（直通 DeepSeek）已真机验收（§7）；**M2（OpenAI 翻译：请求方向 + 非流式 + 流式）已实现并测试（S11/S12），真机验收未做**；运行日志（S9）、发布形态（S10）均已落地；144 个测试全绿。组件 → `文件:行` 见 §8。

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

OAuth / Claude 订阅接入、多用户、云同步、局域网共享、Windows 服务形态、开机自启（托盘常驻已覆盖"别关窗即断"；**S10 已定：不做自启**，要开机跑就自己把快捷方式丢进 `shell:startup`，见 D15）。

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
- 网关自己产生的配置类错误一律带 `x-should-retry: false`：没有激活上游（503）、请求体不是 JSON（400）、Key 解不开（503）。代理密钥无效的 401 也一样。（表里那条「OpenAI 上游（501）」是 S8 当时的状态：S11 起 OpenAI 上游走翻译路径不再经过直通，S12 起 `src/` 里已搜不到 501。）
- 上游返回 401、且上游自己没给重试提示时，网关补上 `false`。
- `x-should-retry` 加进响应头白名单，上游自己的提示原样透传。
- 上游不可达（502）保持可重试。

**`-p` 模式覆盖不到的**：交互式会话（按 Esc / Ctrl+C 中止、长会话的上下文压缩），需要你在自己的终端里用一次。

## 8. 落地位置（组件 → 文件:行，2026-10-15）

DSL 的 15 个组件已全部落地。行号取**类声明行**（会随改动漂移，改代码时顺手核一眼）；同文件里的小类型写在括号里。

| 组件（DSL 名） | 程序集 | 落地位置 |
| --- | --- | --- |
| 组合根与宿主（托盘、单实例） | App | `App.xaml.cs:14`、`TrayIcon.cs:6`、`SingleInstanceGuard.cs:3` |
| WPF 界面 | App | `MainWindow.xaml.cs:8`、`IssuedKeyWindow.xaml.cs:6` |
| ViewModels | App | `ViewModels/MainViewModel.cs:9`、`ProvidersViewModel.cs:27`、`ProxyKeysViewModel.cs:13`、`RunLogViewModel.cs:19`、`SettingsViewModel.cs:5`、`ActivationViewModel.cs:7` |
| 本地 HTTP 入口（含统一收尾中间件） | AiGateway | `Hosting/GatewayServer.cs:16`（`Forward` 按上游类型分派、`ObserveRun` 收尾） |
| 代理密钥验证器 | AiGateway | `ProxyKeys/ProxyKeyAuthenticator.cs:7`、`ProxyKeyHasher.cs:6` |
| 激活快照 | AiGateway | `Configuration/GatewayConfig.cs:5`（`ActiveSelection` 在同文件 :30） |
| 路由解析 | AiGateway | `Routing/RouteResolver.cs:16`（`UpstreamRoute` :5、`RouteResult` :9） |
| 直通转发器 | AiGateway | `Passthrough/PassthroughForwarder.cs:18` |
| Anthropic → OpenAI 翻译器 | AiGateway | `Translation/OpenAiTranslationForwarder.cs:20`、`AnthropicToOpenAiTranslator.cs:8`、`OpenAiToAnthropicTranslator.cs:8` |
| 工具调用增量重编码（流式会话） | AiGateway | `Translation/OpenAiStreamSession.cs:13`、`AnthropicStreamEvent.cs:8` |
| 上游 HTTP 客户端 | AiGateway | `AiGatewayServiceCollectionExtensions.cs:14`（命名客户端 + 管道装配）、`Upstream/UpstreamRequest.cs:3` |
| 配置存储 | AiGateway | `Configuration/ConfigStore.cs:7` |
| 运行日志 | AiGateway | `RunLog/RunLogRing.cs:3`（容量 500）、`RunLog/IRunLog.cs:12`、`Hosting/RunRequestState.cs:9` |
| 真实密钥注入处理器 | Security | `CredentialInjectingHandler.cs:7` |
| DPAPI 保护器与只写密钥库 | Security | `SecretProtector.cs:6`、`RealKeyVault.cs:5`、`AiGatewaySecurityServiceCollectionExtensions.cs:7` |

另：发布形态（D16）落在 `src/AiGateway.App/Properties/PublishProfiles/Release-win-x64.pubxml` 与 `docs/install.md`；测试在 `tests/AiGateway.Tests/`（144 个用例）。

## 9. M2 验收实测（2026-10-15）

**环境**
- 客户端 Claude Code 2.1.300；网关为 Debug 构建（含 S11/S12 与 review 修补的全部代码改动，即 `38f58bd`；其后的 `51f87e1`、`3d14f78` 只动文档）。
- 上游 MiMo `https://api.xiaomimimo.com/v1`（**OpenAI 协议 ⇒ 走翻译路径**），模型 `mimo-v2.6-pro`；激活档已确认为它（读配置元数据核对）。
- Claude Code 用 `-p --permission-mode acceptEdits` 跑；配置目录经 `CLAUDE_CONFIG_DIR` 指向临时目录（**不碰 `~/.claude`**，那条真配置至今不存在），`ANTHROPIC_BASE_URL=http://127.0.0.1:8000`、`ANTHROPIC_AUTH_TOKEN` 是网关签发的代理密钥。
- 客户端 → 网关 → MiMo 直连，**中间没有任何中继**；全程没有出现任何真实上游 Key 的明文。

| # | 项 | 做法 | 现象 | 结论 |
| --- | --- | --- | --- | --- |
| T1 | 工具调用往返 | 让它 Write 建 `mimo-ok.txt`（内容 gateway ok）再用 Read 读回 | 退出码 0；文件真的落盘，内容 `gateway ok.`（句号是模型自己加的）；事件流里出现 **2 个 `tool_use` + 2 个 `tool_result`**、`num_turns=3` | ✅ **增量重编码在真客户端上成立**（S12 唯一的未验项） |
| T2 | 流式 | 同上（Claude Code 恒用 `stream:true`） | 事件按 Anthropic 序列到达并被解析（`assistant` × 3、`text` × 2），没有出现 OpenAI 的帧 | ✅ OpenAI chunk → Anthropic 事件重编码成立 |
| T3 | usage（**RISK-03**） | 看 `--output-format stream-json` 里客户端收到的 usage | `message_start` 是 `0/0`（如设计）；**最终 usage = `input_tokens 62499` / `output_tokens 296`**，客户端据此算出 `total_cost_usd 0.2559` | ✅ **上游确实按 include_usage 给了 usage**：`output_tokens` 是模型侧数字，客户端估算不出来；`message_delta` 的补正也被客户端采纳（顺带回答了 A1 的担心） |
| T4 | `max_tokens` vs `max_completion_tokens` | MiMo 官方样例用后者，我们发前者 | 没有 400，请求正常完成 | ✅ MiMo 收 `max_tokens`——当初担心的那条风险不成立 |
| T5 | 上游 401 的失败速度 | 激活档还留在 DeepSeek（其 Key 已失效）时误跑了两次 | 401 在 **20 秒内**返回并显示，S8 那套三分钟重试没有出现 | ✅ `x-should-retry:false` 生效（这条验在**直通**路径；翻译路径的同类行为同源代码） |

**过程中暴露的一个 UX 问题（未修，列为开放项）**：那两次误跑的根因是**一键切换没生效**——应用会**拒绝激活没有 Key 的上游**（S7 的设计），于是请求仍走 DeepSeek、拿到一个与 MiMo 无关的 401。**同日复核时更正了本段原先的说法**：我先前写「提示一闪而过」是错的。读了 `ViewModels/ActivationViewModel.cs` 后确认——失败时 `Message = ex.Message`（如「这个上游还没有保存 Key，请先在「上游与 Key」里保存 Key 再切换。」）会**常驻**在顶栏下方（红字），并且 `CurrentText` 一直显示「当前：DeepSeek · deepseek-flash」/「尚未激活任何上游，Claude Code 的请求会收到 503」。**所以不是没有提示，是提示没被看见**（一行灰字 + 一行红字，视觉权重太低）。对症的改法是**提高状态的可发现性**（把「当前激活档」做成一眼可见的主状态、失败时给带图标的警示块），而不是「加一个提示」。**我没有改代码**；要不要立一条 RISK 行由你定。

**仍未覆盖**：① 翻译路径上的**客户端中止**（RISK-06 在 CSV 里如实标着「没有专题测试」）；② 真机上 Claude Code 的**交互式会话**（Esc / Ctrl+C、上下文压缩）——一直是人工项。

## 10. cc-switch 对照研究（2026-10-15，实读源码）

**为什么记这节**：它是同类里最强的在位者 —— **14.5 万星、MIT、当天（2026-10-15T09:01Z）仍在推**，Tauri + Rust。这节只写**事实与出处**，不写评判。读法是 `gh api` 取原文。

**机制（与我方同构的部分）**：本机 HTTP 服务默认 `127.0.0.1:15721`（地址与端口可配，**含 `0.0.0.0`**，它的文档还教 WSL2 这么配）；改写客户端配置指向本机、退出时写回 direct；客户端配置里只放**占位密钥 `PROXY_MANAGED`** 与固定模型别名（`README.md` 第 484 行）。代理层体量：`proxy/forwarder.rs` **8482 行**、`proxy/handlers.rs` 4185 行、`proxy/usage/parser.rs` 1292 行。

**它解掉的问题与解法**（出处均为 `src-tauri/src/...`）：

| 问题 | 它的解法（文件） |
| --- | --- |
| 模型名会过期 | 按 baseURL **现查**：候选 `/v1/models`、`/models`、剥离已知兼容后缀后的变体；预设可覆写 `modelsUrl`（`services/model_fetch.rs`，候选顺序见源码 234–241 行） |
| 跨上游混用 → 签名/密文失效 | 「**失败 → 剥离 → 对同一家重发一次**」的整流模式（`proxy/thinking_rectifier.rs` 722 行、`proxy/opaque_state_rectifier.rs` 804 行） |
| 另一种协议**没有字段**放的东西 | 借 thinking signature / redacted-thinking 当信使，客户端回放时还原（`proxy/providers/reasoning_bridge.rs`） |
| Chat 上游把思考**内联进正文** | 专门剥离：流式 `InlineThinkSplitter` / 非流式 `split_leading_think_block`，只认正文开头的块（`proxy/providers/inline_think.rs`） |
| 函数参数**乱序/迟到**（MiniMax `/v1/responses`，实测 2026-10-03） | 把参数为空的结束事件**先扣住**，等真参数到了再放行（`proxy/providers/responses_late_arguments.rs`） |
| 改客户端配置不破坏排版 | `shift_remove` 删除 + 原位改值 + 追加末尾；承诺「键、值、顺序不变；第一次写入可能规范化空白与转义，之后字节稳定」（`live/patch/json.rs`） |
| 改坏了要能还回来 | 改写前把原配置**备份进库**（`database/schema.rs` 的 `proxy_live_backup` 表） |
| 「能连上」≠「能用」 | 只探 base_url 可达性、**不发真实模型请求**；连通性检查**绝不触碰熔断器**，熔断器只由真实流量成败**被动**驱动（`services/stream_check.rs`） |
| 客户端私有字段外泄 | 递归过滤 `_` 前缀字段、支持白名单（`proxy/body_filter.rs`） |
| 帮用户省钱 | 转发前注入 `cache_control`（`proxy/cache_injector.rs`） |
| 上游凭据怎么带 | `AuthInfo { api_key, strategy, access_token }` **策略枚举** + `masked_key()` 前 4 后 4 打码给日志（`proxy/providers/auth.rs`） |

**对照出的我方位置**：

- **主路不校验调用方**：`proxy/server.rs` 的 `build_router` 只有 `.route()`、**无中间件**（`proxy/mod.rs` 是纯声明）；唯一校验 `validate_claude_desktop_gateway_auth` 只挂在 Claude Desktop 那两个 handler 上（`proxy/handlers.rs:251-278`、`:401-422`）。→ 我们的**逐请求代理密钥**相当于把它只给 Desktop 做的那件事推广到了主路（RISK-08 的外部对照）。
- **Key 是明文存的**：schema 里 provider 整份（含 `ANTHROPIC_AUTH_TOKEN`）就在 `providers.settings_config TEXT` 单列，**没有**保护器/密文/版本列；`database/dao/providers.rs`（1013 行）搜 `encrypt|decrypt|base64|cipher|obfuscat` **零命中**，读写就是 `row.get` + `serde_json`。→ 对照我们的 DPAPI + `protector_id` / `key_version`（RISK-01 的单向门）。
- 我方已知空缺（已记进 `violations.csv`）：**RISK-19** 切上游后历史签名/密文失效（无整流层）、**RISK-20** 上游把思考内联进正文（无剥离）。

**未定论（别当结论）**：① 加解密是否藏在 `store.rs` / `commands/*` 那一层（**未读**）；② 它的数据会同步到 WebDAV / S3（`services/webdav_sync.rs`、`s3_sync.rs`、`sync_protocol.rs`）—— 明文 Key 在库里、库又会同步，**会不会跟着出门、载荷有没有单独加密，未查**。

**方法提醒**：`gh search code` 里带 `::` 或 `path:` 的查询在这台机器上会返回**假空结果**（我连踩两次），别把空结果当证据；用单 token 搜索或直接 `gh api` 取原文。

### 10.1 补读的模块（同日第二/三批，共 9 个）

| 模块（`src-tauri/src/...`） | 它解决的 |
| --- | --- |
| `proxy/providers/claude.rs`（2833 行） | 所谓"透传"其实是**透传 + 逐家规范化**：`normalize_anthropic_tool_thinking_history_for_provider`（按上游规范化工具/思考历史 —— 与 RISK-19 同源）、`normalize_deepseek_thinking_disabled_strip_effort`（DeepSeek 关思考时 strip 掉 effort）、`normalize_anthropic_messages_for_provider` |
| `proxy/providers/streaming_codex_anthropic.rs`（1329 行） | **每对协议一个流式转换器**（4 客户端 × 4 上游格式 → 十几个文件、每个 1000+ 行）；本文件是 `streaming_responses.rs` 的反向 |
| `proxy/providers/transform_codex_anthropic.rs`（3106 行） | 一对协议的完整转换；**`encode_anthropic_thinking_block` / `decode_anthropic_thinking_block`**（把 thinking 块编码成不透明字符串运过去、客户端回放时解回）、`effort_to_thinking_budget` |
| `live/engine.rs`（321 行） | **改客户端文件的事务**：hash 先行 → 解析失败即停 → 内存里改 → 序列化 → 写临时文件 → **发布前字节级一次性备份** → **重读比对 hash、没变才 rename**；乐观锁（自家写入方共用锁），并写明外部进程不参与锁、**rename 前仍有极小窗口** |
| `model_capabilities.rs`（291 行） | **模型能力表**：`is_confirmed_text_only_model`、`resolve_image_input_capability` —— 发之前先知道自己能不能发 |
| `json_canonical.rs`（190 行） | 为**缓存命中**规范化 JSON（`canonicalize_tool_arguments_str` 等，键序一抖缓存就不命中）+ 短哈希 `short_sha256_hex` |
| `handler_config.rs`（228 行） | 每家一份 `*_PARSER_CONFIG` / `*_HANDLER_CONFIG` 常量 + `*_usage_event_filter(data)`（"这一行是不是 usage 事件"） |
| `proxy/sse.rs`（379 行，大半是测试） | **一份 SSE 边界清单**：LF / CRLF / 混合行尾且跨 chunk、UTF-8 多字节被 chunk 切开、空 chunk、`data:` 后可选空格、非法字节立即放行不累积、**超长残留有防御性上限** |
| `usage/calculator.rs`（314 行） | 成本计算用**高精度 Decimal**（避免浮点误差），配 `model_pricing` 表 |

**这次对照新增的我方空缺**（**未记 CSV，等你定**）：

- **SSE 超长残留无上限**：我们用 `ReadLineAsync` 逐行读，上游若长时间不发换行就会无上限地堆缓冲（A3 那道"非事件流就拒绝"只挡住了最常见的一种）。它有 `defensive_guard_flushes_oversized_remainder`。
- **发送前没有模型能力判断**：我们无条件把图片翻成 `image_url`，上游模型若是纯文本就会 400；它有纯文本模型判定与图片能力表。
- **函数参数"迟到"**：结束事件先到、真参数随后才到（MiniMax 实测 2026-10-03）；我们的会话在 `finish_reason` 就关块收尾，会在真参数到达前收工。它扣住空参数的结束事件等真参数。
