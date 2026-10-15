# 程序集依赖、护栏与变更影响 —— AI 本地代理网关

- **版本**：2026-10-15 修订版（D11：10 个程序集收成 3 + 1）。旧版的 10 程序集耦合表、规范模型四端口、FORBIDDEN-01/02 等已随架构作废，全文在 git `cc01023`。
- 图源：`ai-gateway.dependencies.dot`（程序集与护栏）、`ai-gateway.request-flow.dot`（一次请求）。机器可读规则：`ai-gateway.violations.csv`。

## 1. 为什么是 3 + 1 个项目

```
AiGateway.App (WinExe, net8.0-windows) ──► AiGateway (net8.0)
           │                                      ▲
           └──────────► AiGateway.Security ───────┘   (net8.0-windows，唯一引用 ProtectedData)
AiGateway.Tests ──► 以上全部（InternalsVisibleTo）
```

**只有"密钥"这条脊柱值得一道编译期边界。** 判断标准：拆出一个程序集，必须换来"编译器替你拦住某类错误"。

| 边界 | 要拦住的错误 | 守法 |
| --- | --- | --- |
| AiGateway **不引用** Security | 网关 / 鉴权 / 日志代码拿到解密能力（FORBIDDEN-06/07） | 编译期**两道独立拦截**（S1 实测）：AiGateway 目标 `net8.0` 引用 `net8.0-windows` 的 Security 直接 **NU1201**；就算改了目标框架，Security → AiGateway 已存在，反向再加一条就是 **MSB4006 循环依赖**。护栏①测试是第三道 |
| 只有 Security 引用 ProtectedData | 有人在别处直接调 DPAPI | 编译期：包引用；AiGateway 目标 `net8.0`，就算加了包，Windows 专属 API 也会触发 CA1416（警告即错误，S1 review 实测过这条） |
| App 看不见两个库的内部类型 | UI 直接 `new` 出保护器或注入器（FORBIDDEN-03/08） | `internal` 默认（越界即 CS0122，S1 实测）。公开面只有：AiGateway 的 `AddAiGateway()`、`GatewayServer`（宿主入口，监听地址常量在它内部，App 改不了）与 `GatewayPortInUseException`、`IRealKeyVault`（只写，给 UI）、`IProxyKeyRegistry` + `IssuedProxyKey` + `ProxyKeyFingerprint` + `ProxyKeySummary`（签发 / 只给指纹的列表 / 吊销 / 删除代理密钥，给 UI）、`IGatewaySettings` + `ProviderConfig` + `ActiveSelection` + `UpstreamKind`（上游、激活、端口，给 UI；签名里没有任何 Key，S7）、`IRunLog` + `RunLogEntry` + `RunLogOutcome`（运行日志只有 Snapshot 一个方法，记录是定长安全字段，给 UI，S9）、`ICiphertextStore` + `CiphertextRecord`（只存取密文，给 Security）；Security 的 `AddAiGatewaySecurity()`。**给 Security 的是公开密文端口，而不是 `InternalsVisibleTo`**——后者会让 Security 看到 AiGateway 内部的上游客户端，等于给 FORBIDDEN-09 开后门；密文端口被谁拿到都解不开（解密点只有一处，护栏②） |
| 协议代码分不分程序集 | ——（入站只有一种协议，不存在两个 codec 互引） | 不拆 |

第一版拆成 10 个程序集，大部分边界拦的是"两个 codec 互引""Core 反向依赖"这类在单入站下不存在的错误；代价却是实打实的：S1 骨架撞出两处程序集环（MSB4006）和一处传递引用泄露。

## 2. 护栏

**一个测试文件**：

| # | 断言 | 拦什么 |
| --- | --- | --- |
| ① | `src/AiGateway/AiGateway.csproj` 里没有指向 Security 的 `ProjectReference`；**且** AiGateway 程序集元数据不引用 Security | 有人给 AiGateway 加 Security 引用。**只查元数据不够**（S1 实测）：编译器会删掉没被代码用到的引用，"只加引用不写代码"时元数据检查仍是绿的，所以要同时查 csproj |
| ② | 全 `src/` 里 `ProtectedData.Unprotect` 恰好出现 1 次，在 `SecretProtector.cs`（S2）；且 `SecretProtector.Unprotect` 只被 `CredentialInjectingHandler` 调用（S5） | 第二个解密点。两级都红灯实测过 |
| ③ | `src/AiGateway.Security/` 源码里不出现 `HttpClient`、`IHttpClientFactory`、`SocketsHttpHandler`、`HttpClientHandler`、`HttpMessageInvoker` 这几个词（整词匹配，`AddHttpClient` 不算），也不出现 `new HttpRequestMessage` | 注入器自己发请求（FORBIDDEN-09）。S5 红灯实测：在 Security 里 `new HttpClient()` 即红 |
| ④ | `IRealKeyVault` 的所有成员返回 `void` / `bool` / `Task` / `Task<bool>` | 只写端口被加上读方法（FORBIDDEN-10） |
| ⑤ | 吊销一个代理密钥后，下一个请求返回 401 | 内存快照没随写入替换（DECIDED-01） |
| ⑥ | 设了 `ASPNETCORE_URLS` 与 `Kestrel__Endpoints__*` 环境变量后，监听地址仍然只有一个 `127.0.0.1` | D9 被环境变量绕过。**S3 红灯实测**：换回默认的 `WebApplication.CreateBuilder()`，环境变量就多开了一个 `0.0.0.0` 监听；所以宿主用空构建器，什么配置源都不读 |
| ⑦ | 上游命名客户端**不写任何内置日志**（抓取器在 Trace 级证明自己在工作，同时上游客户端类别下零条日志、所有日志含结构化字段里都搜不到 Key） | Key 进日志（RISK-11 / OPEN-13）。**S5 实测**：默认在 Trace 级原样记录 `x-api-key: 真实Key`；改用 `RedactLoggedHeaders` 后格式化文本变成 `*`，但**结构化字段里仍是明文**，JSON / Serilog 这类结构化输出会照写——所以用 `RemoveAllLoggers()` 整个拿掉 |
| ⑧ | 上游返回 302 时原样把 302 交回，不跟过去，另一台主机收不到任何请求 | `x-api-key` 是自定义头，跟随重定向会把它带到别的主机。S5 红灯实测 |
| ⑨ | 请求发出后，持有该请求对象的代码在头里找不到 `x-api-key` | 网关代码事后从请求对象读到明文。S5 红灯实测 |
| ⑩ | 请求自带的 `x-api-key` / `Authorization`（比如误转发的代理密钥）被替换，上游只看到真实 Key | 代理密钥被转发到上游。S5 红灯实测 |
| ⑪ | `src/AiGateway/RunLog/` 与 `RunRequestState.cs` 的源码里不出现 `Exception` / `Message`；`IRunLog` 只有 `Capacity` 与 `Snapshot`（没有写方法）；`RunLogEntry` 没有 `Key` / `Plaintext` / `Sha256` 成员；**转发器的构造函数只有 `RouteResolver` 与 `IHttpClientFactory`（拿不到配置存储）** | 有人往运行日志里塞错误文本、正文或密钥（FORBIDDEN-06），或让转发器在收尾时重读激活档、把旧请求记成新上游（S9）。红灯实测：给请求状态加一个 `LastExceptionMessage` 字段、给 `IRunLog` 加一个 `Append`、把 `ConfigStore` 拿回转发器——各自只让对应测试变红。**最后那条只有结构性断言能抓**：把重读恢复后全量跑，行为测试一条都不红（那个竞态窗口是微秒级，测试撞不上） |

①②③ 是源码 / 项目文件级断言，粗糙但够用：它们守的是"出现了就一定该有人看一眼"的东西；④–⑪ 是行为级 / 结构级测试，⑦–⑪ 里 ⑦–⑩ 用一个真实 Kestrel 桩上游跑。ArchUnitNET 不再需要。

**类型护栏**（不用测试，签名本身就拦）：
- 日志写入器只接受 `ProxyKeyFingerprint`，不接受 `string`——想塞明文就得显式构造它，review 时刺眼（FORBIDDEN-06）。**指纹 = 代理密钥 SHA-256 的前 8 位十六进制**（S4 定稿），不是明文前缀：日志和 UI 里不出现明文的任何一部分，用户靠创建时的标签认密钥。
- 交出明文的 `IssuedProxyKey` 是普通类而**不是 record**：record 自动生成的 `ToString()` 会把 `Plaintext` 打进任何格式化它的日志。S4 红灯实测：改成 record 后"格式化不泄露明文"测试变红。
- 鉴权产出的请求上下文**没有任何密钥字段**，"顺手把真实 Key 带下去"在类型上不成立。
- 翻译器无状态单例，每请求状态（块索引游标、工具参数累积、usage）放在每请求的上下文对象里，防并发串流（RISK-05）。

**UI 层（S7）**：
- **ViewModel 对象图里没有 Key**：真实 Key 只从 `PasswordBox` 交给 `IRealKeyVault.Save`，随即清空输入框；代理密钥明文只进一次性窗口。测试用反射遍历 ViewModel 的整个对象图（字段、集合、嵌套对象）找明文，扫描器先对一个埋了明文的对象自检，证明它真能找到。红灯实测：给 ViewModel 加一个留住 Key 或代理密钥明文的属性即红（FORBIDDEN-03）。
- **本机以外的上游地址必须 https**（RISK-15）：http 会让真实 Key 明文出网；只有回环地址（本机桩、本机代理）可以用 http。
- **上游 id 由名称生成且永不改**：DPAPI 的 entropy 绑在 id 上，改名只改显示名。
- **不可逆操作的确认框默认按钮是「否」**（RISK-16）：真 exe 实测 `DM_GETDEFID` 改前 6（「是」）、改后 7（「否」）——误按回车不会吊销密钥或删掉上游。
- **运行日志只能被拉取**：`IRunLog` 只有 `Capacity` 与 `Snapshot()`；请求线程不引用 UI，UI 用 `DispatcherTimer` 每秒读一次快照（FORBIDDEN-04）。每请求的临时状态放在 `HttpContext.Items` 里的小对象上。
- **一行日志的两个标签来自同一份快照**：`RouteResult` 自带 `ProviderId`（连"没激活上游 / 没存 Key"这类失败也带），转发器因此不需要配置存储，也不会在收尾时重读——这是 S9 复核时改掉的：原先写成 `state.Provider ??= store.Current.Active?.ProviderId`，注释却声称"就是解析器刚读的那份快照"，实际是第二次读，且从不使用 `route.ProviderId`。结构性断言（转发器构造函数参数）现在守着这条——实测把重读恢复后**只有它会红**，行为测试撞不上那个微秒级窗口。
- WPF 的 GridView 行在 UI 自动化里叫 `DataItem`，可访问名称取自行对象的 `ToString()`；record 默认会把全部字段念出来，所以行记录都重写了 `ToString()`。

## 3. S1 实测留下的教训（仍然有效）

这些是第一版骨架在本机撞出来的，换个结构也会复现：

- **组件图 ≠ 程序集图**：同一程序集内两条边无害，折叠到程序集上可能成环。照图建引用，第一步必须是 `dotnet build`。
- **`.gitignore` 的 `*.app`** 在 Windows（`core.ignorecase=true`）下会吞掉任何名字以 `.App` 结尾的项目目录：本地构建照样绿，推上去的仓库却缺项目。已加负规则 `!/src/AiGateway.App/`。
- **`TreatWarningsAsErrors` 管不到 MSBuild 警告**："引用的项目不存在"只是警告，构建 exit 0。已加 `MSBuildTreatWarningsAsErrors`。
- **SDK 的 ProjectReference 默认传递**：A → B → C 会让 A 直接看见 C。新结构里 App 本来就直接引用两个库，所以靠 `internal` 而不是靠"看不见程序集"来守 UI。
- **"全绿"只认 fresh clone**：`git status` 干净时看不到被忽略的文件。

## 4. 两扇差点打开的后门（D12 / D13 的来历）

**公共端口是一扇 DI 后门。** 公开的接口对引用它的所有代码可见，任何类都能在构造函数里要一个实例：

- `IRealKeyVault` 若有 `Get`，网关注入它就拿到明文——此时"类型层、引用层、解密点计数"三层防线**全绿**。⇒ D13：只写。
- 注入器接法三选一：

| 接法 | 问题 | 结论 |
| --- | --- | --- |
| Security 引用上游客户端并调用它 | Security 在类型上就能发请求（FORBIDDEN-09） | ✗ |
| 注入器做成公共端口（`Apply(providerId, headerSink)`），上游客户端调用 | 同一扇 DI 后门：任何类注入它、自传 sink 就接走明文 | ✗（我曾推荐过，review 时推翻） |
| **H：注入器是 `DelegatingHandler`，组合根把它挂进上游 HttpClient 管道** | Security 要依赖 `System.Net.Http` 的 `DelegatingHandler`；FORBIDDEN-09 的检测改为精确意图（护栏③） | ✓ D12 |

H 的收益：没有任何代码能调用注入器；上游客户端不经手明文、甚至不知道注入器存在；provider id 经请求选项传递，键名是 AiGateway 里的 `UpstreamRequest.ProviderId`。

**S5 落地后的细节**：注入时先清掉请求上已有的 `x-api-key` 与 `Authorization`（护栏⑩），再写入真实 Key，**请求发完立即删掉**（护栏⑨）；目前只写 `x-api-key`（Anthropic 与 DeepSeek 都认），M2 接 OpenAI 时再按上游类型加 `Authorization: Bearer`。"激活快照"没有另起一份数据：路由每次只读一次 `ConfigStore.Current`，从同一份快照里拿激活项、Provider 和"有没有 Key"，切换时不会读到半新半旧。

## 5. 变更影响（哪个改动最贵）

| 变更 | 触及 | 半径 | 验证成本 |
| --- | --- | --- | --- |
| 新增一个说 Anthropic 协议的上游（Kimi、智谱等） | 一条 Provider 配置 | 极小 | 一次真实请求 |
| 新增一个说 OpenAI 协议的上游 | 一条 Provider 配置（翻译器已存在之后） | 小 | 一次带工具调用的真实请求 |
| 新增一种非 Anthropic、非 OpenAI 协议的上游 | 新翻译器 + 路由一个分支 | 中 | fixture 回放 + 真实请求 |
| 一键切换改成"每密钥独立路由" | 鉴权器开始读路由、快照结构变、UI 多一层 | 大且方向相反 | 等于推翻 D7 |
| 改直通的头 / 查询串处理 | 直通转发器 | 小 | **高——只有真跑 Claude Code 才暴露** |
| 改翻译的事件顺序 / 块索引 | 翻译器 + 工具调用重编码 | 大 | **高——同上** |
| 加 OpenAI 协议的入站客户端 | 新入站端点 + 反向翻译 + OpenAI 形状错误体 | 大 | 高；D1 已明确不做 |
| **改 DPAPI 作用域 / entropy 方案** | Security + config.json | **不可逆** | 已存 Key 全部要重录 |
| **改代理密钥哈希方案（含加盐）** | 鉴权器 + 全部已发放密钥 | **不可逆** | 所有客户端要重填 |
| JSON ⇄ SQLite | 配置存储一个组件 | 小 | 中 |
| UI 与网关拆成两个进程 | 激活快照与密钥快照都要 IPC | 大 | 高；D3 已明确不做 |

两处"不可逆"都是加密决策，便宜在事前、昂贵在事后——所以 `protector_id` / `key_version` / `algo_version` / `enabled` 四个字段在写第一条数据前就要进 JSON 结构（计划 S2）。

## 6. 难点

1. **直通保真**（M1）：头（`anthropic-version`、`anthropic-beta`）、查询串（`?beta=true`）、只改顶层 `model` 而其余字段不变、SSE 逐块 flush、客户端中止传播到上游。难度低，但任何一处错都只在真 Claude Code 上暴露——所以 M1 以真客户端验收收尾。
   **S6 落地与实测**：
   - 请求头**白名单**转发：`anthropic-*`、`accept`、`user-agent`；cookie、客户端遥测头、代理密钥一律留下（护栏⑩之外又一层）。响应头同样白名单：`content-type`、`retry-after`、`request-id`、`anthropic-*`。
   - 请求体用 `JsonNode` 只改 `model`，测试用 JSON 深度比较证明其余字段（含 `thinking`、`cache_control`、中文）语义不变；红灯：顺手删掉 `thinking`（旧版规范模型的做法）即红。
   - **真正致命的是缓冲，不是漏 flush**：红灯实测里去掉显式 `FlushAsync` 仍然全绿（Kestrel 每次 `WriteAsync` 自己就 flush）；而"先读完整个响应再写"或用默认的 `ResponseContentRead`，流式与中止两条测试**同时**变红。显式 flush 保留作双保险。
   - 客户端中止 → 上游请求被取消（进程外演示：1.2 秒掐断，上游在同一时刻收到取消）；上游客户端无超时（默认 100 秒会掐断长回答）。
   - 失败映射：无激活 → 503；OpenAI 上游 → 501（M2）；请求体不是 JSON 对象 → 400；上游连不上 → 502；**存的 Key 在当前 Windows 账户下解不开 → 503 并提示重新录入**（RISK-01 的用户可见面）；上游自己的 4xx/5xx 连同 `retry-after` 原样透传。
   - **S8 真客户端补的一条**：Claude Code 对 5xx 与 401 会重试约 3 分钟（10 次）才显示错误。所以网关自己产生的配置类错误（503 / 501 / 400）与代理密钥 401 都带 `x-should-retry: false`；上游 401 没给提示时补上 `false`；上游自己的提示原样透传；只有"上游不可达"的 502 保持可重试。修复前没激活上游要等 174 秒，修复后 3 秒（RISK-17）。
2. **工具调用增量重编码**（M2）：OpenAI `tool_calls` 是扁平分片（`index` 定位、首片带 `id`+`name`、后续只追加 `arguments`、没有结束标记）；Anthropic 要求显式块生命周期（`content_block_start` → N × `input_json_delta` → `content_block_stop`）。必须凭空构造块边界：何时敢发 start（等 name 到齐）、文本块与多个并行工具块的索引如何共存、收尾时未完成的 JSON 怎么办。这是"一种协议没有、另一种强制要求"的信息，不是接口不匹配。

## 7. 尚未闭环

见 `ai-gateway.violations.csv` 中状态为 OPEN 的行，以及 `ai-gateway.architecture-understanding.md` §7。
