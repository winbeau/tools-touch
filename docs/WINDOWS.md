# Windows 运行与验收

## 使用便携包

解压生成的 `ToolsTouch-win-x64*.zip` 到当前用户可写目录，运行 `ToolsTouch.exe`。包包含 .NET Windows x64 运行时、Node.js 和固定版本 Pi 依赖，不要求最终用户另外安装开发环境。

当前包只经过 Linux 交叉编译及核心自动化测试，尚未完成原生 Windows UI、DPAPI 或真实账号验收。不要将构建成功当作完整验收。

应用只允许运行一个实例。本地数据在 `%LOCALAPPDATA%\ToolsTouch`：SQLite、CV、论文、设置；`pi` 子目录仅用于 Pi 认证及会话，`credentials\gmail.dpapi` 由 Windows 当前用户 DPAPI 保护。

不要把 Gmail 凭据放入 Pi 目录。迁移到另一台机器或 Windows 用户时，应重新连接 Gmail；DPAPI 凭据不适合跨用户直接复制。

## 首次配置

1. 在 Settings 点击“启动 / 重新连接 Pi”，再点“OpenAI 登录”。点击“打开登录浏览器”完成 Pi 提供的 OAuth 流程；只有 Pi 要求时才填写手动授权码。WPF 不交换、保存或读取 OpenAI Token。认证仍由 Pi 的 Provider 管理。
2. 选择 Pi 返回的模型，保存设置。当前状态区区分已配置凭据与实际调用可用性；真实模型调用仍是最终可用性的验证。
3. 网页搜索需要 Brave Search API 凭据。把密钥保存到本机私有文本文件，在“Agent 与检索配置”填入文件路径。只由 C# 搜索服务读取，不传给 Pi。账户方案、余额和配额须由 API 凭据持有人确认；没有配置时发现任务返回明确错误，不生成假结果。
4. 导入 CV PDF，核对提取文本，填写研究兴趣、项目和技能，点击确认保存。未确认的 CV 内容不会作为个人经历传给模型；每次确认创建新版本，正在执行的任务固定已有版本。
5. Gmail：在自己的 Google Cloud 项目启用 Gmail API，创建 **Desktop app** OAuth 客户端，下载客户端 JSON。在 Settings 填入该文件路径并点击连接 Gmail。授权请求仅包含 `gmail.send` 和 `gmail.readonly`。Google 测试模式需将使用账号列为测试用户；公开发布前处理相应 OAuth 验证要求。

资料保存在本地；模型任务会把选取的网页、论文片段及已确认经历发送给当前 OpenAI Provider。Gmail 的刷新凭据与邮箱内容不会交给 Agent。

## 核心操作

- Discover 输入研究方向，点击发现导师。结果需来自实际检索及网页核实；没有 CV 仍可发现导师。
- Professors 选择导师并打开详情；深入分析执行阅读和分析两个阶段，保存可恢复检查点。
- 论文检索优先使用 arXiv，空结果或来源不可用时使用 Crossref；限制单并发，arXiv 至少间隔 3 秒，Crossref 至少 1 秒。来源仍须核实相关性。
- 论文阅读可以指定起始页码，显示摘要或每次最多 10 页的全文片段。arXiv PDF 固定检索结果的版本。获取不到全文、扫描 PDF 或提取失败时不会声称已经读过全文；当前没有 OCR。
- 生成草稿创建新版本。Outreach 编辑正文、检查收件人和附件、保存，再点击 Send 并确认。只有这个人工入口发送邮件。
- `Unknown` 表示发送结果不确定：点击核对发送结果。只有找到相同 Message-ID 与收件人的已发送邮件才转为 Sent；未找到仍保留 Unknown，不自动重发。
- 点击同步回复，根据已发送邮件的 Gmail threadId 更新本地回复记录。目前只关联同线程回复。
- 导师详情的投递历史显示草稿版本、发送及回复状态，可直接打开对应邮件。

## 从源码运行

开发机需要 .NET SDK 10.0.400、Node.js 24。仓库根目录运行：

```powershell
npm --prefix agent-host ci --ignore-scripts
npm --prefix agent-host test
dotnet run --project tests/ToolsTouch.Core.Tests
dotnet run --project src/ToolsTouch.Desktop
```

源码启动时，在 Settings 将 AgentHost 路径设为仓库 `agent-host\dist\index.js`；Node 填安装位置或 PATH 中的 `node`。便携包默认自动使用随包 Node 和 AgentHost。

构建便携包还需要 Python 3：

```powershell
python scripts/package.py
```

脚本先运行测试，再交叉发布 Windows x64，安装锁定的 Windows 生产依赖，下载并校验官方 Node 压缩包的 SHA-256。脚本不发布、不签名、不调用真实模型、不发送真实邮件；已有输出不会被覆盖，可用 `--output` 指定新目录。

可用 `python scripts/verify-package.py artifacts/ToolsTouch-win-x64-r2` 验证生成目录的全部文件哈希。校验只证明产物与构建清单一致，不是代码签名或 Windows 运行验证。

## Windows 必须补做的验收

- 干净 Windows 用户解压运行；确认六页渲染、选择、滚动、命令可用状态和错误提示正常。
- OpenAI 真实登录、模型调用、八工具往返、取消任务及重启恢复。
- Brave 真实凭据检索，核实 World Model 导师身份、至少一篇论文及相应邮件草稿，检查来源是否支撑结论。
- Gmail OAuth 真实回调、重新启动后刷新、DPAPI 保存与重新连接。
- 发送可先用自动化替身验证。只有用户在界面操作或另行明确授权，才发送真实测试邮件；之后验证已发送记录和实际回复同步。
- 人工编辑后重新生成不覆盖旧草稿；连续点击不重复发；发送时断网保持明确失败或 Unknown；Unknown 核对不擅自重发。

官方参考：[Google 桌面 OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)、[Gmail 发送指南](https://developers.google.com/workspace/gmail/api/guides/sending)、[Gmail 权限](https://developers.google.com/workspace/gmail/api/auth/scopes)。
