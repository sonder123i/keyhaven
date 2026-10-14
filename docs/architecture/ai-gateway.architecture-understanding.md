# C4 架构理解 —— AI 本地代理网关（Windows WPF）

- 技能链：`c4model`（结构语义）+ `dependency-impact-analyzer` + `graphviz`（依赖图）
- 模型源文件（真相源）：`docs/architecture/ai-gateway.structurizr.dsl`
- 视图：`SystemContext`（L1）、`Containers`（L2）、`Components`（L3，容器 `AiGateway.App.exe`）
- **视图状态：target-state（目标架构）。工作区为空，无任何代码/配置证据。所有节点的 `state=target`，置信度记录在 `ai-gateway.evidence.md`。请勿把本模型当作已验证的现状。**
- 读者：solo 开发者本人 + 未来接手的工程视角
- 一句话范围：单机桌面应用，在本机 `127.0.0.1:8000` 暴露一个 Anthropic ⇄ OpenAI 双向协议转换代理，Key 用 Windows DPAPI 加密存 SQLite，UI 一键切换激活模型。

## 需求基线（已由澄清题确认）

| # | 决策 | 选定 | 对架构的直接后果 |
| --- | --- | --- | --- |
| D1 | 协议转换范围 | **双向矩阵 Anthropic + OpenAI** | 用「入站协议 → 规范模型 → 出站协议」两段式，保持 2+2 个 codec 而非 4 个专用转换器 |
| D2 | 存储选型 | **DPAPI + SQLite 单库** | Provider/Model/密文 Key/路由档同库；加密边界收在 `AiGateway.Security`，数据访问收在 `AiGateway.Storage` |
| D3 | 进程形态 | **同进程内嵌 Kestrel** | 部署单元只有一个 `AiGateway.App.exe`；UI 与网关共享内存态 → 激活切换无需 IPC，但也**没有进程边界保护**（见 L3 的 `ActivationState`） |
| D4 | MVP 能力 | **tool_use / function calling 双向** | 故障转移出局；**thinking/reasoning 透传出局**；请求日志按 D5 以"基础运行日志"形态回归 MVP，但**统计与成本核算出局** |
| D5 | 产品定位 | **轻量、极简、纯本地**；UI 只保留四类核心：模型管理、代理密钥管理、一键切换、基础运行日志 | 功能范围由"闭合的不做清单"界定，而非逐条规则；`Stripped` 标签即此意 |
| D6 | 入站鉴权模型 | **代理密钥机制**：对外发放可撤销的虚拟密钥；网关验证通过后在**内存中**替换为真实 API Key 再转发 | 入站区从"无鉴权本机区"变成"持凭证区"；真实 Key 的暴露面被压缩到单一节点（见 L3 第②条脊柱） |
| D7 | 代理密钥与路由的关系 | **路由全部来自全局激活档，密钥不含路由信息** | `routeResolver` 只读 `ActivationState`；**删掉 2 条编译期边**。代价：所有入站客户端共享同一个当前模型，**不能同时用不同模型** |
| D8 | 代理密钥存储形态 | **只存不可逆 SHA-256，生成后不可回显** | 代理密钥路径上没有解密调用；`Security` 职责不扩大。代价：忘了只能重新生成并重填所有客户端 |
| D9 | 监听范围 | **地址写死 `127.0.0.1`，只有端口可在 UI 改** | 监听不再是可变配置，无 LAN 开关；`RISK-02` 降为低 |
| D10 | 鉴权失败防护 | **只做定时安全比较**，无失败计数、无退避、无锁定 | `ProxyKeyAuth` **零可变状态** → 天然无状态 Singleton；吊销因无缓存而天然即刻生效 |

## 产品定位与「不做清单」（闭合集合）

设计原则是**先定边界闭合，再谈功能**。以下能力被**明确拒绝**，不是"还没做"，而是"不打算做"。它们不出现在任何视图里，出现即为范围漂移：

| 拒绝项 | 拒绝理由 |
| --- | --- |
| 统计图表 / 用量看板 / 成本核算 | 与"轻量"直接冲突；一行日志能回答的问题不需要一张图 |
| 多用户 / 注册 / 登录体系 | 单机桌面应用，操作者只有一个人 |
| 云端同步 / 配置托管 / 账号互通 | 纯本地是前提；且与 DPAPI 的"同机同用户"绑定天然冲突 |
| 请求体 / 响应内容落盘 | 只做代理不做审计；也是日志体积与隐私的上界 |
| 上游多 Key 轮转 / 自动重试 / 降级 | D4 出局；429 原样透传，让用户自己决定 |
| 插件系统 / 脚本引擎 / 自定义转换规则 | 协议对就两个，写死比可插拔便宜 |
| OAuth / Claude Pro-Max 订阅接入 | 未提，未画成通路 |
| **每密钥独立模型路由**（按 D7 排除） | 会删不掉 `RequestContext` 的路由字段、让鉴权器重新查库，等于把刚删的 2 条边加回来；与「一键切换」的全局语义冲突 |
| **局域网访问 / 失败退避锁定**（按 D9+D10 排除） | 地址写死回环后没有对外攻击面；为它建计数、配置项与解锁路径，只服务于一个不存在的威胁 |

**"代理密钥"本身不是这条原则的例外** —— 它不是新功能，而是将"真实 Key 不外泄"从依赖端口不暴露，变成依赖一个可撤销的凭证。**但 D7+D8+D9 合起来把它的定位钉低了，要说准：在这个设计里代理密钥不是网络安全边界，它是「可撤销性 + 防误操作」机制** —— 保证真实 Key 永远不出现在客户端配置文件里，并能一键收走某个客户端的访问权。这是运维价值，不是防御纵深。将来若有人加 LAN 开关，爆破风险与"无缓存"这条不变式必须重评，不能沿用今天的结论。

## D4 的后果必须说清（不要靠画图掩盖）

你只勾了 `tool_use`。于是三件事明确不在首期：

1. **thinking / reasoning 流式透传** —— 但 Claude Code 的请求体里**会带 `thinking` 字段**，这不是我们能选择不存在的。所以首期必须做一个显式动作：**在 `AnthropicCodec` 解析入站请求时，把 `thinking` 配置识别出来并降级（丢弃或透传给原生 Anthropic Provider）**，而不是让它无声地流进规范模型。DSL 里我已把 `anthropicCodec` 的描述从"thinking_delta 渲染"改为不含 thinking，并在 evidence 里登记为待决项 `OPEN-01`。
2. **统计图表 / token 用量看板 / 成本核算** —— 出局（D5 不做清单）。但注意：基础运行日志**回归 MVP** 了，它只到"一行一请求"为止。而更关键的是，Anthropic 的 `message_delta` 事件**结构上要求携带 `usage`** —— 这与"要不要做用量统计"是两回事：即使一行统计都不做，也必须合成一个 usage 对象，否则事件序列不合法。这条容易被"统计出局"误伤，`RISK-03` 已登记。
3. **故障转移 / 多 Key 轮转 / 重试** —— 出局，且已进不做清单。首个版本遇到 429 就把上游错误状态码按 Anthropic 错误体形状透传回 Claude Code。可接受。

## L1 System Context：边界与信任

```
[Claude Code CLI] ──Anthropic/SSE──┐
                                   ├──► 【AI 本地代理网关】 ──HTTPS──► [OpenAI 兼容 API: openai / deepseek …]
[OpenAI 协议客户端] ──chat/completions─┘                        └────► [Anthropic API]
                                   ▲                                    ▲
[桌面操作者] ──鼠标键盘──► 【本机 UI】                              【Windows DPAPI】
```

- **信任边界要显式命名，且它不在你以为的地方。** 按 D6，客户端侧流动的**只有代理密钥**（可撤销、可轮换、泄露代价有限）；真实 Key 明文只存在于 `credentialInjector` 所在的**特权区**。所以本系统真正的边界不是"本机 vs 网络"，而是**`credentialInjector` 这一个节点** —— 它左边是代理密钥，右边是真实 Key。图里两个 cluster（客户端可达区 / 特权区）就是按这条线切的。
- 这条边界之所以仍然危险，是因为**鉴权与注入在同一个进程里**：一旦运行日志、异常消息或上游错误透传里带出真实 Key，边界的价值就归零。所以 DSL 与 DOT 里把它写成四条可被测试拦住的禁止边（`FORBIDDEN-06/07/08/09`），而不是一句"注意安全"。
- UI 里的一次误点（比如"导出配置"）同样可能把明文 Key 送出 —— **导出配置**这类功能本身就在不做清单里。
- 被标注为 `LowConfidence` 的节点：`OpenAI 协议客户端`。D1 选双向矩阵，但**没有确认证据表明首期真有非 Claude Code 的入站客户端**。它是被 D1 的选型带进来的，不是被需求带进来的。
- 未知区（未画成完整通路）：OAuth/订阅制接入（Claude Pro/Max）、局域网多机共享。这两项按 `avoid` 原则**不画 happy path**。

## L2 Containers：为什么只有一个可部署容器

D3 选了同进程内嵌，所以 **C4 的"容器 = 可独立部署/执行单元"只有一条**：`AiGateway.App.exe`。

WPF UI 与 Kestrel **不是两个容器**，它们是同一个容器里的两组组件。把它们画成两个容器会是错的 —— 它们永远一起构建、一起启动、一起崩溃，没有独立生命周期。这个判断值得留在文档里，因为后面若有人提议拆 sidecar，依据就在这里：**拆分的唯一收益是崩溃隔离与可独立重启，代价是把 `ActivationState` 从内存共享变成 IPC**（`§5.4` 影响半径表里的大改动项）。

第二个容器是数据仓 `gateway.db`（SQLite 文件，`tags Database`）。

外部系统：`Windows DPAPI`（OS 能力，不是我们的代码）、`OpenAI 兼容远端 API`、`Anthropic API`。

## L3 Components：两条不许打折的脊柱

`AiGateway.App.exe` 内 **19 个组件**（18 个 MVP + 1 个 Phase 2 `failoverPolicy`）。数量以真相源 `.structurizr.dsl` 为准：`grep -c '= component'` → 19，逐个为 `compositionRoot · wpfViews · viewModels · httpEndpoints · proxyKeyAuthenticator · httpListenerPolicy · credentialInjector · routeResolver · anthropicCodec · openAiCodec · canonicalModel · toolCallBridge · streamRelay · upstreamClients · activationState · secretProtector · repositories · runLog · failoverPolicy`。读这张图要抓住**两条**脊柱：

**脊柱①（协议）—— 不许打折：**

```
HttpEndpoints
   ├─► AnthropicCodec ─┐
   ├─► OpenAiCodec ────┼──► CanonicalModel ──► UpstreamClients ──► StreamRelay ──► (回到 HttpEndpoints 写出)
                       ┘         ▲
                          ToolCallBridge
```

**`AnthropicCodec` 与 `OpenAiCodec` 之间没有任何边，且永远不许有。** 两条边都指向 `CanonicalModel`。这是把「N×M 专用转换器」压回「N+M 适配器」的全部机制，也是本模型里第一条**结构上不可协商**的规则（`FORBIDDEN-01`）。

**脊柱②（密钥）—— D6 新增，同样不许打折：**

```
HttpEndpoints ─► ProxyKeyAuthenticator ─ ─(仅指纹/RouteProfile)─ ─► RouteResolver
                        │                                            │
                   repositories                               CredentialInjector ─► UpstreamClients ─► 远端
                   (取代理密钥哈希)                                    ▲
                                                              secretProtector ─► DPAPI
```

`proxyKeyAuthenticator`（验证）与 `credentialInjector`（替换）**必须是两个组件**，不是一个"代理密钥中间件"。理由不是洁癖：

- 鉴权器有**失败路径**（401、密钥被禁用）。如果它同时也能解密真实 Key，那么失败路径就站在了密钥暴露面上 —— 一句写进错误消息的 debug 文本就能泄露。拆开后，鉴权器**在类型上就拿不到真实 Key**（`FORBIDDEN-07`）。
- 注入点是**唯一**该出现"明文 Key"字面量的地方。合并后你无法用一条 grep 或一条架构测试回答"真实 Key 能被读到哪里"。
- 代理密钥用**单向哈希**存储（校验即可，不可还原），真实 Key 用 **DPAPI 可逆密文**。两种密钥的存储形态不同，正好被这两个组件各自封装。

其余组件的分组理由：

| 组 | 组件 | 为什么这么切 |
| --- | --- | --- |
| 宿主 | `compositionRoot` | 唯一允许"知道所有具体实现"的地方（DI 绑定、Kestrel 启停、单实例 Mutex）。它不稳定是**设计意图** |
| 入站 | `httpEndpoints` | 唯一持有 `HttpContext` 的人：按入站协议选 codec、按 route 选 Provider、负责 Write 与**逐事件 Flush** |
| 鉴权 | `proxyKeyAuthenticator` + `credentialInjector` | 验证与替换拆成两组件，使鉴权器的**失败路径在类型上接触不到真实 Key**；注入点成为全工程唯一出现明文 Key 的地方 |
| 路由 | `routeResolver` | 把请求体的 `model` 字段（可能是 Claude Code 硬编码的模型名）+ 代理密钥绑定的 RouteProfile 解析成真实 (Provider, model)。**不碰任何密钥** |
| 流式 | `streamRelay` | 纯变换：上游帧 → 规范增量 → 入站事件；只做中止传播与半截流终止，**不做 Flush**。**全系统故障聚集点** |
| 映射 | `anthropicCodec` / `openAiCodec` / `canonicalModel` / `toolCallBridge` | 见上；`toolCallBridge` 单独成组件是因为它的复杂度不属于任何一个协议 |
| 出站 | `upstreamClients` | 每个 Provider 的鉴权头形态不同（`Authorization: Bearer` vs `x-api-key:` + `anthropic-version`），差异收在这里；也是真实 Key 的**唯一出口** |
| 共享态 | `activationState` | D3 的必然产物。UI 写、网关读，**必须原子可见** → 不可变快照整体替换 |
| 安全 | `secretProtector` | DPAPI CurrentUser + 每 Provider 独立 entropy（域分离，防止一个 Provider 的密文被当另一个用） |
| 存储 | `repositories` | SQLite CRUD + 建库迁移；真实 Key 密文与代理密钥哈希同库不同形态 |
| 日志 | `runLog` | 一行一请求 + 只记指纹。**刻意不做**统计、成本、请求体、响应内容（`Stripped`）|
| Phase 2 | `failoverPolicy` | 按 D4 出局且进不做清单，用 `dashed true` 样式标出，**不是现状** |

`canonicalModel` 被刻意标为 `NoIo`：它是一个纯 C# record 集合，不引用任何程序集。内核稳定度 `I = 0.00` 就是这么来的。

## 8000 端口与安全梯子（D6 提出，D9/D10 定稿）

原来 `http://localhost:8000` 的安全性**完全靠"只监听回环"** —— 那句话的潜台词是：一旦为了"给远程机用"改成 `0.0.0.0`，同网段任何人都能拿你的 Key 烧钱。代理密钥把保证方式从**依赖网络边界**改为**依赖一个可撤销凭证**，这是本次补充需求里最实质的架构变化。**但 D9 明确决定不因此打开 LAN** —— 凭证带来的可能性被主动留在门外，只保留它的撤销与防误操作价值。

已由 D8/D9/D10 定稿的梯子：

1. **`127.0.0.1` 写死在代码里，UI 不提供局域网开关**（D9）。省掉一个配置面，也省掉"不小心暴露"这个失误面 —— 这正是极简。
2. **端口可在 UI 改，默认 8000**。8000 在开发者机器上是高频冲突端口（`aiohttp` / Jupyter 都占它），所以端口是**必须可变**的那一项，而地址不是。
3. **`/health` 免鉴权**，供 UI 自检"端口活着"。这条顺手把"端口被占"从玄学变成可诊断。
4. **鉴权失败：只做 `FixedTimeEquals`，不做失败计数、不做退避、不做锁定**（D10）。回环场景下没有在线猜解面；不做这几样反而让鉴权器**零可变状态**，白送一个无状态 Singleton。
5. **哈希：裸 SHA-256，无盐、不用慢哈希**（D8）。理由是代理密钥由程序生成、是高熵随机值，不是人写的口令，不存在离线猜解面。**这个理由必须留在文档里** —— 否则将来一定有人把它"升级"成 PBKDF2，并给每次请求的 `enabled` 检查加上一笔派生开销。

仍然成立的三条要求（与监听范围无关，别跟着一起删）：

- **401 的错误体形状**：Anthropic 与 OpenAI 的错误 JSON 结构不同（`{type:"error",error:{type,message}}` vs `{error:{message,type,code}}`）。鉴权失败发生在解析入站协议**之前**，所以按**路径**判形状（`/v1/messages` → Anthropic），不按客户端头判。返回错形状会让 Claude Code 显示成无法理解的报错，而不是"密钥无效"。
- **代理密钥不进日志**：D5 要日志 + D6 有密钥 ⇒ 日志是最可能的泄露面。`FORBIDDEN-06` 要求日志只记前缀指纹。
- **每请求现查哈希，不做缓存**：缓存会把"吊销即刻生效"变成一句假话。SQLite 上一次索引查找的代价，换一条真实的安全语义，这笔账在轻量定位下是划算的。

## 预览与验证

- **可视化检查**：用 Qoder 的 Structurizr DSL viewer 打开 `docs/architecture/ai-gateway.structurizr.dsl`（三视图都在 `views {}` 里）。
- **数据流 / 请求生命周期**：`docs/architecture/ai-gateway.request-flow.dot`（用 DOT viewer 打开）。读法：绿色加粗是主路径；**唯一一条红色粗边是真实 Key 出进程的那一步**，其余任何位置出现明文 Key 都是违规；三个红色节点（401 / 路由失败 / 上游错误）与一条反向控制边（客户端中止 → 取消上游）是**刻意画出来的断头**，不是 happy path 的装饰。
- **依赖与影响半径**：`docs/architecture/ai-gateway.dependencies.dot` + `ai-gateway.dependency-impact-findings.md`。
- **我没能验证的事**（2026-10-14 复核后如实更新）：
  - ~~本机无 Graphviz~~ → **已装 16.1.0，两个 DOT 都已真渲**。`request-flow.dot` 一次通过；`dependencies.dot` 首渲失败（禁止边带 rank 约束成环 → `trouble in init_rank`），加 `constraint=false` 后 stderr 全空。
  - ~~本机无 .NET SDK~~ → **已装 8.0.426**，并用临时 WPF 项目真编译过 `net8.0-windows`（0 警告 0 错误）。
  - **仍然未验证：`.dsl` 至今没被 Structurizr 解析器读过**（本机无 Structurizr CLI，只有 JDK 25 可跑它）。语法只按插件 token 上限规则人工核对。所以 L1/L2/L3 三视图"能打开"这件事还没被证明——第一次打开若空白，**改 DSL 源，别手改导出物**。

## 维护约定

- `.structurizr.dsl` 是真相源；渲染预览与导出是派生物。
- 节点状态迁移规则：某组件的代码落地后，把它从 `target` 改 `current` 并在 evidence 里补 `sourceRefs`（文件:行）。
- 新增协议时，只在 L1 侧加一个 codec 组件 + `compositionRoot` 一条绑定；**若你发现需要动已有 codec，说明架构破了**，这是本模型自带的回归探针。
