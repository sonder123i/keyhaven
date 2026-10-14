# 模块依赖分析与变更影响 —— AI 本地代理网关

- 技能链：`dependency-impact-analyzer` + `graphviz`（图源）+ `c4model`（结构语义）
- 分析粒度：**程序集（.csproj）**。比命名空间粗、比部署单元细，是 C# 里唯一能被编译器强制的耦合单位。
- 状态：**全部 target-state**。工作区无代码，因此没有 import/manifest 证据；下述 fan-in/fan-out 是从设计意图边集算出来的，不是测出来的。
- 图源：`docs/architecture/ai-gateway.dependencies.dot`（`rankdir=TB`，Canvas/DOT viewer 可预览）
- 数据流图：`docs/architecture/ai-gateway.request-flow.dot`（请求生命周期 + 代理密钥替换点）
- 机器可读违规表：`docs/architecture/ai-gateway.violations.csv`
- **本轮补充需求（D5 轻量极简 / D6 代理密钥）落在：** §1 读图顺序、§2 耦合表 + 暴露面量化、§3 类型清单第 4/5 条、§4.4 后门边、§5 两行不可逆项、§6 第二难点、§7 四条新断言、§8 三条新未闭环项

> ⚠️ 本机没有 Graphviz 二进制（`command -v dot` 无输出），DOT 未经 `dot -Tsvg` 实渲验证。已人工核对节点形状与边属性均为合法键，但**布局效果未被证明**。

---

## 1. 读图顺序

1. 先看 `cluster_core`（L0）—— 它是唯一允许被所有层指向的节点。
2. 再看 L1 两个 codec 之间的空白 —— **那里没有边，这个空白就是整套架构的验收标准**。
3. 然后看 `cluster_gateway` 的管道顺序：`HttpEndpoints → ProxyKeyAuth`（先鉴权，且它对 Security **零依赖**）→ `RouteResolver / StreamRelay → Providers`。
4. 看 `CredInjector`：它只有一条来自 `Hosting` 的入边和一条红色的 `→ Providers` 出边 —— **这就是真实 Key 的全部暴露面**。
5. 最后看 `cluster_hosting`：它是唯一拥有 9 条"知道具体实现"出边的节点。
6. 红色虚线边是 `FORBIDDEN-*`（现在共 9 条），**它们不是现状，是需要被测试拦住的禁令**。

> 两张图的分工：`ai-gateway.dependencies.dot` 回答「谁**能**调用谁」（编译期可达性）；`ai-gateway.request-flow.dot` 回答「密钥在哪一步被换掉」（运行时顺序）。代理密钥机制必须两张图都改 —— 只改数据流图会漏掉"鉴权器不该能解密"这条静态约束，只改依赖图会漏掉"401 的错误体形状"这条运行时约束。

## 2. 耦合度（由设计边集计算，非实测）

| 节点 | Ca（传入） | Ce（传出） | I = Ce/(Ca+Ce) | 判读 |
| --- | --- | --- | --- | --- |
| `AiGateway.Core` | **13** | 0 | **0.00** | 最大稳定核。高 Ca + 零 Ce = 抽象应该待的地方，符合稳定抽象原则 |
| `AiGateway.Mapping.Anthropic` | 1（仅 Hosting） | 1 | 0.50 | 被 Hosting 绑定后才可达，正确 |
| `AiGateway.Mapping.OpenAI` | 1 | 1 | 0.50 | 同上 |
| `AiGateway.Gateway :: ProxyKeyAuth` | 1 | 2 | 0.67 | 只被 `HttpEndpoints` 调用；**对 Security 零依赖**（这是 `FORBIDDEN-07` 要保的东西） |
| `AiGateway.Security :: CredInjector` | 1（仅 Hosting） | 3 | **0.75** | 全图唯一"高不稳但零同层依赖"的节点 —— 它只许被装配、不许被别处调用 |
| `AiGateway.Gateway :: StreamRelay` | 1 | 2 | 0.67 | 稳定度尚可，但**故障聚集点**（见 §5） |
| `AiGateway.Gateway :: RunLog` | 1 | 2 | 0.67 | 依赖 Core + Storage，**不依赖 Security**（`FORBIDDEN-06`） |
| `AiGateway.Providers` | 4 | **1** | **0.20** | Ce 3 → 2 → **1**：先是不再解密 Key（D6），再是不查库（D7）。**只剩 Core 一个依赖 = 全图最易单测的一层** |
| `AiGateway.Storage` | **4** | 2 | 0.33 | 传入从 5 降到 4：`Providers` 不再依赖它（D7） |
| `AiGateway.Security` | 3 | 1 | 0.25 | 稳定，合理（能力小、被依赖多） |
| `AiGateway.App.ViewModels` | 2 | 1 | 0.33 | 合理 |
| `AiGateway.Hosting` | **0** | **9** | **1.00** | 最大不稳定 peripheral —— **这是设计意图，不是缺陷** |

两个结论值得明说：

- 这套分层里 **不存在意外热点**。`Core` 稳、`Hosting` 不稳，正是该有的形状。
- **`Hosting` 是唯一持有具体 codec / 注入器类型的地方**。将来若加第二个入口（headless 服务 / CLI），绑定清单会在两处重复从而漂移。→ 阶段 4 前就应把"绑定清单"做成一个可复用扩展方法，而不是内联在 `App.xaml.cs` 里。

### D6（代理密钥）在依赖图上换来的量化收益

补充需求之前，真实 Key 的取用路径是 `upstreamClients → repositories → secretProtector`，也就是说**任何能调用 `upstreamClients` 或读到 `repositories` 的代码，都顺带拥有了拿到明文 Key 的能力**。按扇入计数，那是一个 **3 个调用方**（StreamRelay、Hosting、Failover）的暴露面。

改完之后：

- 真实 Key 明文的出现面 = **1 个节点（`CredInjector`）+ 1 条边（`CredInjector → Providers`）**。
- `Ca(CredInjector) = 1`，且那唯一一条入边来自 `Hosting`（组合根），**没有任何业务组件调用它** —— 它是被"装配"的，不是被"调用"的。
- 鉴权侧彻底断开：`Ca(Security)` 里不再有 Gateway 组件，`ProxyKeyAuth` 对 `Security` 的依赖被 `FORBIDDEN-07` 禁掉。

这条收益可以用一次 grep 验收：**全工程里调用 DPAPI 解密的位置应当恰好为一处**。多于两处，就说明有人开了第二条后门 —— 而这正是"代理密钥"这个设计能否成立的唯一判据。

### D7–D10 的净效果：删边与去状态，不是加层

补充需求听起来像"加一个中间件"，实际这四个选择把图**变小了**：

| 选择 | 净变化 |
| --- | --- |
| D7 路由只来自全局激活档 | **删 2 条编译期边**：`RouteResolver → Storage`、`Providers → Storage`；`RequestContext` 里没有路由字段，鉴权器与路由彻底解耦 |
| D8 只存哈希、不可回显 | `Security` 的职责范围**不变大**（它只服务真实 Key），代理密钥路径上不存在解密调用 |
| D9 地址写死回环 | **删 1 个配置面 + 删 1 个 UI 开关**：监听不再是可变状态，只有端口是 |
| D10 只做定时安全比较 | 鉴权器**零可变状态** → 天然满足 `RISK-05` 的无状态要求，无需为它写并发测试 |

一句话把这四条合起来的含义说透，别让它被"代理密钥"这个名字拔高：**在这个监听范围与这组防护档位下，代理密钥不是网络安全边界，它是「撤销 + 防误操作」机制** —— 它保证真实 Key 永远不出现在客户端配置文件里、并且能一键收走某个客户端的访问权。这依然是很值的设计，但它是**运维价值而不是防御纵深**。将来若有人加 LAN 开关，`⑤⑥` 与爆破风险必须重评，不能沿用今天的结论 —— 这句已写进 `request-flow.dot` 的 `threat` 节点，免得只剩文档记忆里才有。

## 3. .NET 特有事实：程序集环不是"坏味道"，是编译错误

Java/JS 里循环依赖是债务；在 .NET 里 `CS0146 / A circular dependency was detected` 直接构建失败。这带来一个真实的推论：

> 你**不会**遇到环，但你会反复遇到"我需要引用那个程序集才能拿到这个类型"。那一刻的便宜做法是加引用（然后撞上环、被迫把类型搬进 Core）；正确做法是**从一开始就把跨层共享类型放进 Core**。

已识别的五个"必然要搬进 Core"的类型 —— 提前放对位置比事后搬家便宜：

1. 激活档快照（UI 写、Gateway 读）—— 见 §4.1
2. 每请求工具调用累积状态（`StreamContext`）—— 见 §4.3
3. 规范 usage / stop_reason / 错误载荷（含 **按入站协议区分的 401 错误体形状**）—— 已在 Core
4. **`RequestContext`**（已认证的代理密钥身份 + 其 RouteProfile + 请求中止令牌）：鉴权器产出、路由消费，两侧跨程序集，只能落 Core
5. **`ProxyKeyFingerprint`**（一个只包裹前缀片段的新类型，不包裹完整密钥）：这是 `FORBIDDEN-06` 从"审查条款"变成**编译器条款**的机制 —— 日志写入器的参数类型是 `ProxyKeyFingerprint`，谁想塞明文就必须先显式构造它，而那会在 code review 里刺眼。类型即护栏。

> 第 5 条是这套设计里最便宜的强约束：**用类型而不是规则来守边界**。一条自然语言规则会被忘，一个不接受 `string` 的方法签名不会。

## 4. 切断点分析：三条会自然长出来的边/环，如何在设计期消掉

### 4.1 激活态（同进程决定的必然产物）

WPF UI 与 Kestrel 同进程，所以"当前激活模型"**没有进程边界保护**。

- 若实现放在 `Gateway` 或 `App`：`ViewModels → Gateway` 立刻成环（编译失败）。
- 方案：**接口 `IActivationState` 进 Core，实现放 Hosting 注册为 Singleton，UI 与 RouteResolver 各自只依赖接口。**
- 剩下的真问题是**内存可见性**，不是依赖。切换必须对并发请求原子可见：写入时替换一个**不可变快照引用**（`Volatile.Read` / `Interlocked.Exchange`），而不是原地改字段。C4 里 `ActivationState` 已标注为"不可变快照 + 变更事件"，就是这条。
- 风险登记：`FORBIDDEN-03`。

### 4.2 流式回写（runtime 是环，compile-time 必须不是）

真实数据流是**闭环**的：

```
HttpEndpoints → 解码 → Providers → 上游 SSE → StreamRelay → 编码 → 写回 HttpEndpoints.Response
```

若 `StreamRelay` 直接持有 `HttpContext` 去 `WriteAsync`，`StreamRelay` 就依赖了传输层，而 `HttpEndpoints` 又依赖 `StreamRelay` —— 环。

**采用方案 B：管道不碰 HttpContext。**

```
Core 端口（每协议对 4 件）：
  IInboundRequestDecoder    入站线格式 → CanonicalRequest
  IOutboundRequestEncoder   CanonicalRequest → 出站线格式
  IOutboundStreamDecoder    上游原始帧 → CanonicalDelta（累积态在此，非 codec 字段）
  IInboundEventEncoder      CanonicalDelta → 入站事件序列

StreamRelay : 接收 IAsyncEnumerable<CanonicalDelta>，产出 IAsyncEnumerable<InboundEvent>
HttpEndpoints : 按 (入站协议, 目标 Provider) 从工厂取那 4 件，负责 Write + Flush + Abort
```

三个直接收益：

1. **编译期单向**，环消失。
2. **协议配对是运行时装配决定**，不是依赖关系 —— 新增第三种协议时两个已有 codec 零改动。
3. `StreamRelay` 可用 golden fixture 单元测试，不需要真起 Kestrel。

`Flush` 时机仍留在 `HttpEndpoints`（它是唯一知道 `IHttpResponseBodyFeature` 的人）；`StreamRelay` 只做纯变换。**这一条务必保持，否则纯管道会被污染成需要集成测试才能验证。**

### 4.3 无状态 codec

codec 若用实例字段累积 `arguments` 分片，多会话并发会互相串流（Claude Code 后台并行子代理很常见）。

- 规则：所有 codec 注册为 **Singleton 且无可变实例字段**；每请求状态放进由 `HttpEndpoints` 创建的 `StreamContext`（含 `content_block` 索引游标、tool 累积缓冲、`usage` 累加器）。
- 风险登记：`RISK-05`。

### 4.4 代理密钥：一条会自然长出来的"后门边"（D6 新增）

这条不是环，是**比环更可能的东西** —— 它在实现时看起来完全合理：

> 「我在鉴权器里已经查到这个代理密钥对应的 RouteProfile 了，顺手把真实 Key 也取出来放进 RequestContext，下游就不用再查一次库了。」

一旦这样写，`ProxyKeyAuth → Security` 这条边成立，于是：

1. 鉴权器的**每一条失败路径**（401、密钥被禁用、Profile 缺失）都站在了明文 Key 的可达范围内；
2. `RequestContext` 里出现一个 `string RealApiKey` 字段，而 `RequestContext` 是日志器最自然的输入对象 —— 真实 Key 离日志只差一次 `ToString()` 或一次 JSON 序列化；
3. §2 里"暴露面 = 1 个节点 1 条边"的可验证性**当场作废**，而且没有任何测试会红。

防线按成本从低到高，三层都要：

| 防线 | 做法 | 拦住什么 |
| --- | --- | --- |
| 类型 | `RequestContext` **不含**任何密钥字段，只含 `ProxyKeyFingerprint` + `RouteProfileId`（一个 id，不是凭据） | "顺手带上 Key"在类型上不成立 |
| 引用 | `AiGateway.Gateway`（鉴权所在）**不引用** `AiGateway.Security` | 想调用解密要先改 csproj，改动可见 |
| 测试 | ArchUnitNET 断言 `ProxyKeyAuth` 不依赖 `Security`；并断言 `Security` 的解密方法只有 `CredInjector` 所在类型能触达 | 漂移会被 CI 拦 |

配套的一次性验收：**全工程 DPAPI 解密调用点计数必须为 1**。这是 grep 就能做的检查，也是 D6 整个机制成立与否的唯一判据。

## 5. 影响半径（哪个改动最贵）

| 变更 | 直接触及 | 半径 | 验证成本 |
| --- | --- | --- | --- |
| 新增一种入站/出站协议 | 新 codec 程序集 + 1 条 Hosting 绑定 + RouteResolver 1 个分支；**两个已有 codec 零改动** | 小 | 低 —— 纯 fixture 回放 |
| 给 CanonicalModel 加可选字段（如 `cache_read_input_tokens`） | Core + 2 codec + StreamRelay + 测试 | 中 | 中 |
| 改鉴权头形态（`Authorization` vs `x-api-key`） | Providers + CredInjector | 小 | 低 |
| 吊销 / 禁用一个代理密钥 | 只影响 `ProxyKeyAuth` 下次校验；按 D7 **不改变任何路由**，按 D10 **无缓存所以天然即刻生效** | 小 | **低** —— 一个集成测试断言下一个请求 401 即可，无需失效广播 |
| 改「一键切换」的语义（全局 ⇄ 每密钥） | 若要转向每密钥钉死：`RequestContext` 加路由字段、鉴权器开始查 Profile、`routeResolver` 重新依赖 `Storage`、UI 多一层 | **大且方向相反** | 高 —— 等于把 D7 删掉的边全部加回来 |
| **改 SSE Flush / 事件顺序** | HttpEndpoints + StreamRelay + AnthropicCodec | **大** | **高 —— 只有真跑 Claude Code 才暴露** |
| **改 DPAPI scope / entropy** | Security + gateway.db | **不可逆** | 已存真实 Key 密文全废，需"全部重录 Key" |
| **改代理密钥哈希方案（含是否加盐）** | ProxyKeyAuth + 全部已发放密钥 | **不可逆** | 已发放的代理密钥**全部无法校验**，所有客户端要重填 |
| 换 ORM（Dapper ⇄ EF Core） | Storage + 迁移；Core 端口不变 | 小 | 中 |
| WPF UI ⇄ 独立 sidecar 进程 | 推翻 §4.1，`IActivationState` 需 IPC 版本 | 大 | 高 |

注意表中两行标着「不可逆」的条目构成一组对称：**两处加密决策都是单向门**，都便宜在"事前"、昂贵在"事后"。它们是这张表里唯二"改了就救不回来"的条目 —— 其余行半径再大，方向都能折返。

**三个高价值前瞻约束（现在做便宜，以后做贵）：**

1. `gateway.db` 的 secret 表必须有 `protector_id` + `key_version`，否则将来从 DPAPI 迁移到信封加密时无法识别哪些行是老密文。这是 `RISK-01` 的免费保险。
2. `StreamContext` 的块索引游标必须与 `content_block_start/stop` 严格配对 —— Claude Code 收到乱序 `content_block_index` 会直接断流。这条只能在真实客户端上验证，所以要**尽早做端到端探针**，不要等全部写完。
3. 代理密钥表同样需要 `algo_version` + `enabled` 位。理由与第 1 条同构：日后想从裸 SHA-256 换到加盐/慢哈希时，没有版本列就无法做"老哈希继续可用、新密钥用新算法"的平滑过渡。同时**生成端就决定"事后能否回显"**（见 §8），这个选择一旦写入第一条记录就固化。

## 6. 核心难点（值得单独盯）

**流式工具调用的增量重编码。**

OpenAI 的 `tool_calls` 是**扁平分片**：`index` 定位、第一个分片带 `id`+`function.name`、后续分片只追加 `function.arguments` 字符串，且**没有结束标记**（只靠 `finish_reason: "tool_calls"` 或 `data: [DONE]` 隐含收尾）。

Anthropic 要求**显式块生命周期**：`content_block_start(type=tool_use, id, name)` → N × `content_block_delta(partial_json)` → `content_block_stop`。

于是你必须**凭空构造出块边界**：决定何时敢发 `content_block_start`（要等 name 到齐）、`index` 与 `content_block` 序号如何共存（text 与多个并行 tool call 混排）、以及收尾时把未完成的 JSON 怎么办。

这不是"接口不匹配"，而是**一种协议根本没有、另一种协议强制要求的信息**。它就是 `ToolCallSession` 必须待在 Core、且必须有独立 golden-fixture 测试的原因。

**第二难点（D6 之后新增，难度低但出错率高）**：401 与上游错误体的**协议形状保真**。鉴权失败发生在你还没解析入站协议之前，所以你必须在"只知道请求头"的情况下判断该用哪种错误 JSON —— 实践做法是按路径判（`/v1/messages` → Anthropic 形状，`/v1/chat/completions` → OpenAI 形状），而不是按客户端头判。这条不值得单独一层抽象，但值得一条契约测试。

## 7. 治理：怎么把上面这些变成机器能拦的

在 `AiGateway.ArchitectureTests`（xUnit）里用 **ArchUnitNET** 断言，成本一个测试文件：

```
Mapping.Anthropic 不依赖 Mapping.OpenAI（及反向）
Core 不依赖任何本解决方案其他程序集
App 不依赖 Storage / Security / Gateway
Gateway 不依赖 App
Providers/Storage 之外不得引用 Microsoft.Data.Sqlite          ← FORBIDDEN-05/08
Gateway 的鉴权侧（ProxyKeyAuth）不依赖 Security                ← FORBIDDEN-07
RunLog / 日志写入器不依赖 Security                             ← FORBIDDEN-06
Security 不依赖 HttpClient / System.Net                        ← FORBIDDEN-09
DPAPI 解密调用点计数 == 1                                      ← §4.4 一次性验收
```

四条 `FORBIDDEN-06/07/08/09` 全部来自本轮补充需求。**只有测试能防漂移，清单不能**；而其中"解密点计数 == 1"甚至不需要 ArchUnitNET，一个 grep 断言就够。

## 8. 尚未闭环（不假装已解决）

### 已在第二轮澄清中决出（D7–D10，全部取最轻档）

**代理密钥机制原有的三条未闭环项 —— 已决：**

- `UNKNOWN-04` **已决 D7：路由全部跟随全局激活档，代理密钥不含路由信息。** 直接后果要说清：**多个客户端无法同时使用不同模型** —— 所有入站客户端共享同一个"当前激活模型"。若将来需要"这个客户端走 DeepSeek、那个走 Claude"，那不是加配置，而是把 D7 删掉的边全部加回来（见 §5 表）。
- `UNKNOWN-05` **已决 D8：只存 SHA-256 哈希，生成后不可回显。** 代价：忘了就重新生成、所有客户端配置要重填。无盐、不用慢哈希在这里是正当的，因为密钥由程序生成、是高熵随机值而非人写口令 —— 这条理由要留在文档里，否则将来一定有人"顺手"把它升级成 PBKDF2 并给 `enabled` 检查加一次派生开销。
- `UNKNOWN-06` **已决 D9 + D10：地址写死 `127.0.0.1`（只有端口可改），失败防护只做定时安全比较，不加计数与退避。** 因此鉴权器零可变状态。
- `RISK-02` 随之**降级为低**：LAN 暴露面在本设计里不存在，`⑤⑥` 两条不变式只在"将来有人加 LAN 开关"时才需要重评。

仍然开着的：

- `UNKNOWN-01` 存储访问选型 Dapper vs EF Core —— 一次 spike 即可决出，不要现在赌。
- `UNKNOWN-02` 首期是否真服务 OpenAI 协议入站客户端（双向矩阵是被 D1 选型带进来的，无使用证据）。
- `UNKNOWN-03` thinking 透传 / 故障转移 的范围（请求日志已按 D5 以基础形态回归 MVP）。
- `RISK-04` Claude Code 对 `/v1/messages/count_tokens` 的实际依赖程度 —— **需实测**，我按"必须存在、可返回估算"设计，未经证实。
- `OPEN-07` 第一个要接的真实 Provider 是 DeepSeek 还是 OpenAI —— 影响 profile 优先级，与 `RISK-04` 同批实测。

## 9. 下一步

阶段 4 开工前的唯一硬阻塞：本机 **没有 .NET SDK**（只有 `Microsoft.WindowsDesktop.App 8.0.28` 运行时，`C:\Program Files\dotnet\sdk` 不存在）。不装 SDK 无法 `dotnet new` / `dotnet build`，C4 里所有容器都无法实例化。
