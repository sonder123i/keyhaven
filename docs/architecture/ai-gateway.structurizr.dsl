workspace "AI 本地代理网关" "Windows WPF 桌面端 AI 代理网关 —— greenfield 目标架构（target-state）。工作区无代码，所有节点状态为 target，置信度见 ai-gateway.evidence.md。" {

    !identifiers hierarchical

    model {
        operator = person "桌面操作者" "配置 Provider、录入/轮换 API Key、一键切换激活模型、查看请求足迹"

        claudeCode = softwareSystem "Claude Code CLI" "只说 Anthropic 协议的入站客户端。配置 ANTHROPIC_BASE_URL=http://127.0.0.1:8000，API Key 字段填【代理密钥】。" {
            tags "External,InboundClient"
        }
        openAiClient = softwareSystem "OpenAI 协议客户端" "Cursor / Codex / Cline / 自研脚本等，说 /v1/chat/completions，同样只持代理密钥。inferred：首期是否服务这类客户端尚未确认。" {
            tags "External,InboundClient,LowConfidence"
        }

        gateway = softwareSystem "AI 本地代理网关" "单机桌面应用：真实上游 API Key 用 DPAPI 加密存本地 SQLite；对外只发放可撤销的【代理密钥】。在本机 8000 端口暴露 Anthropic ⇄ OpenAI 双向协议转换代理，验过代理密钥后在内存中替换为真实 Key 再转发，支持 SSE 流式。" {

            appExe = container "AiGateway.App.exe" "唯一可部署单元：STA 线程跑 WPF UI，后台线程跑 Kestrel 网关，同进程共享内存态。" "C# / .NET 8 (net8.0-windows) + WPF + ASP.NET Core Minimal Hosting + Kestrel" {
                tags "Deployable,SameProcess"

                compositionRoot = component "Composition Root / 宿主生命周期" "注册 DI；启动/停止 Kestrel；持有单实例 Mutex；退出时优雅关闭在途流。" ".NET Generic Host + Microsoft.Extensions.DependencyInjection" {
                    tags "Hosting"
                }

                wpfViews = component "WPF Views (XAML)" "四个核心页面：模型/Provider 管理、代理密钥管理、激活档一键切换、基础运行日志列表。代理密钥明文只在生成的那一刻出现一次并靠复制带走，之后列表只显示指纹。" "XAML + .NET 8 WPF" {
                    tags "UI"
                }
                viewModels = component "ViewModels (MVVM)" "界面只保留四类核心动作：模型管理、代理密钥管理（生成/吊销/复制一次明文）、一键切换激活模型、查看基础运行日志。**刻意不做**统计图表、多用户、云端同步。" "CommunityToolkit.Mvvm（选型待确认）" {
                    tags "UI,LowConfidence"
                }

                httpEndpoints = component "本地 HTTP 入口" "POST /v1/messages、POST /v1/chat/completions、POST /v1/messages/count_tokens、GET /health。只做协议派发与响应写出，不含业务。" "ASP.NET Core Minimal API" {
                    tags "Gateway,Mvp"
                }
                proxyKeyAuthenticator = component "代理密钥验证器" "入站鉴权中间件：从 Authorization / x-api-key 取代理密钥，与库中 SHA-256 哈希做 CryptographicOperations.FixedTimeEquals 比对，检查 enabled 位。产出【只含身份与指纹】的已认证 RequestContext —— 按 D7 不携带任何路由信息。失败按入站协议错误体形状回 401。按 D10：无缓存、无失败计数、无退避，因此本组件完全无状态、注册为 Singleton；每请求现查哈希正是为了让吊销即刻生效。" "ASP.NET Core middleware + SHA-256 + FixedTimeEquals" {
                    tags "Security,Mvp"
                }
                httpListenerPolicy = component "监听与端口策略" "按 D9：绑定地址在代码里写死 127.0.0.1，UI 不提供局域网开关，只暴露端口改（默认 8000）。端口冲突要能在 UI 上看到并当场改，/health 免鉴权用于自检端口活着。" "Kestrel Listen(Loopback, port)" {
                    tags "Hosting,Mvp,Stripped"
                }
                credentialInjector = component "真实密钥注入器" "真实 API Key 明文在进程内的唯一现身点。形态是 HttpClient 管道里的一个 DelegatingHandler，由 compositionRoot 装配进 upstreamClients 的 HttpClient，不被任何组件调用（2026-10-15 定稿 H）：按请求选项里的 provider id 取 DPAPI 密文 → 解密 → 仅写入经过它的这次出站请求的鉴权头。不构造、不发起任何请求；不落日志、不进异常消息、不回给客户端。" "DelegatingHandler + DPAPI" {
                    tags "Security,Mvp,HighRisk"
                }
                routeResolver = component "路由与激活模型解析器" "按 D7：路由的【唯一来源】是 ActivationState 的全局快照，本组件不读代理密钥的任何属性、也不查库。输入 (入站协议, 请求体 model 字段) → 输出 (Provider, 真实 model, BaseUrl)。" "C#" {
                    tags "Gateway,Mvp"
                }
                anthropicCodec = component "Anthropic 编解码器" "解析 /v1/messages 请求与生成 messages SSE 事件序列（message_start / content_block_start / content_block_delta / content_block_stop / message_delta / message_stop）。thinking 按 D4 出局：入站 thinking 配置必须被识别并显式降级，不得无声流入规范模型。" "System.Text.Json" {
                    tags "Mapping,Mvp"
                }
                openAiCodec = component "OpenAI 编解码器" "解析 /v1/chat/completions 请求与生成 chat.completion.chunk 序列。" "System.Text.Json" {
                    tags "Mapping,Mvp"
                }
                canonicalModel = component "内部规范消息模型" "Provider 无关的 Message / ContentBlock / ToolSpec / Delta / Usage / StopReason 抽象。双向矩阵的唯一汇合点。" "C# records, 无外部依赖" {
                    tags "Core,Mvp,NoIo"
                }
                toolCallBridge = component "工具调用桥接器" "tool_use ⇄ tool_calls 的语义与分片对齐：把 OpenAI 的 index+id+function.name+arguments 增量拼成 Anthropic 的 input_json_delta。" "C#" {
                    tags "Mapping,Mvp,HighRisk"
                }
                streamRelay = component "流式转发管线" "逐事件读上游 SSE → 解码为规范增量 → 重编码为入站协议事件 → 立即 flush；处理心跳、上游提前断开、客户端中止。" "IStreamDecoder/Encoder pipeline" {
                    tags "Gateway,Mvp,HighRisk"
                }
                upstreamClients = component "上游 Provider 客户端" "按 Provider 构造 HttpClient：BaseUrl、鉴权头形态（Authorization Bearer vs x-api-key）、超时与非流式/流式调用。" "HttpClient + SocketsHttpHandler" {
                    tags "Provider,Mvp"
                }
                activationState = component "激活态共享内存" "「当前激活模型」的进程内单一事实源；UI 切换后网关下一个请求即生效，无需 IPC。按 D7 它是路由的【唯一来源】，因此快照必须自带 Provider、真实模型名、BaseUrl 与密钥句柄（是引用不是明文）。" "ConcurrentDictionary + 不可变快照 + 变更事件" {
                    tags "Core,Mvp,SameProcess"
                }
                secretProtector = component "密钥保护服务" "DPAPI CryptProtectData/CryptUnprotectData，CurrentUser 作用域 + 每 Provider 独立 entropy 做域分离。" "System.Security.Cryptography.ProtectedData" {
                    tags "Security,Mvp"
                }
                repositories = component "数据访问仓储" "Provider / Model / 真实 Key 密文 / ProxyKey(哈希+启用位) / 全局激活档（D7：只有一份）/ 运行日志行的 SQLite 读写与建库迁移。" "SQLite + Dapper 或 EF Core（选型待定）" {
                    tags "Storage,Mvp,LowConfidence"
                }
                runLog = component "基础运行日志" "一行一请求：时间 / 代理密钥前缀指纹 / 入站协议 / 目标 Provider+model / 状态 / 首字节耗时 / 中止原因。环形缓冲有上限。**刻意不含**统计图表、成本核算、请求体、响应内容、任何密钥明文。" "SQLite 环形表 + 只读列表 UI" {
                    tags "Observability,Mvp,Stripped"
                }
                failoverPolicy = component "故障转移与重试" "多 Key 轮转、429/5xx 退避重试、备用模型降级。" "Polly（选型待定）" {
                    tags "Reliability,Phase2,LowConfidence"
                }
            }

            configDb = container "gateway.db" "本机 SQLite 单文件，绝不明文落盘。两种密钥存储形态不同：真实上游 API Key = DPAPI 可逆密文（能还原）；代理密钥 = 不可逆 SHA-256，按 D8【生成后不可回显】。无盐且不用慢哈希在此是正当的，因为代理密钥由程序生成、是高熵随机值而非人写口令，不存在离线猜解面。代理密钥表带 algo_version + enabled 位以备平滑换算法。" "SQLite 文件 + DPAPI 加密列 + SHA-256 哈希列" {
                tags "Database"
            }
        }

        dpapi = softwareSystem "Windows DPAPI" "OS 级用户主密钥体系。密文只能被同机同用户解密：换机、换 Windows 账户、重置密码后可能不可读。" {
            tags "External,OsCapability"
        }

        openAiApi = softwareSystem "OpenAI 兼容远端 API" "api.openai.com / api.deepseek.com 等，暴露 /v1/chat/completions。" {
            tags "External,Upstream"
        }
        anthropicApi = softwareSystem "Anthropic API" "api.anthropic.com，暴露 /v1/messages。" {
            tags "External,Upstream"
        }

        operator -> gateway "通过界面管理密钥、发放/吊销代理密钥、一键切换当前模型" "Keyboard/Mouse"
        gateway -> dpapi "加解密 API Key（CurrentUser 作用域）" "CryptProtectData"

        claudeCode -> gateway "POST /v1/messages（含流式）、/v1/messages/count_tokens；头带代理密钥" "HTTP/1.1 + SSE over localhost:8000"
        openAiClient -> gateway "POST /v1/chat/completions；头带代理密钥" "HTTP/1.1 over localhost:8000"

        gateway -> openAiApi "转发规范化后的 chat/completions 请求" "HTTPS + SSE"
        gateway -> anthropicApi "转发规范化后的 messages 请求（Provider 原生即 Anthropic 时近乎直传）" "HTTPS + SSE"

        gateway.appExe -> gateway.configDb "读写 Provider/Model/真实Key密文/代理密钥哈希/路由档/日志行" "Microsoft.Data.Sqlite"

        gateway.appExe.wpfViews -> gateway.appExe.viewModels "数据绑定 / 命令" "XAML Binding"
        gateway.appExe.viewModels -> gateway.appExe.activationState "读当前激活档、写入切换" "in-process"
        gateway.appExe.viewModels -> gateway.appExe.repositories "增删改 Provider 元数据（经 Core 仓储端口；不含 Key）" "in-process"
        gateway.appExe.viewModels -> gateway.appExe.secretProtector "保存 / 删除真实 Key：经 Core 只写端口 IRealKeyVault，签名上不可读回（S1 review）" "in-process"
        gateway.appExe.viewModels -> gateway.appExe.upstreamClients "点击「测试连通性」" "in-process"
        gateway.appExe.wpfViews -> gateway.appExe.compositionRoot "App 入口在 UI 线程上调 AddAiGateway()：装配非 UI 实现并启动 Kestrel（C2 修正：组合根不引用 UI，VM 由 App 自己注册）" "in-process"
        gateway.appExe.compositionRoot -> gateway.appExe.httpEndpoints "在同进程后台线程启动 Kestrel" "in-process"
        gateway.appExe.compositionRoot -> gateway.appExe.activationState "装载初始路由快照" "in-process"

        gateway.appExe.httpEndpoints -> gateway.appExe.proxyKeyAuthenticator "每个 /v1 请求先过鉴权（/health 除外）" "in-process"
        gateway.appExe.proxyKeyAuthenticator -> gateway.appExe.repositories "每请求现查：只取哈希与 enabled 位（无缓存，D10）" "in-process"
        gateway.appExe.proxyKeyAuthenticator -> gateway.appExe.runLog "401 也留痕，只记指纹" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.routeResolver "携已认证 RequestContext 解析目标 Provider 与真实 model" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.anthropicCodec "入站 Anthropic：请求解码 / 出站事件编码" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.openAiCodec "入站 OpenAI：请求解码 / 出站 chunk 编码" "in-process"
        gateway.appExe.routeResolver -> gateway.appExe.activationState "读不可变激活快照（D7：路由唯一来源）" "in-process"
        gateway.appExe.compositionRoot -> gateway.appExe.httpListenerPolicy "按存好的端口启动监听；地址写死回环" "in-process"

        gateway.appExe.anthropicCodec -> gateway.appExe.canonicalModel "入站协议 → 规范请求" "in-process"
        gateway.appExe.openAiCodec -> gateway.appExe.canonicalModel "入站协议 → 规范请求" "in-process"
        gateway.appExe.anthropicCodec -> gateway.appExe.toolCallBridge "tool_use 块增量编码" "in-process"
        gateway.appExe.openAiCodec -> gateway.appExe.toolCallBridge "tool_calls 分片解码" "in-process"
        gateway.appExe.canonicalModel -> gateway.appExe.upstreamClients "规范请求交给出站客户端" "in-process"

        gateway.appExe.streamRelay -> gateway.appExe.anthropicCodec "重编码为 messages SSE 事件" "in-process"
        gateway.appExe.streamRelay -> gateway.appExe.openAiCodec "重编码为 completion chunk" "in-process"
        gateway.appExe.streamRelay -> gateway.appExe.toolCallBridge "累积 arguments 分片为 partial_json" "in-process"
        gateway.appExe.upstreamClients -> gateway.appExe.streamRelay "上游 SSE 原始帧" "in-process"
        gateway.appExe.streamRelay -> gateway.appExe.httpEndpoints "逐事件回写并 flush 到入站连接" "in-process"

        gateway.appExe.credentialInjector -> gateway.appExe.secretProtector "现场解密（全工程唯一解密调用点）" "in-process"
        gateway.appExe.secretProtector -> gateway.appExe.repositories "IRealKeyVault 实现：加密后只存密文字节（C1 修正：Storage 不再反向依赖 Security）" "in-process"
        gateway.appExe.credentialInjector -> gateway.appExe.repositories "按【全局激活档】指向的 Provider 取 DPAPI 密文（D7）" "in-process"
        gateway.appExe.credentialInjector -> gateway.appExe.upstreamClients "作为其 HttpClient 管道处理器，在出站请求经过时写入真实 Key 鉴权头（唯一跨界赋值点；运行时装配，非编译期依赖）" "in-process"

        gateway.appExe.httpEndpoints -> gateway.appExe.runLog "每请求收尾写一行" "in-process"
        gateway.appExe.upstreamClients -> gateway.appExe.failoverPolicy "429/5xx 交由重试与轮转（Phase 2）" "in-process"
        gateway.appExe.secretProtector -> dpapi "调用 OS 主密钥" "P/Invoke"
        gateway.appExe.compositionRoot -> gateway.appExe.proxyKeyAuthenticator "装载鉴权中间件到管道" "in-process"

        gateway.appExe.upstreamClients -> openAiApi "HTTPS 出站调用" "HTTPS + SSE"
        gateway.appExe.upstreamClients -> anthropicApi "HTTPS 出站调用" "HTTPS + SSE"
    }

    views {
        systemContext gateway "SystemContext" "L1 —— 谁用这个网关，它直连哪些外部系统。全部为 target 状态（尚无代码）。" {
            include *
            autoLayout lr
        }

        container gateway "Containers" "L2 —— 唯一可部署单元 AiGateway.App.exe（WPF UI 与 Kestrel 同进程）+ 本机 SQLite 数据仓。对外只发可撤销的代理密钥，真实 Key 明文永不离开进程。" {
            include *
            autoLayout lr
        }

        component gateway.appExe "Components" "L3 —— appExe 内 19 个组件（18 MVP + 1 Phase 2）。两条不许打折的脊柱：① 入站协议 → 规范模型 → 出站协议，两个 codec 永不可互引；② 代理密钥只决定「能不能进」(D7)，路由只来自全局激活快照，真实 Key 只在 credentialInjector 一处现身。" {
            include *
            autoLayout tb
        }

        styles {
            element "Person" {
                shape person
                background #08427b
                color #ffffff
            }
            element "External" {
                background #999999
                color #ffffff
            }
            element "InboundClient" {
                shape hexagon
            }
            element "Upstream" {
                shape pipe
            }
            element "Database" {
                shape cylinder
                background #4d6fa8
                color #ffffff
            }
            element "Deployable" {
                background #2e5f8a
            }
            element "OsCapability" {
                shape circle
                background #b8860b
                color #ffffff
            }
            element "UI" {
                background #6b8cba
            }
            element "Core" {
                background #1f6f5c
                color #ffffff
            }
            element "Mapping" {
                background #7b5ea7
                color #ffffff
            }
            element "Gateway" {
                background #3a6ea5
                color #ffffff
            }
            element "Provider" {
                background #2f7d6b
                color #ffffff
            }
            element "Security" {
                background #a33b3b
                color #ffffff
            }
            element "Storage" {
                background #5c5c5c
                color #ffffff
            }
            element "Hosting" {
                background #556b7b
                color #ffffff
            }
            element "Phase2" {
                dashed true
                opacity 70
            }
            element "LowConfidence" {
                dashed true
            }
            edge "in-process" {
                dashed true
            }
            edge "SSE" {
                color #c0392b
            }
        }
    }
}
