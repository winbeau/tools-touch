# Windows 运行与验收

## 使用安装 EXE

从 [GitHub Releases](https://github.com/winbeau/tools-touch/releases) 下载 `ToolsTouch-Setup-*-win-x64.exe`，双击完成安装。默认位置为 `%LOCALAPPDATA%\Programs\ToolsTouch`，仅为当前用户安装，无需管理员权限；开始菜单入口自动创建，桌面快捷方式可选。

运行中的应用会阻止安装和卸载，升级前请先退出 Tools Touch。卸载入口在 Windows“已安装的应用”和开始菜单中；卸载不删除 `%LOCALAPPDATA%\ToolsTouch` 下的数据库、CV、草稿、设置或凭据。当前安装包未做代码签名，下载校验值在 Release 的 `SHA256SUMS.txt` 中。

安装器由 [Inno Setup](https://jrsoftware.org/isinfo.php) 构建。Windows 开发机安装 Inno Setup 6.3 或更新版本和 Python 3 后，可基于已校验的便携包重建：

```powershell
python scripts/build-installer.py --package artifacts/ToolsTouch-win-x64-r3.zip --version 0.1.0 --output artifacts/release-v0.1.0
python scripts/test-installer.py --installer artifacts/release-v0.1.0/ToolsTouch-Setup-0.1.0-win-x64.exe --test-exe artifacts/desktop-tests-win/ToolsTouch.Desktop.Tests.exe --output artifacts/installer-check
```

`ISCC_EXE` 可指定编译器位置。构建器只提取并打包便携包清单内通过 SHA-256 校验的文件；输出安装 EXE、`SHA256SUMS.txt` 与构建元数据，已有输出目录不会覆盖。安装测试要求当前 Windows 用户尚未安装 Tools Touch，会在临时目录执行安装、逐文件校验、重复安装修复、桌面回归及卸载，不使用真实账号或发送邮件。

## 使用便携包

解压生成的 `ToolsTouch-win-x64*.zip` 到当前用户可写的 **Windows 本地目录**，运行 `ToolsTouch.exe`。包包含 .NET Windows x64 运行时、Node.js 和固定版本 Pi 依赖，不要求最终用户另外安装开发环境。不要直接从 `\\wsl.localhost\...` 运行：本次验证中，大小写敏感的 WSL 共享路径导致 WPF 原生 DLL 加载失败，Pi 启动也出现超时；复制到 Windows 本地目录后可运行。

已增加 Windows 11 原生自动检查，覆盖六页与详情子页渲染、界面绑定、草稿编辑、DPAPI 跨进程读取和 Pi 连接后退出。测试用合成资料和独立临时目录；真实账号、人工完整操作及干净 Windows 用户验收仍需完成。不要将自动检查当作完整验收。

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

可用 `python scripts/verify-package.py artifacts/ToolsTouch-win-x64-r3` 验证生成目录的全部文件哈希。校验只证明产物与构建清单一致，不是代码签名或 Windows 运行验证。

## 可重复的 Windows 原生检查

在 Windows 上安装 .NET SDK 10.0.400 和 Python 3 后运行：

```powershell
python scripts/test-windows.py --package artifacts/ToolsTouch-win-x64-r3 --output artifacts/windows-check
```

脚本发布桌面测试程序，将其与包内 Node / AgentHost 复制到 Windows 临时目录，然后在 STA 界面线程上加载实际 MainWindow 与 MainViewModel。每次输出目录必须是新目录，内含 `results.json`、六页及三个详情子页 PNG。整个过程不打开登录流程、不读取正式应用资料、不发送真实邮件，结束后清理临时运行目录。窗口在屏幕外渲染，因此截图是实际 WPF 渲染结果，不是人工操作录像。

如果使用 WSL 交叉构建，可先执行以下命令，再从 Windows 调用上面的脚本并追加 `--test-exe artifacts/desktop-tests-win/ToolsTouch.Desktop.Tests.exe`，Windows 侧无需另外安装 SDK：

```sh
dotnet publish tests/ToolsTouch.Desktop.Tests -c Release -r win-x64 --self-contained true -o artifacts/desktop-tests-win
```

检查范围包括合成凭据加密落盘、另一个 Windows 进程解密、损坏检测与删除；页面切换、草稿保存与未保存内容保留、空选择清理、已发送锁定；实际包内 Pi 的匿名状态握手；以及模拟应用退出时界面线程等待清理的死锁回归。不会证明 Google 刷新凭据、真实模型工具调用或发送成功。

## Windows 必须补做的验收

- 干净 Windows 用户解压运行；确认六页渲染、选择、滚动、命令可用状态和错误提示正常。
- OpenAI 真实登录、模型调用、八工具往返、取消任务及重启恢复。
- Brave 真实凭据检索，核实 World Model 导师身份、至少一篇论文及相应邮件草稿，检查来源是否支撑结论。
- Gmail OAuth 真实回调、重新启动后刷新、DPAPI 保存与重新连接。
- 发送可先用自动化替身验证。只有用户在界面操作或另行明确授权，才发送真实测试邮件；之后验证已发送记录和实际回复同步。
- 人工编辑后重新生成不覆盖旧草稿；连续点击不重复发；发送时断网保持明确失败或 Unknown；Unknown 核对不擅自重发。

官方参考：[Google 桌面 OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)、[Gmail 发送指南](https://developers.google.com/workspace/gmail/api/guides/sending)、[Gmail 权限](https://developers.google.com/workspace/gmail/api/auth/scopes)。
