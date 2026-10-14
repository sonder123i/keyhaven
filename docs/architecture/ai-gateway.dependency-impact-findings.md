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
| App 看不见两个库的内部类型 | UI 直接 `new` 出保护器或注入器（FORBIDDEN-03/08） | `internal` 默认 + 每个库只公开一个注册方法 |
| 协议代码分不分程序集 | ——（入站只有一种协议，不存在两个 codec 互引） | 不拆 |

第一版拆成 10 个程序集，大部分边界拦的是"两个 codec 互引""Core 反向依赖"这类在单入站下不存在的错误；代价却是实打实的：S1 骨架撞出两处程序集环（MSB4006）和一处传递引用泄露。

## 2. 护栏

**一个测试文件**：

| # | 断言 | 拦什么 |
| --- | --- | --- |
| ① | `src/AiGateway/AiGateway.csproj` 里没有指向 Security 的 `ProjectReference`；**且** AiGateway 程序集元数据不引用 Security | 有人给 AiGateway 加 Security 引用。**只查元数据不够**（S1 实测）：编译器会删掉没被代码用到的引用，"只加引用不写代码"时元数据检查仍是绿的，所以要同时查 csproj |
| ② | 全 `src/` 里 `ProtectedData.Unprotect` 恰好出现 1 次，且在注入处理器 | 第二个解密点 |
| ③ | `src/AiGateway.Security/` 源码里不出现 `HttpClient`、`IHttpClientFactory`、`SocketsHttpHandler`、`HttpClientHandler`、`new HttpRequestMessage`（`base.SendAsync` 除外） | 注入器自己发请求（FORBIDDEN-09） |
| ④ | `IRealKeyVault` 的所有成员返回 `void` / `bool` / `Task` / `Task<bool>` | 只写端口被加上读方法（FORBIDDEN-10） |
| ⑤ | 吊销一个代理密钥后，下一个请求返回 401 | 内存快照没随写入替换（DECIDED-01） |

②③ 是源码级 grep 断言，粗糙但够用：它们守的是"出现了就一定该有人看一眼"的东西。ArchUnitNET 不再需要。

**类型护栏**（不用测试，签名本身就拦）：
- 日志写入器只接受 `ProxyKeyFingerprint`（只包前缀片段的类型），不接受 `string`——想塞明文就得显式构造它，review 时刺眼（FORBIDDEN-06）。
- 鉴权产出的请求上下文**没有任何密钥字段**，"顺手把真实 Key 带下去"在类型上不成立。
- 翻译器无状态单例，每请求状态（块索引游标、工具参数累积、usage）放在每请求的上下文对象里，防并发串流（RISK-05）。

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

H 的收益：没有任何代码能调用注入器；上游客户端不经手明文、甚至不知道注入器存在；provider id 经请求选项传递，键名是 AiGateway 里的一个字符串常量。

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
2. **工具调用增量重编码**（M2）：OpenAI `tool_calls` 是扁平分片（`index` 定位、首片带 `id`+`name`、后续只追加 `arguments`、没有结束标记）；Anthropic 要求显式块生命周期（`content_block_start` → N × `input_json_delta` → `content_block_stop`）。必须凭空构造块边界：何时敢发 start（等 name 到齐）、文本块与多个并行工具块的索引如何共存、收尾时未完成的 JSON 怎么办。这是"一种协议没有、另一种强制要求"的信息，不是接口不匹配。

## 7. 尚未闭环

见 `ai-gateway.violations.csv` 中状态为 OPEN 的行，以及 `ai-gateway.architecture-understanding.md` §7。
