# AI 本地代理网关（Windows WPF）

> 新会话的入口。只交代**到哪了、先读什么、为什么**；架构细节在 `docs/architecture/`。

## 这是什么

C# / .NET 8 WPF 桌面应用。Claude Code 把 `ANTHROPIC_BASE_URL` 指向本机 `127.0.0.1:端口`，网关验过**代理密钥**后转给当前激活的上游：
- 上游原生说 Anthropic 协议（Anthropic、DeepSeek `/anthropic`）→ **直通**，只改 `model` 字段；
- 上游只说 OpenAI 协议 → **翻译**（第二里程碑）。

真实上游 Key 以 **DPAPI 密文**存在本机 `config.json` 里，只在一处解密，只沿一条边离开进程。

**产品定位：轻量、极简、纯本地。** 这是你定的硬约束。

## 现在在哪一步

| 阶段 | 状态 |
| --- | --- |
| 1–3 探索 / 澄清 / 架构 | ✅ **2026-10-15 review 后重写**（第一版出自上一个模型；为什么改见下文与 `evidence.md` §2） |
| 4 实现计划 | ✅ **13 个故事**：`docs/plan/ai-gateway.implementation-plan.json`。M1 = S1–S8（DeepSeek 直通可用），M2 = S11–S12（OpenAI 翻译） |
| 5 编码 | 🟢 **M1 达成（S1–S10）+ M2 全部实现（S11–S12）（2026-10-15）**。真 Claude Code 2.1.300 经网关直通 DeepSeek：对话、工具调用、thinking、图片、中止、一键切换、吊销后立即 401 全部实测通过（记录：`evidence.md` §7）。已有：3 + 1 个项目；`config.json` + DPAPI 只写密钥库；只监听 `127.0.0.1` 的宿主（单实例、托盘常驻）；代理密钥（签发 / 鉴权 / 即刻吊销）；真实 Key 注入（不跟随重定向、零日志、发完即删）；直通转发（只改 `model`、头白名单、真流式、中止传播）；最小 UI（上游与 Key / 代理密钥 / 运行日志 / 设置 + 顶栏一键切换）；运行日志（500 行内存环形缓冲，一行一请求，只拉取）；**发布形态选定为依赖框架**（0.37 MB / 9 文件，一条命令，见 `docs/install.md`），不做开机自启。**S11 已加 OpenAI 上游的翻译路径（请求方向 + 非流式响应）**：Anthropic 请求 → `chat/completions`（system / 文本 / 图片 / tool_use / tool_result / tools / tool_choice / stop_sequences，`stream` 时带 `stream_options.include_usage`，thinking 丢弃 [D4]），非流式响应译回 Anthropic message，翻译路径上 `count_tokens` 直接 404；入口按上游类型分派（Anthropic 走直通、OpenAI 走翻译），上游是 OpenAI 时真实 Key 以 `Authorization: Bearer` 注入。**S12 完成流式**：OpenAI chunk → Anthropic 事件序列（`message_start` / `content_block_start|delta|stop` / `message_delta` / `message_stop`），工具调用的扁平分片按上游 `index` 累积、**块索引严格配对**，名称到齐才开块，收尾补齐所有块关闭，usage 从末尾分片合成进 `message_delta`。**144 个测试全绿**（含 S11/S12 自检 review 的修补：解析兜底、首字节语义、护栏⑪覆盖两个转发器、非事件流的 200 拒绝为 502）。**M1 + M2 代码都已完成**；**S13 文档迁移已做**——DSL 里已落地组件标 `current`（去掉 `Milestone2`）、`evidence.md` 补 §8 组件→`文件:行`、`violations.csv` 按实测关掉 M2 相关的 RISK-03/05 并更新 RISK-06、两张 DOT 重渲通过。**M2 真机验收已通过（2026-10-15，`evidence.md` §9）**：真 Claude Code → 网关 → MiMo（OpenAI 协议走翻译路径，模型 `mimo-v2.6-pro`），工具调用往返成功（Write + Read、2 个 `tool_use` + 2 个 `tool_result`），usage 非 0（input 62499 / output 296），RISK-03 据此关闭。**S14 做了"傻瓜式"**：顶栏主状态徽标（未激活时整块变告警色）+ 代理密钥可用把数、上游为空时给三步引导（不摆空表格）、**一键预设厂家**（只收厂家本体 6 条；地址只写核实过的、未核实的明标；模型名刻意不预填）、两个只读诊断按钮（测试连接 / 拉取模型）。163 测试全绿；真 exe 上用 UIA 只读走查核到 XAML 绑定真的解析了（徽标与代理密钥行都显示实测值）。**唯一没做的是 `structurizr validate`**：下载 CLI 被本会话策略拦下（「下载并执行未知代码」），命令在「环境与工具」一节，请你跑一次 |

## 先读这些（按顺序）

1. `docs/architecture/ai-gateway.architecture-understanding.md` —— 决策 D1–D16、不做清单、外部事实（带网址）、两条脊柱、安全梯子。
2. `docs/architecture/ai-gateway.structurizr.dsl` —— 真相源，L1/L2/L3 三视图，15 个组件。2026-10-15 用 Structurizr CLI v2025.11.09 解析通过；**S13 的改动之后还没重新校验**（CLI 下载被本会话策略拦下）——但改动本身过了结构自检：花括号配平、描述里无尖括号、无死样式、无 `Milestone2` 残留。请按「环境与工具」里的命令跑一次 `validate`。
3. `docs/architecture/ai-gateway.dependency-impact-findings.md` —— 为什么是 3 + 1 个项目、十一条护栏、S1 实测教训、两扇后门、UI 层的守法、变更影响。
4. `docs/architecture/ai-gateway.request-flow.dot` —— 一次请求：鉴权 → 路由 → 直通 / 翻译 → 注入 → 上游。
5. `docs/architecture/ai-gateway.dependencies.dot` —— 程序集引用、运行时装配、禁止边。
6. `docs/architecture/ai-gateway.evidence.md` —— 每条决策出自谁、哪一轮；外部证据出处。**别把我的推断当成你的决定。**
7. `docs/architecture/ai-gateway.violations.csv` —— 规则、风险、开放项（含已退役 / 已关闭的，留作来历）。

## 已定决策（要改先说代价）

| # | 决策 |
| --- | --- |
| D1 | 只收 Anthropic 入站；出站：Anthropic 兼容上游直通，OpenAI 上游翻译 *(10-15 修订)* |
| D2 | JSON 配置 + DPAPI 加密字段；运行日志只在内存 *(10-15 修订)* |
| D3 | UI 与 Kestrel 同进程，唯一可部署单元；托盘常驻 |
| D4 | 翻译路径只保证 tool_use；直通原样携带一切 |
| D5 | 轻量极简纯本地；UI 只有 Provider/模型、代理密钥、一键切换、运行日志 |
| D6 | 客户端只持可吊销代理密钥，网关在内存里换成真实 Key（挡的是本机网页与其他程序） |
| D7 | 路由只来自全局激活档（多客户端不能同时用不同模型） |
| D8 | 代理密钥只存 SHA-256，不可回显 |
| D9 | 监听写死 127.0.0.1，只有端口可改 |
| D10 | 鉴权失败只做 FixedTimeEquals，无计数 / 退避 / 锁定 |
| D11 | 3 个产品程序集 + 1 个测试项目；库默认 internal *(10-15)* |
| D12 | 注入器是 DelegatingHandler，组合根挂进上游 HttpClient 管道，无人可调用 *(10-15)* |
| D13 | `IRealKeyVault` 只写 *(10-15)* |
| D14 | M1 = DeepSeek 直通，零协议翻译 *(10-15)* |
| D15 | **不做开机自启**（要开机跑就自己把快捷方式丢进 `shell:startup`）*(10-15)* |
| D16 | 发布形态 = **依赖框架**（win-x64，0.37 MB / 9 文件）；代价：目标机要装 .NET 8 的 Desktop 与 ASP.NET Core 两个运行时 *(10-15 实测四种产物后选定)* |

## 2026-10-15 review 改了什么（一段话）

第一版里单个决定都轻，整体结构却很重：第一轮标"（推荐）"的 D1 双向矩阵、D2 SQLite 都比你的原话（单向、"SQLite 或 JSON"）更重，而"轻量"原则第二轮才给、D1/D2 从没回头复核；它漏了"直通"（你点名的三个上游里两家原生说 Anthropic 协议）；Claude Code 官方网关协议（要求转发 anthropic-* 头、`count_tokens` 可选）它没查，RISK-04 写反了；代理密钥的防御理由写错（回环挡不住本机浏览器里的网页）；真相源 DSL 第 229 行就解析失败，从未能打开。旧版全文在 git `cc01023`。

## 环境与工具（已就绪）

- .NET SDK `8.0.426`（`global.json` 钉住）；Graphviz `16.1.0`（`C:\Program Files\Graphviz\bin\dot.exe`）。
- git：仓库内 `user.email` = GitHub noreply；远程 `sonder123i/ai-gateway`（**private**），push 只走 `ssh://git@ssh.github.com:443/...`。
- 校验 DSL：`gh release download v2025.11.09 -R structurizr/cli -p structurizr-cli.zip`，解压后
  `java -cp "cli/lib/*" com.structurizr.cli.StructurizrCliApplication validate -workspace docs/architecture/ai-gateway.structurizr.dsl`
  （本机 JDK 25 可跑；CLI 94MB，用完即删，不进仓库）。DSL 描述里别写尖括号（`<port>` 这类会让导出的 DOT 渲染失败）。
- **Claude Code 2.1.300 已用 npm 全局装好**。接网关：在网关的「代理密钥」页生成一把，把弹出的配置片段合并进 `~/.claude/settings.json`；DeepSeek 上游的地址填 `https://api.deepseek.com/anthropic`，模型填 `deepseek-flash, deepseek-v4-pro`（DeepSeek 已改名，旧的 `deepseek-chat` / `deepseek-reasoner` 别再用）。
- C 盘 90% 满（剩约 26G）；E 盘充裕。
- 发布：`docs/install.md`（依赖框架，一条 `dotnet publish -p:PublishProfile=Release-win-x64`；本机已具备 8.0 的三个共享框架）。

## 仍然开着

- 交互式会话（Esc / Ctrl+C 中止、长会话的上下文压缩）没有自动化覆盖——S8 只能用 `-p` 模式跑，需要你自己用一次。
- 仓库原定 M1 跑通后再考虑公开；M1 已达成，公不公开、什么时候公开由你定（目前仍是 private）。
- **OpenAI 上游怎么填**：类型选「OpenAI」、地址填 API 根（网关在其后追加 `/chat/completions`；例如 DeepSeek 填 `https://api.deepseek.com`、OpenAI 填 `https://api.openai.com/v1`、**小米 MiMo 填 `https://api.xiaomimimo.com/v1`**）；真实 Key 以 `Authorization: Bearer` 注入，不是 `x-api-key`。MiMo 的 `mimo-v2.6-pro` 已实测通过（§9）。
- **M2 真机验收：已通过（2026-10-15）** —— 真 Claude Code → 网关 → MiMo（OpenAI 协议 ⇒ 翻译路径），工具调用往返成功，记录与数字见 `evidence.md` §9。仍然开着两件：① **翻译路径的客户端中止没有专题测试**（`violations.csv` 的 RISK-06 如实标着；要补就照搬 S6 那条挂断测试）；② 真机上的**交互式会话**（Esc / Ctrl+C、上下文压缩）一直需要人工跑。
- **S14 那两个诊断按钮还等你点一次**：真 exe 上「测试连接」与「拉取模型」我只验到"按钮在界面树里、绑定生效"，**没代你点**（UIA 点击不稳）。你在界面上点一下：测试连接该报「地址可达：HTTP …，往返 … ms」；拉取模型对 OpenAI 类上游该把模型名填进「模型」框。
