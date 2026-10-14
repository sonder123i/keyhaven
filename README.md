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
| 5 编码 | 🟡 **S1–S3 完成（2026-10-15）**：3 + 1 个项目；`config.json` + DPAPI 只写密钥库；**网关能启动了**：`GatewayServer` 只监听 `127.0.0.1:8000`（空宿主构建器，环境变量加不出别的监听）、`GET /health`、端口被占给出可读提示、单实例、关窗隐藏到托盘。24 个测试全绿；真实 exe 已演示启动 / 关窗 / 第二实例被挡。**待你手动验证一项**：右键托盘图标 →「退出」后端口释放。还没有鉴权与转发。**下一步 = S4**（代理密钥：生成、哈希、内存快照鉴权、吊销） |

## 先读这些（按顺序）

1. `docs/architecture/ai-gateway.architecture-understanding.md` —— 决策 D1–D14、不做清单、外部事实（带网址）、两条脊柱、安全梯子。
2. `docs/architecture/ai-gateway.structurizr.dsl` —— 真相源，L1/L2/L3 三视图，15 个组件。**已用 Structurizr CLI 解析通过**。
3. `docs/architecture/ai-gateway.dependency-impact-findings.md` —— 为什么是 3 + 1 个项目、五条护栏、S1 实测教训、两扇后门、变更影响。
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

## 2026-10-15 review 改了什么（一段话）

第一版里单个决定都轻，整体结构却很重：第一轮标"（推荐）"的 D1 双向矩阵、D2 SQLite 都比你的原话（单向、"SQLite 或 JSON"）更重，而"轻量"原则第二轮才给、D1/D2 从没回头复核；它漏了"直通"（你点名的三个上游里两家原生说 Anthropic 协议）；Claude Code 官方网关协议（要求转发 anthropic-* 头、`count_tokens` 可选）它没查，RISK-04 写反了；代理密钥的防御理由写错（回环挡不住本机浏览器里的网页）；真相源 DSL 第 229 行就解析失败，从未能打开。旧版全文在 git `cc01023`。

## 环境与工具（已就绪）

- .NET SDK `8.0.426`（`global.json` 钉住）；Graphviz `16.1.0`（`C:\Program Files\Graphviz\bin\dot.exe`）。
- git：仓库内 `user.email` = GitHub noreply；远程 `sonder123i/ai-gateway`（**private**），push 只走 `ssh://git@ssh.github.com:443/...`。
- 校验 DSL：`gh release download v2025.11.09 -R structurizr/cli -p structurizr-cli.zip`，解压后
  `java -cp "cli/lib/*" com.structurizr.cli.StructurizrCliApplication validate -workspace docs/architecture/ai-gateway.structurizr.dsl`
  （本机 JDK 25 可跑；CLI 94MB，用完即删，不进仓库）。DSL 描述里别写尖括号（`<port>` 这类会让导出的 DOT 渲染失败）。
- **本机没有 Claude Code**：M1 验收（S8）前要 `npm i -g @anthropic-ai/claude-code`，并需要你的 DeepSeek Key。
- C 盘 90% 满（剩约 26G）；E 盘充裕。

## 仍然开着

- `OPEN-11` 发布形态：依赖框架还是自包含——S10 实测数字后由你选。已知（S1 实测 runtimeconfig）：App 需要 **3 个**共享框架（Microsoft.NETCore.App、WindowsDesktop.App、AspNetCore.App），依赖框架发布时目标机要装齐。
- `OPEN-12` DeepSeek 是否接受 `?beta=true`、是否支持 `count_tokens`、错误体是否回显 Key——S8 实测。
- `OPEN-13` IHttpClientFactory 自带日志处理器是否给鉴权头打码——S5 实测。
