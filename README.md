# Tools Touch

正在实现的 Windows WPF 导师发现与投递工作台。Pi 负责发现、论文分析与起草；C# 应用服务负责本地数据、人工发送和回复状态。

整体扩展架构见 [设计与分步编码计划](docs/design/README.md)：学校／学院窗口、导师采集、两级推荐、投递管理，以及支持自定义字段与全量导出的表格记录器。37 个实施工作包已完成；真实账号、来源范围和原生 Windows 人工验收按矩阵单独记录。

新对话编码从 [Luna 接手入口](docs/design/START-HERE.md) 开始，按 [实施状态](docs/design/IMPLEMENTATION-STATE.md) 续接；文件迁移、37 个工作包、核心协议与黄金验收样例已列入设计。

Windows 安装版：[下载 v0.3.3 安装器](https://github.com/winbeau/tools-touch/releases/download/v0.3.3/ToolsTouch-Setup-0.3.3-win-x64.exe) · [发布说明](docs/releases/v0.3.3.md)。下载后双击安装，内置运行环境，无需管理员权限。卸载保留本地研究资料和账户数据；真实账号完整流程仍待验收。

v0.3.3 将 11 个 WPF 页面整理为七组左侧导航，统一浅色工作台、邮件编辑区及表格工具栏，保留未保存编辑；23 组原生桌面自动检查通过。研究服务、Pi 登录适配和 Gmail 流程均保留；真实账号端到端验收仍未完成。完整范围与未完成项见 [实施记录](docs/IMPLEMENTATION.md)。

Windows 使用、配置与打包步骤见 [WINDOWS.md](docs/WINDOWS.md)。便携包内含 self-contained .NET、Node、Pi 和 Python collector 生产依赖；原生自动检查不等于真实账号完整验收。逐项证据与缺口见 [ACCEPTANCE.md](docs/ACCEPTANCE.md)。

## v0.2.1 公众安装准备

首次打开使用 Gmail 登录，登录邮箱同时作为投递邮箱，成功后进入工作台并加密保存登录状态。打开程序自动加载组件并显示进度；Settings 可选择服务商、浏览器授权、设备码或 API Key。修复 OpenAI 授权选项未提交导致后续登录一直忙碌的问题，增加取消和重试。Gmail 支持导入并校验桌面客户端 JSON、授权阶段显示和具体失败原因。v0.2.1 安装包内置 Google 桌面客户端配置，普通用户无需导入 JSON。Gmail API 已启用，但 Google 应用仍处于测试状态，品牌资料和权限审核未完成，目前不能保证任意 Google 账号均可登录；真实 Google/OpenAI 账户端到端验证仍需完成。

新增应用诊断日志及导出入口，保留 14 天，不记录密钥、授权码或正文。详细配置与发布者预置 Google 客户端的方法见 Windows 文档。

应用主页与隐私说明：[GitHub Pages](https://winbeau.github.io/tools-touch/)。公开审核与打包要求见 [PUBLIC-RELEASE.md](docs/PUBLIC-RELEASE.md)。

## 已有核心验证

安装 .NET SDK 10.0.400、Node.js 24、pnpm 10.14.0、uv 0.9.17 后，从仓库根目录依次执行：

```sh
pnpm install --frozen-lockfile --ignore-scripts
pnpm --filter tools-touch-agent-host test
uv sync --all-packages --locked
uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'
dotnet run --project tests/ToolsTouch.Core.Tests
```

这是使用真实临时 SQLite 数据库的可执行回归测试，失败时返回非零退出码。邮件传输使用测试替身，不发送真实邮件。测试工程可在 Linux 和 Windows 运行；通过不代表 WPF 或 OAuth 已完成验收。

Windows 原生检查（额外需要 Python 3）会保存页面截图和 JSON 结果；所有测试使用隔离资料，不登录或发送真实邮件：

```powershell
python scripts/test-windows.py --package artifacts/ToolsTouch-win-x64-v0.2.1 --output artifacts/windows-check
```

v0.2.0 原生检查 12 组全部通过，包括六页及三个详情子页渲染、草稿编辑与空选择清理、DPAPI 跨进程读取、首次 Gmail 登录门槛与模拟回调、模型密钥配置、随包组件状态握手及退出死锁回归。WSL 交叉构建后的运行方法见 [Windows 原生检查](docs/WINDOWS.md#可重复的-windows-原生检查)。

核心回归包含实际 C# ↔ Node/Pi 子进程握手，因此需要先构建 AgentHost。可选的真实来源连通性检查：

```sh
dotnet run --project tests/ToolsTouch.Core.Tests -- --live-sources
```

该命令查询 arXiv/Crossref、下载固定版本的 World Models PDF 并抓取公开主页；不调用付费 LLM、不登录、不发送邮件。它验证来源连通性及解析，不证明导师检索质量或完整产品流程。

AgentHost 依赖固定到与现有 `pi/` 相同的 0.85.0 版本，可单独启动：

```sh
cd agent-host
pnpm --dir .. --filter tools-touch-agent-host check
pnpm --dir .. --filter tools-touch-agent-host test
pnpm --dir .. --filter tools-touch-agent-host build
node dist/index.js /path/to/dedicated-pi-storage
```

`pi/` 保留为上游参考源码，应用不修改其中的认证或 Agent Loop。

AgentHost 使用 JSONL stdin/stdout；支持 `status`、`login`、`auth_reply`、`run`、`cancel`、`tool_result`。C# 的 AgentBridge 分发工具请求，DiscoveryService 保存阶段检查点和结构化分析。Pi 存储目录只能用于 Pi 认证和会话，不要放 Gmail 凭据。WPF 从运行组件读取服务商列表，支持 OpenAI、DeepSeek、Claude、GLM 等的授权和模型配置；模型凭据仍由 Pi 执行交换与存储，Gmail 凭据由当前 Windows 用户 DPAPI 保护。

网页检索适配器使用 Brave Search API，未配置凭据返回 `WEB_SEARCH_NOT_CONFIGURED`；不会静默生成模拟结果。学术检索使用无需用户登录的 arXiv（单并发、至少间隔 3 秒）与 Crossref（单并发、至少间隔 1 秒）。Brave 真实配额与检索质量验收仍待完成。
