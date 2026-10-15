# 架构理解 —— AI 本地代理网关（Windows WPF）

- **版本**：2026-10-15 修订版。第一版由上一个模型在 2026-10-14 产出；本版按 2026-10-15 的 review 与你当场选定的四项决策重写。旧版全文在 git `cc01023`。
- **真相源**：`ai-gateway.structurizr.dsl`（2026-10-15 用 Structurizr CLI v2025.11.09 解析通过并导出三视图；旧版在第 229 行就解析失败，从未能打开过。**S13 改动之后尚未重新校验**——CLI 下载被会话策略拦下，命令见 README「环境与工具」）。
- **状态**：**M1 达成（2026-10-15，计划 S1–S10）**：真 Claude Code 经网关直通 DeepSeek，对话、工具调用、thinking、图片、中止、一键切换、吊销都已实测，记录见 `evidence.md` §7；运行日志页与发布形态（依赖框架）也已落地。**M2（OpenAI 翻译：请求方向 + 非流式 + 流式）已实现并测试（S11/S12），真机验收未做**；S13 文档迁移已做（落地位置见 `evidence.md` §8）。
- **一句话范围**：Claude Code 指向本机 `127.0.0.1:端口`；网关验代理密钥，把请求交给当前激活的上游——上游原生说 Anthropic 协议就**直通**，只说 OpenAI 协议就**翻译**；真实 Key 只以 DPAPI 密文存在本机 JSON 里。

## 1. 决策基线

| # | 决策 | 来源 |
| --- | --- | --- |
| D1 | **只收 Anthropic 入站**（`/v1/messages`）；出站分两条：Anthropic 兼容上游**直通**，OpenAI 上游**翻译** | 原话"`/v1/messages → /v1/chat/completions`"（单向）；2026-10-15 review 后选定。**修订**了第一轮的"双向矩阵" |
| D2 | **JSON 配置文件**，Key 字段 DPAPI 加密；运行日志**只在内存**，重启清空 | 原话"SQLite 或 JSON 加密存储"；10-15 选定。**修订**了第一轮的"SQLite 单库" |
| D3 | UI 与 Kestrel **同进程**，唯一可部署单元 `AiGateway.App.exe`；**托盘常驻**是它的必然要求 | 第一轮选定；托盘为 review 补 |
| D4 | 翻译路径只保证 `tool_use`；**直通路径原样携带一切**（含 thinking） | 第一轮选定；适用范围 10-15 收窄 |
| D5 | 轻量、极简、纯本地；UI 只有：Provider/模型管理、代理密钥管理、一键切换、运行日志 | 第二轮原话 |
| D6 | 代理密钥：客户端只持可吊销的代理密钥，网关在内存里换成真实 Key | 第二轮原话；**防御理由已更正**，见 §5 |
| D7 | 路由只来自全局激活档，代理密钥不含路由（代价：多客户端不能同时用不同模型） | 第二轮选定 |
| D8 | 代理密钥只存 SHA-256，生成后不可回显（代价：忘了只能重发） | 第二轮选定 |
| D9 | 监听地址写死 `127.0.0.1`，只有端口可改 | 第二轮选定 |
| D10 | 鉴权失败只做 `FixedTimeEquals`，无计数 / 退避 / 锁定 | 第二轮选定 |
| D11 | **3 个产品程序集 + 1 个测试项目**；各库默认 `internal`，只公开一个注册方法 | 10-15 选定 |
| D12 | 注入器是 Security 里的 `DelegatingHandler`，由组合根挂进上游 HttpClient 管道，**无人可调用** | 10-15 选定（"H"） |
| D13 | `IRealKeyVault` **只写**（Save / Delete / Exists），签名上拿不到明文 | 10-15 review |
| D14 | 第一个里程碑 M1 = **DeepSeek 直通**，零协议翻译；OpenAI 翻译是 M2 | 10-15 选定 |
| D15 | **不做开机自启**：要开机跑就自己把快捷方式丢进 `shell:startup` | 10-15 你定（S10） |
| D16 | 发布形态 = **依赖框架** win-x64（0.37 MB / 9 文件）；代价是目标机要装 .NET 8 的 Desktop 与 ASP.NET Core 两个运行时 | 10-15 实测四种产物后你选定（S10） |

**为什么第一版被大改**（详见 `evidence.md` §3）：
1. 第一轮"（推荐）"的 D1 双向矩阵、D2 SQLite 都**比你的原话更重**；"轻量"原则第二轮才出现，D1/D2 从没按它复核。
2. 漏了"直通"：你点名的三个上游里，Claude 与 DeepSeek 都原生说 Anthropic 协议，走规范模型纯属损失（多写代码，还丢 thinking 与 beta 头）。
3. 结构重量与原则不符：10 个程序集、19 个组件、9 条禁令；S1 骨架实际撞出两处程序集环和一处传递引用泄露。
4. 两条事实写错：`count_tokens` 并非缺了就报错；代理密钥的防御理由不对（见 §5）。

## 2. 不做清单（闭合集合，出现即范围漂移）

| 拒绝项 | 理由 |
| --- | --- |
| 统计图表 / 用量看板 / 成本核算 | 与"轻量"冲突 |
| 多用户 / 注册登录 / 云同步 | 单机单人；DPAPI 与跨机同步天然冲突 |
| 请求体 / 响应内容落盘 | 只做代理不做审计 |
| 故障转移 / 多 Key 轮转 / 自动重试 | 上游错误原样透传，由你决定 |
| 插件 / 脚本 / 自定义转换规则 | 写死比可插拔便宜 |
| **OpenAI 协议的入站客户端**（Cursor 等） | D1 修订；真要时再加 |
| **改写用户的 `settings.json`** | 只给可复制的配置片段 |
| 每密钥独立路由（D7）、局域网访问与失败锁定（D9/D10） | 见决策表 |
| OAuth / Claude 订阅接入 | 未提 |

## 3. 外部事实（2026-10-15 当场核实）

| 事实 | 出处 | 影响 |
| --- | --- | --- |
| 网关必须转发 `anthropic-version`（去掉请求会坏）与 `anthropic-beta`（去掉可能 400） | https://code.claude.com/docs/en/llm-gateway-protocol | 直通必须带头；规范模型路线会天然丢头 |
| 推理请求发往 `/v1/messages?beta=true` | 同上 | 路由要容忍并转发查询串 |
| `/v1/messages/count_tokens` **可选**，缺失时 Claude Code "fall back to a character-based estimate" | 同上 | 关闭 OPEN-04；旧 RISK-04 的"缺了就报错"是错的 |
| `ANTHROPIC_AUTH_TOKEN` → `Authorization`；`ANTHROPIC_API_KEY` → `x-api-key` | 同上 | 鉴权两种头都要认 |
| DeepSeek 提供 `https://api.deepseek.com/anthropic`：支持 thinking / tools / tool_choice / 图片；忽略 `anthropic-beta`、`cache_control`；未知模型名映射到默认模型 | https://api-docs.deepseek.com/guides/anthropic_api | M1 可以零翻译 |
| DeepSeek 现在的模型名是 `deepseek-flash` 与 `deepseek-v4-pro`（S8 当天核实；旧名 `deepseek-chat` / `deepseek-reasoner` 已过时） | https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code | 上游配置里填这两个名字 |
| Claude Code 对 5xx 与 401 会重试约 3 分钟才显示错误，除非响应带 `x-should-retry: false` | 本机实测（`evidence.md` §7） | 网关自己的配置类错误都要带这个头，否则配错时要干等 3 分钟 |
| 同类项目 cc-switch（14.4 万星，Windows 原生）已有本机路由、格式转换、占位密钥、热切换、故障转移；claude-code-router（3.8 万星）专做协议翻译 | https://github.com/farion1231/cc-switch （README 第 344、363、484 行）· https://github.com/musistudio/claude-code-router | 本项目的差异只剩脊柱②（密钥安全）与极简，力气应花在那里 |

⚠️ 上表前四行来自对官方页面的抓取摘要，不是逐字引文；要核对就打开第一个网址看 "endpoints" 与 "headers" 两节。cc-switch 是否加密存 Key、它的本机路由校不校验占位密钥，**都未核实**（README 没写，要看其源码与 `docs/user-manual/en/4-proxy/`）。

## 4. 结构

**L1**：使用者是你；入站客户端只有 Claude Code；上游分 Anthropic 兼容与 OpenAI 两类；外部能力是 Windows DPAPI；**威胁方是"本机不受信来源"**（浏览器网页、其他本机程序）。

**L2**：一个可部署单元 `AiGateway.App.exe`，加 `%LOCALAPPDATA%` 下的 `config.json`。

**L3：两条脊柱**

脊柱① 协议——入站只有 Anthropic，出站按上游分叉：

```
/v1/messages ─► 鉴权 ─► 路由(激活快照) ─┬─► 直通：只改 model，转发 anthropic-* 头与查询串，SSE 字节原样回流   (M1)
                                         └─► 翻译：Anthropic → chat/completions，chunk → Anthropic 事件       (M2)
```

脊柱② 密钥——代理密钥只决定"能不能进"，真实 Key 只在一处解密、只沿一条边离开进程：

```
代理密钥 ─► SHA-256 ─► 内存快照比对        （AiGateway，看不见 Security）
上游 HttpClient 管道 ─► 注入处理器 ─► DPAPI 解密 ─► 写鉴权头 ─► 上游   （AiGateway.Security，无人可调用）
```

**15 个组件**（按程序集）：

| 程序集 | 组件 |
| --- | --- |
| AiGateway.App | 组合根与宿主（含托盘、单实例）、WPF 界面、ViewModels |
| AiGateway | 本地 HTTP 入口、代理密钥验证器、激活快照、路由解析、直通转发器、Anthropic→OpenAI 翻译器 (M2)、工具调用增量重编码 (M2)、上游 HTTP 客户端、配置存储、运行日志 |
| AiGateway.Security | 真实密钥注入处理器、DPAPI 保护器与只写密钥库 |

为什么只给 Security 一道编译期边界、其余靠 `internal`：见 `dependency-impact-findings.md` §1。

## 5. 安全梯子（D6 / D8 / D9 / D10）

- **代理密钥的真实职责（更正旧版）**：旧版说"监听写死 127.0.0.1 后代理密钥不是网络安全边界"——**错**。浏览器里的任意网页都可能向 `127.0.0.1:端口` 发请求（Chrome 正在收紧本地网络访问，但其他浏览器与旧版本不保证；DNS rebinding 还能读到响应）。高熵代理密钥 + 鉴权正是挡它们的门。理由写错，将来就会有人按错的理由把它"简化"掉。
- **DPAPI 的边界**：CurrentUser 作用域防其他 Windows 用户与离线拿走硬盘，**防不住以你本人身份运行的恶意程序**（它能直接调解密）。
- **裸 SHA-256、无盐、不用慢哈希**仍然正当：代理密钥由程序生成、是高熵随机值，没有离线猜解面。这条理由要留在代码旁，否则将来有人会"升级"成 PBKDF2。
- **吊销即刻生效**（DECIDED-01 修订）：不再"每请求读盘"。UI 与网关同进程，写配置的同时整体替换内存里的哈希快照即可；真正的防线是"吊销后下一个请求必须 401"这条测试。
- **401 只有一种形状**（Anthropic），因为入站只有一种协议。

## 6. 里程碑

- **M1 能用**：Claude Code → 网关 → DeepSeek `/anthropic` 直通。零协议翻译，thinking 原样可用。
- **M2 OpenAI**：加翻译路径，唯一的协议难点是"工具调用增量重编码"（OpenAI 扁平分片 → Anthropic 块生命周期）。**已实现（S11/S12）**：请求方向与两种响应方向都在 `Translation/`，块边界的构造规则见 `findings` §6；只差真机验收。

## 7. 已验证 / 未验证

- ✅ DSL 用 Structurizr CLI v2025.11.09 解析通过并导出 L1/L2/L3（**S13 改动后待重新校验**）；两张 DOT 在 S13 之后重渲无报错（Graphviz 16.1.0，exit 0）。
- ✅ 外部事实见 §3（附网址）。
- ✅ 骨架已按 D11 重做为 3 + 1 个项目（计划 S1）：AiGateway 碰不到 Security 有编译期两道拦截（NU1201、MSB4006）加护栏测试；App 构造 Security 内部类型即 CS0122。
- ✅ DeepSeek 接受 `?beta=true`、支持 `count_tokens`（Claude Code 实际没调用过）、Key 错误时只回显末 4 位（S8 实测，`evidence.md` §7）。
- ✅ 真 Claude Code 2.1.300 经网关直通 DeepSeek：对话、工具调用、thinking、图片、中止、一键切换、吊销后立即 401（S8）。
- ❌ 交互式会话（Esc / Ctrl+C、长会话的上下文压缩）没有自动化覆盖，需要你在自己的终端里用一次。
- ✅ IHttpClientFactory 自带日志**不**给鉴权头打码，`RedactLoggedHeaders` 也只管格式化文本、结构化字段仍是明文（S5 实测）⇒ 上游客户端拿掉全部内置日志处理器，并禁止自动跟随重定向。

## 8. 维护约定

- DSL 是真相源；改完必须过一次 `structurizr validate`（做法见 README）。
- 组件落地后在 DSL 描述里标 `current`，并在 `evidence.md` 补真实 `文件:行`。
- 新增上游时：说 Anthropic 协议的只需新增一条 Provider 配置（走直通）；说其他协议的才需要新翻译器。**若发现要改直通转发器，说明某个上游不是真兼容，应改走翻译。**
