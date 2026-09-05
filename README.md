# Tools Touch

正在实现的 Windows WPF 导师发现与投递工作台。Pi 负责发现、论文分析与起草；C# 应用服务负责本地数据、人工发送和回复状态。

Windows 安装版：[前往 GitHub Releases](https://github.com/winbeau/tools-touch/releases)。下载 `ToolsTouch-Setup-*-win-x64.exe` 后双击安装，内置运行环境，无需管理员权限。卸载保留本地研究资料和账户数据；真实账号完整流程仍待验收。

已提供六页 WPF、研究服务、Pi 登录适配和 Gmail 流程实现，并补充原生 Windows 桌面回归检查；真实账号端到端验收仍未完成。完整范围与未完成项见 [实施记录](docs/IMPLEMENTATION.md)。

Windows 使用、配置与打包步骤见 [WINDOWS.md](docs/WINDOWS.md)。更新后的便携包目标为 `artifacts/ToolsTouch-win-x64-r3.zip`，内含 .NET、Node 和 Pi 依赖；原生自动检查不等于真实账号完整验收。逐项证据与缺口见 [ACCEPTANCE.md](docs/ACCEPTANCE.md)。

## 已有核心验证

安装 .NET SDK 10.0.400 与 Node.js 24 后，从仓库根目录依次执行：

```sh
npm --prefix agent-host ci --ignore-scripts
npm --prefix agent-host test
dotnet run --project tests/ToolsTouch.Core.Tests
```

这是使用真实临时 SQLite 数据库的可执行回归测试，失败时返回非零退出码。邮件传输使用测试替身，不发送真实邮件。测试工程可在 Linux 和 Windows 运行；通过不代表 WPF 或 OAuth 已完成验收。

Windows 原生检查（额外需要 Python 3）会保存页面截图和 JSON 结果；所有测试使用隔离资料，不登录或发送真实邮件：

```powershell
python scripts/test-windows.py --package artifacts/ToolsTouch-win-x64-r3 --output artifacts/windows-check
```

本次检查 7 组全部通过，包括六页及三个详情子页渲染、草稿编辑与空选择清理、DPAPI 跨进程读取、随包 Pi 状态握手及退出死锁回归。WSL 交叉构建后的运行方法见 [Windows 原生检查](docs/WINDOWS.md#可重复的-windows-原生检查)。

核心回归包含实际 C# ↔ Node/Pi 子进程握手，因此需要先构建 AgentHost。可选的真实来源连通性检查：

```sh
dotnet run --project tests/ToolsTouch.Core.Tests -- --live-sources
```

该命令查询 arXiv/Crossref、下载固定版本的 World Models PDF 并抓取公开主页；不调用付费 LLM、不登录、不发送邮件。它验证来源连通性及解析，不证明导师检索质量或完整产品流程。

AgentHost 依赖固定到与现有 `pi/` 相同的 0.85.0 版本，可单独启动：

```sh
cd agent-host
npm ci --ignore-scripts
npm test
npm run build
node dist/index.js /path/to/dedicated-pi-storage
```

`pi/` 保留为上游参考源码，应用不修改其中的认证或 Agent Loop。

AgentHost 使用 JSONL stdin/stdout；支持 `status`、`login`、`auth_reply`、`run`、`cancel`、`tool_result`。C# 的 AgentBridge 分发工具请求，DiscoveryService 保存阶段检查点和结构化分析。Pi 存储目录只能用于 Pi 认证和会话，不要放 Gmail 凭据。WPF 提供两个登录入口；OpenAI 交换与存储凭据仍由 Pi 执行，Gmail 凭据由当前 Windows 用户 DPAPI 保护。

网页检索适配器使用 Brave Search API，未配置凭据返回 `WEB_SEARCH_NOT_CONFIGURED`；不会静默生成模拟结果。学术检索使用无需用户登录的 arXiv（单并发、至少间隔 3 秒）与 Crossref（单并发、至少间隔 1 秒）。Brave 真实配额与检索质量验收仍待完成。
