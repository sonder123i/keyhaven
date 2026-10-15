# 发布与安装（一页）

**产物形态：依赖框架（framework-dependent），win-x64。** 2026-10-15 实测四种产物后选定（数字见 `docs/plan/ai-gateway.implementation-plan.json` 的 S10）：目录 0.37 MB / 9 个文件，热启动到 `/health` 200 约 0.6 秒。自包含最轻也要 79 MB，且"单文件"变体首跑会把 211 MB 解到 `%TEMP%\.net`——都不符合这个项目"轻量极简纯本地"的定位。

## 目标机前提：两个运行时

依赖框架的产物需要目标机装 **.NET 8 的 Desktop Runtime 与 ASP.NET Core Runtime**（两者合起来提供本应用声明的三个共享框架：`Microsoft.NETCore.App`、`Microsoft.WindowsDesktop.App`、`Microsoft.AspNetCore.App`）。官方 8.0 下载页：<https://dotnet.microsoft.com/download/dotnet/8.0>

本机已具备（`dotnet --list-runtimes` 可见 8.0 的三个）。换机或重装系统后再装一次即可。

## 发布

```
dotnet publish src/AiGateway.App/AiGateway.App.csproj -p:PublishProfile=Release-win-x64 -o <目标目录>
```

产出 9 个文件：`AiGateway.App.exe` 加 `AiGateway.App.dll`、`AiGateway.dll`、`AiGateway.Security.dll`、两个 json 与三个 `.pdb`（`.pdb` 只在排查崩溃栈时有用，介意就删掉，不影响运行）。

## 安装（三分钟）

1. 把**整个目录一起**拷到你想放的地方（例如 `D:\AiGateway\`）。依赖框架的产物里，exe 需要同目录的那 3 个 dll 与两个 json——只拿走 exe 是起不来的。
2. 双击 `AiGateway.App.exe`。托盘出现图标，网关在 `http://127.0.0.1:8000` 上跑（端口默认 8000，可在「设置」页改）；主窗口右上角的叉是**隐藏到托盘**，不是退出——真正退出请右键托盘图标选「退出」。
   **放两份副本没意义**：配置不跟程序目录走，两份共用 `%LOCALAPPDATA%\AiGateway\config.json`；而且同一时刻只允许跑一个（单实例）。
3. 窗口里配一次：
   - 「上游与 Key」页：新建上游（DeepSeek 填 `https://api.deepseek.com/anthropic`，模型填 `deepseek-flash, deepseek-v4-pro`）→ 保存 → 把这个上游的 Key 存进去（界面里之后再看不到它）。
   - 「代理密钥」页：生成一把，把弹出的配置片段合并进 Claude Code 的 `~/.claude/settings.json`（或设成同名环境变量）。**这段片段只显示一次。**
   - 顶栏：选上游与模型 → 「切换」。
4. 验证：跑一次 Claude Code 提问；网关「运行日志」页会多出一行，显示密钥指纹、上游、模型、状态与首字节耗时。

## 不做开机自启

默认不装开机自启（2026-10-15 决定）。要开机就自己跑，把 `AiGateway.App.exe` 的快捷方式丢进 `shell:startup` 即可（Win+R 输入 `shell:startup`）。

## 卸载

删掉程序目录，再删 `%LOCALAPPDATA%\AiGateway\`——`config.json` 里存着上游 Key 的 DPAPI 密文与代理密钥的哈希，删掉即彻底清除。

## 出事时看这里

- **端口被占**：启动时会问你要不要改用一个空闲端口；同意后写进配置，Claude Code 配置里的地址也要跟着改。
- **Key 解不开**（换机、换 Windows 账户、重置密码后）：请求会返回 503 并提示重新录入，在「上游与 Key」页重存一次即可。
- **客户端报 401**：代理密钥被吊销或写错了，重新发一把。
- **想确认真在转发**：「运行日志」页只记元数据（时间、指纹、上游、模型、状态、首字节、结束原因），不记请求内容与任何密钥。