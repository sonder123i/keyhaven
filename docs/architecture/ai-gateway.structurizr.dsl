workspace "AI 本地代理网关" "Windows WPF 桌面端 AI 代理网关 —— target-state（尚无业务代码）。2026-10-15 按 review 决策重写：只收 Anthropic 入站；出站分直通（Anthropic 兼容上游）与翻译（OpenAI，第二里程碑）；JSON 配置；3 个产品程序集 + 1 个测试项目。" {

    !identifiers hierarchical

    model {
        operator = person "桌面操作者" "录入上游 Key、生成 / 吊销代理密钥、一键切换当前模型、看运行日志。"

        claudeCode = softwareSystem "Claude Code CLI" "唯一入站客户端，只说 Anthropic Messages 协议。ANTHROPIC_BASE_URL=http://127.0.0.1:端口号，ANTHROPIC_AUTH_TOKEN=代理密钥。推理请求发往 /v1/messages?beta=true，带 anthropic-version / anthropic-beta 头；count_tokens 缺失时自行按字符估算。" {
            tags "External,InboundClient"
        }
        localUntrusted = softwareSystem "本机不受信来源" "浏览器里的任意网页（含 DNS rebinding）与其他本机程序。监听写死 127.0.0.1 挡不住它们 —— 这正是代理密钥鉴权的防御职责（D6）。" {
            tags "External,Threat"
        }

        gateway = softwareSystem "AI 本地代理网关" "单机桌面应用：真实上游 Key 以 DPAPI 密文存在本机 JSON 配置里；对外只发可吊销的代理密钥。Anthropic 兼容上游走直通，OpenAI 上游走翻译。" {

            appExe = container "AiGateway.App.exe" "唯一可部署单元，UI 与 Kestrel 同进程（D3）。由 3 个程序集组成：AiGateway.App（WPF + 组合根 + 托盘）、AiGateway（网关、路由、直通、翻译、配置、日志；不引用 Security）、AiGateway.Security（DPAPI 与注入处理器；全工程唯一能解密）。各库默认 internal，只公开注册方法与两个端口（IRealKeyVault 只写、ICiphertextStore 只存取密文）。" "C# / .NET 8 + WPF + ASP.NET Core Kestrel" {
                tags "Deployable"

                compositionRoot = component "组合根与宿主" "[AiGateway.App] 调 GatewayServer.Create 并在装配回调里调 AddAiGatewaySecurity()；Kestrel 由空宿主构建器起，不读任何环境变量与 appsettings（否则 Kestrel__Endpoints__* 会多开 0.0.0.0 监听，S3 实测）；端口被占时弹出可读提示后退出；单实例 Mutex；托盘常驻：关窗只隐藏，托盘菜单才退出（D3 的必然要求，否则关窗即断 Claude Code）。" ".NET Generic Host + DI" {
                    tags "App"
                }
                wpfViews = component "WPF 界面" "[AiGateway.App] 四类页面（D5）：Provider/模型管理、代理密钥管理、一键切换、运行日志。代理密钥明文只在生成时显示一次，旁边给出可复制的 Claude Code 配置片段；不改写用户的 settings.json。" "XAML" {
                    tags "App"
                }
                viewModels = component "ViewModels" "[AiGateway.App] 只经端口操作：配置存储、只写密钥库、激活快照、日志读取。" "CommunityToolkit.Mvvm（待定）" {
                    tags "App"
                }

                httpEndpoints = component "本地 HTTP 入口" "[AiGateway] POST /v1/messages（含 ?beta=true 查询串）、POST /v1/messages/count_tokens、GET /health。地址写死 127.0.0.1，只有端口可改（D9）。" "ASP.NET Core Minimal API" {
                    tags "Lib"
                }
                proxyKeyAuthenticator = component "代理密钥验证器" "[AiGateway] 从 x-api-key 或 Authorization: Bearer 取代理密钥，SHA-256 后与已启用哈希的内存快照做 FixedTimeEquals（D8/D10）。快照随配置写入整体替换，吊销即刻生效，不每请求读盘。失败回 Anthropic 形状 401。" "SHA-256 + FixedTimeEquals" {
                    tags "Lib"
                }
                activationState = component "激活快照" "[AiGateway] 当前激活的 (Provider, 上游类型, BaseUrl, 真实模型名)。不另起数据：就是配置快照 ConfigStore.Current 里的激活项，切换即整体替换配置快照（D7：路由唯一来源）；路由每次只读一份快照，不会读到半新半旧。" "ConfigStore.Current（不可变 record，Volatile 替换）" {
                    tags "Lib"
                }
                routeResolver = component "路由解析" "[AiGateway] 读激活快照，决定走直通还是翻译、真实模型名是什么。不读代理密钥的任何属性。" "C#" {
                    tags "Lib"
                }
                passthroughForwarder = component "直通转发器" "[AiGateway] 上游原生说 Anthropic 协议时使用（Anthropic、DeepSeek /anthropic）。请求体只改顶层 model 字段，其余原样；转发 anthropic-version / anthropic-beta 与原查询串；响应状态码与 SSE 字节原样逐块回流并 flush；客户端中止即取消上游。thinking、cache_control 等一律原样携带。" "JsonNode 单字段改写 + 流式复制" {
                    tags "Lib"
                }
                translator = component "Anthropic → OpenAI 翻译器" "[AiGateway] 上游只说 chat/completions 时使用（第二里程碑）。请求翻译（带 stream_options.include_usage）；流式把 OpenAI chunk 重编码为 Anthropic 事件序列并合成 usage；按 D4 只保证 tool_use，thinking 在这条路上丢弃。无状态单例，每请求状态放在上下文对象里。" "System.Text.Json" {
                    tags "Lib,Milestone2"
                }
                toolCallSession = component "工具调用增量重编码" "[AiGateway] OpenAI tool_calls 扁平分片（index / 首片 id+name / 后续 arguments、无结束标记）→ Anthropic content_block_start / input_json_delta / content_block_stop，块索引严格配对。全设计唯一的协议难点。" "C#" {
                    tags "Lib,Milestone2,HighRisk"
                }
                upstreamClients = component "上游 HTTP 客户端" "[AiGateway] IHttpClientFactory 命名客户端；只在请求选项里设 provider id，代码不经手真实 Key，也不知道注入器存在。不跟随重定向（x-api-key 会被带到别的主机）；移除全部内置日志处理器（S5 实测：Trace 级会原样记录真实 Key，打码只盖格式化文本、盖不住结构化字段）。" "IHttpClientFactory" {
                    tags "Lib"
                }
                configStore = component "配置存储" "[AiGateway] 读写 config.json：Provider 元数据、Key 密文（含 protector_id / key_version）、代理密钥哈希（含 algo_version / enabled）、激活档、端口。原子写；启动时读一次，之后以内存快照为准。" "System.Text.Json" {
                    tags "Lib"
                }
                runLog = component "运行日志" "[AiGateway] 内存环形缓冲，一行一请求：时间、代理密钥指纹、上游、模型、状态、首字节耗时、中止原因。重启清空。不含请求体、响应内容、任何密钥明文。" "内存环形缓冲" {
                    tags "Lib"
                }

                credentialInjector = component "真实密钥注入处理器" "[AiGateway.Security] HttpClient 管道里的 DelegatingHandler，由组合根经 Security 唯一的公开注册方法挂进上游客户端；没有任何组件能调用它。按请求选项里的 provider id 取密文 → 解密 → 先清掉请求上已有的 x-api-key 与 Authorization，再写入真实 Key 的 x-api-key；请求发完立即删掉该头，持有请求对象的代码事后读不到。不构造、不发起请求。" "DelegatingHandler" {
                    tags "Security,HighRisk"
                }
                secretProtector = component "DPAPI 保护器与只写密钥库" "[AiGateway.Security] DPAPI CurrentUser + 每 Provider 独立 entropy。对外只实现只写端口 IRealKeyVault（Save / Delete / Exists），签名上拿不到明文；解密只供注入处理器在程序集内部使用。" "System.Security.Cryptography.ProtectedData" {
                    tags "Security"
                }
            }

            configFile = container "config.json" "%LOCALAPPDATA% 下的单个 JSON 文件。真实 Key 只以 DPAPI 密文出现；代理密钥只以 SHA-256 出现（D8，不可回显）。" "JSON 文件 + DPAPI 加密字段" {
                tags "File"
            }
        }

        dpapi = softwareSystem "Windows DPAPI" "OS 用户主密钥体系。防其他 Windows 用户与离线拿走硬盘；防不住以本人身份运行的恶意程序（它能直接调解密）。换机、换账户、重置密码后密文可能不可读。" {
            tags "External,OsCapability"
        }
        anthropicApi = softwareSystem "Anthropic 兼容上游" "原生说 Messages 协议的上游：api.anthropic.com、https://api.deepseek.com/anthropic 等。DeepSeek 支持 thinking / tools / 图片，忽略 anthropic-beta 与 cache_control，未知模型名映射到默认模型。" {
            tags "External,Upstream"
        }
        openAiApi = softwareSystem "OpenAI Chat 上游" "只说 /v1/chat/completions 的上游（api.openai.com 等），需要翻译。第二里程碑。" {
            tags "External,Upstream,Milestone2"
        }

        operator -> gateway "管理 Key 与代理密钥、一键切换、看日志" "Keyboard/Mouse"
        claudeCode -> gateway.appExe.httpEndpoints "POST /v1/messages?beta=true（SSE）与 /v1/messages/count_tokens；头带代理密钥" "HTTP/1.1 + SSE，127.0.0.1"
        localUntrusted -> gateway.appExe.httpEndpoints "可以向回环端口发请求；拿不到代理密钥 → 401" "HTTP"

        gateway.appExe.compositionRoot -> gateway.appExe.wpfViews "启动主窗口与托盘图标" "in-process"
        gateway.appExe.compositionRoot -> gateway.appExe.httpEndpoints "后台线程启动 Kestrel：地址写死 127.0.0.1，端口取自配置" "in-process"
        gateway.appExe.compositionRoot -> gateway.appExe.credentialInjector "经 Security 唯一的公开注册方法，把注入处理器挂进上游 HttpClient 管道" "in-process"
        gateway.appExe.wpfViews -> gateway.appExe.viewModels "数据绑定 / 命令" "XAML Binding"
        gateway.appExe.viewModels -> gateway.appExe.configStore "增删改 Provider 与端口；生成代理密钥时只写入其 SHA-256" "in-process"
        gateway.appExe.viewModels -> gateway.appExe.secretProtector "保存 / 删除真实 Key：只写端口 IRealKeyVault，签名上不可读回" "in-process"
        gateway.appExe.viewModels -> gateway.appExe.activationState "一键切换：整体替换不可变快照" "in-process"
        gateway.appExe.viewModels -> gateway.appExe.runLog "只读最近若干行" "in-process"

        gateway.appExe.httpEndpoints -> gateway.appExe.proxyKeyAuthenticator "/v1/* 先鉴权（/health 除外）" "in-process"
        gateway.appExe.proxyKeyAuthenticator -> gateway.appExe.configStore "读已启用代理密钥哈希的内存快照（写入即替换）" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.routeResolver "解析目标上游与真实模型" "in-process"
        gateway.appExe.routeResolver -> gateway.appExe.activationState "读不可变快照（D7）" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.passthroughForwarder "上游是 Anthropic 兼容：直通" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.translator "上游是 OpenAI：翻译（第二里程碑）" "in-process"
        gateway.appExe.httpEndpoints -> gateway.appExe.runLog "每请求收尾写一行，只记代理密钥指纹" "in-process"

        gateway.appExe.passthroughForwarder -> gateway.appExe.upstreamClients "原样请求体（只改 model）+ anthropic-version / anthropic-beta + 原查询串" "in-process"
        gateway.appExe.translator -> gateway.appExe.toolCallSession "tool_calls 分片 → content_block 生命周期" "in-process"
        gateway.appExe.translator -> gateway.appExe.upstreamClients "chat/completions 请求（stream_options.include_usage）" "in-process"

        gateway.appExe.credentialInjector -> gateway.appExe.upstreamClients "作为其管道处理器，在出站请求经过时写真实 Key 鉴权头（运行时装配，两程序集互不引用）" "in-process"
        gateway.appExe.credentialInjector -> gateway.appExe.secretProtector "现场解密（全工程唯一解密调用点）" "in-process"
        gateway.appExe.credentialInjector -> gateway.appExe.configStore "按 provider id 取密文" "in-process"
        gateway.appExe.secretProtector -> gateway.appExe.configStore "加密后只写密文" "in-process"
        gateway.appExe.secretProtector -> dpapi "CryptProtectData / CryptUnprotectData（CurrentUser + 每 Provider entropy）" "P/Invoke"
        gateway.appExe.configStore -> gateway.configFile "启动读一次；写入用临时文件 + 替换" "System.Text.Json"

        gateway.appExe.upstreamClients -> anthropicApi "HTTPS；真实 Key 只在这条边上离开进程" "HTTPS + SSE"
        gateway.appExe.upstreamClients -> openAiApi "HTTPS（第二里程碑）" "HTTPS + SSE"
    }

    views {
        systemContext gateway "SystemContext" "L1 —— 谁用这个网关、它连哪些外部系统、威胁来自哪里。" {
            include *
            autoLayout lr
        }

        container gateway "Containers" "L2 —— 唯一可部署单元 AiGateway.App.exe + 本机 config.json。" {
            include *
            autoLayout lr
        }

        component gateway.appExe "Components" "L3 —— 15 个组件，按程序集着色。两条脊柱：① 入站只有 Anthropic，出站分直通与翻译；② 代理密钥只决定能不能进，真实 Key 只在注入处理器一处解密、只沿上游客户端这一条边离开进程。" {
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
            element "Threat" {
                background #b91c1c
                color #ffffff
                border dashed
            }
            element "Upstream" {
                shape pipe
            }
            element "File" {
                shape folder
                background #4d6fa8
                color #ffffff
            }
            element "Deployable" {
                background #2e5f8a
                color #ffffff
            }
            element "OsCapability" {
                shape circle
                background #b8860b
                color #ffffff
            }
            element "App" {
                background #6b8cba
                color #ffffff
            }
            element "Lib" {
                background #3a6ea5
                color #ffffff
            }
            element "Security" {
                background #a33b3b
                color #ffffff
            }
            element "Milestone2" {
                border dashed
                opacity 70
            }
        }
    }
}
