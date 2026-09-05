# Windows 运行与验收

## 使用安装 EXE

从 [GitHub Releases](https://github.com/winbeau/tools-touch/releases) 下载 `ToolsTouch-Setup-*-win-x64.exe`，双击完成安装。默认位置为 `%LOCALAPPDATA%\Programs\ToolsTouch`，仅为当前用户安装，无需管理员权限；开始菜单入口自动创建，桌面快捷方式可选。

运行中的应用会阻止安装和卸载，升级前请先退出 Tools Touch。卸载入口在 Windows“已安装的应用”和开始菜单中；卸载不删除 `%LOCALAPPDATA%\ToolsTouch` 下的数据库、CV、草稿、设置或凭据。当前安装包未做代码签名，下载校验值在 Release 的 `SHA256SUMS.txt` 中。

安装器由 [Inno Setup](https://jrsoftware.org/isinfo.php) 构建。Windows 开发机安装 Inno Setup 6.3 或更新版本和 Python 3 后，可基于已校验的便携包重建：

```powershell
python scripts/build-installer.py --package artifacts/ToolsTouch-win-x64-v0.2.0-login.zip --version 0.2.0 --output artifacts/release-v0.2.0-login
python scripts/test-installer.py --installer artifacts/release-v0.2.0-login/ToolsTouch-Setup-0.2.0-win-x64.exe --test-exe artifacts/desktop-tests-v0.2.0-login/ToolsTouch.Desktop.Tests.exe --output artifacts/installer-check
```

`ISCC_EXE` 可指定编译器位置。构建器只提取并打包便携包清单内通过 SHA-256 校验的文件；输出安装 EXE、`SHA256SUMS.txt` 与构建元数据，已有输出目录不会覆盖。安装测试要求当前 Windows 用户尚未安装 Tools Touch，会在临时目录执行安装、逐文件校验、重复安装修复、桌面回归及卸载，不使用真实账号或发送邮件。

## 使用便携包

解压生成的 `ToolsTouch-win-x64*.zip` 到当前用户可写的 **Windows 本地目录**，运行 `ToolsTouch.exe`。包包含 .NET Windows x64 运行时、Node.js、固定版本 Pi 依赖、Windows Python embeddable runtime 和 collector 生产依赖，不要求最终用户另外安装开发环境。构建清单同时记录开发 workspace 的 Python 3.12.12 和随包 Windows runtime 的 Python 3.12.10。不要直接从 `\\wsl.localhost\...` 运行：本次验证中，大小写敏感的 WSL 共享路径导致 WPF 原生 DLL 加载失败，Pi 启动也出现超时；复制到 Windows 本地目录后可运行。

已增加 Windows 11 原生自动检查，覆盖六页与详情子页渲染、界面绑定、草稿编辑、DPAPI 跨进程读取和 Pi 连接后退出。测试用合成资料和独立临时目录；真实账号、人工完整操作及干净 Windows 用户验收仍需完成。不要将自动检查当作完整验收。

应用只允许运行一个实例。本地数据在 `%LOCALAPPDATA%\ToolsTouch`：SQLite、CV、论文、设置；`pi` 子目录仅用于 Pi 认证及会话，`credentials\gmail.dpapi` 由 Windows 当前用户 DPAPI 保护。

不要把 Gmail 凭据放入 Pi 目录。迁移到另一台机器或 Windows 用户时，应重新连接 Gmail；DPAPI 凭据不适合跨用户直接复制。

## 首次登录

首次打开必须使用 Gmail 登录后才能进入工作台。此 Gmail 同时作为本地应用的登录账号和投递邮箱，界面显示当前账号；未登录或授权失败时，研究和资料操作不可用。组件加载在登录页后台自动进行。

授权成功后，刷新凭据由当前 Windows 用户 DPAPI 加密保存；下次打开保留登录。凭据缺失、损坏或无法解密时回到登录页。此版本是本地桌面应用，没有独立服务器账号或云端资料同步，研究资料仍保存在当前 Windows 用户的数据目录。

v0.2.1 安装包已预置发布者的 Google 桌面登录配置，普通用户无需导入 JSON。Google 对外发布和权限审核由发布者完成；审核状态尚未验证，测试模式下仍仅允许项目测试用户。详见 PUBLIC-RELEASE.md。

## 首次配置

1. 打开程序后自动加载组件，顶部显示加载进度和阶段，无需手动启动。先完成 Gmail 登录，再进入 Settings 选择服务商及授权方式；安装版本优先使用随包组件，避免升级后引用已删除的旧便携目录。
2. OpenAI · ChatGPT 账户支持浏览器授权与设备码授权；OpenAI API、DeepSeek、Claude、GLM 等支持组件提供的授权方式。API Key 在密码框输入，保存后清空输入框，不写入普通设置或诊断日志。浏览器授权会自动打开系统浏览器；重复点击受状态限制，未完成可取消再重试。选择模型后保存设置，研究任务使用所选服务商。凭据保存不代表额度或真实调用已经验证。
3. 网页搜索需要 Brave Search API 凭据。把密钥保存到本机私有文本文件，在“检索与任务配置”填入文件路径。只由 C# 搜索服务读取，不传给 Pi。账户方案、余额和配额须由 API 凭据持有人确认；没有配置时发现任务返回明确错误，不生成假结果。
4. 导入 CV PDF，核对提取文本，填写研究兴趣、项目和技能，点击确认保存。未确认的 CV 内容不会作为个人经历传给模型；每次确认创建新版本，正在执行的任务固定已有版本。
5. Gmail 客户端（发布者配置）：在自己的 Google Cloud 项目启用 Gmail API，创建 **Desktop app** OAuth 客户端，下载客户端 JSON。在 Settings 点击“导入 Google 客户端配置”，再连接 Gmail。若不清楚此配置是什么，请联系应用发布者提供；它不是 Gmail 密码。授权请求仅包含 `gmail.send` 和 `gmail.readonly`。Google 测试模式需将使用账号列为测试用户；公开发布前处理相应 OAuth 验证要求。

资料保存在本地；模型任务会把选取的网页、论文片段及已确认经历发送给选定的模型服务商。Gmail 的刷新凭据与邮箱内容不会交给 Agent。

## 日志与登录排错

应用诊断日志在 `%LOCALAPPDATA%\ToolsTouch\logs`，Settings 可打开目录或导出 ZIP。记录加载、授权阶段、分类错误代码和异常类型，不记录原始异常正文、URL、API Key、授权码、CV 或邮件正文；保留 14 天，每日日志超过约 2 MB 时轮换。研究任务事件仍保存在数据库中。

`RUN_BUSY` 表示研究任务未结束；`AUTH_IN_PROGRESS` 表示授权未结束。可取消对应操作再试。OpenAI 回调端口被占用时可改用设备码。Gmail 会区分客户端缺失/类型错误、拒绝授权、权限不足、Gmail API 未启用、授权失效及无法解密旧凭据。

发布者可用 `python scripts/package.py --google-client C:\Private\google-desktop-client.json` 将自己的 Google 桌面 OAuth 客户端配置随包发布；应用会自动发现它。仓库不提供有效的 Google 客户端注册，未带配置的安装包仍需导入。公开分发前需完成该 Google 项目的授权配置及适用验证。

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

开发机需要 .NET SDK 10.0.400、Node.js 24、pnpm 10.14.0、uv 0.9.17 和 Python 3.12.12。仓库根目录运行：

```powershell
pnpm install --frozen-lockfile --ignore-scripts
pnpm --filter tools-touch-agent-host test
uv sync --all-packages --locked
uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'
dotnet run --project tests/ToolsTouch.Core.Tests
dotnet run --project src/ToolsTouch.Desktop
```

源码运行时自动向上查找仓库 `agent-host\dist\index.js`，Node 需位于 PATH；自定义路径可在本地 settings.json 中设置。安装包和便携包自动使用随包组件。

构建便携包还需要 Python 3：

```powershell
uv run --all-packages --locked python scripts/package.py
```

脚本先运行测试，再交叉发布 Windows x64，使用锁定的 uv 依赖安装 collector 的纯 Python Windows 生产依赖，下载并校验官方 Node 与 Python embeddable 压缩包的 SHA-256。脚本不发布、不签名、不调用真实模型、不发送真实邮件；已有输出不会被覆盖，可用 `--output` 指定新目录。

可用 `uv run --all-packages --locked python scripts/verify-package.py artifacts/ToolsTouch-win-x64-v0.2.0-login` 验证生成目录的全部文件哈希。校验只证明产物与构建清单一致，不是代码签名或 Windows 运行验证。

## 可重复的 Windows 原生检查

在 Windows 上安装 .NET SDK 10.0.400 和 Python 3 后运行：

```powershell
python scripts/test-windows.py --package artifacts/ToolsTouch-win-x64-v0.2.0-login --output artifacts/windows-check
```

脚本发布桌面测试程序，将其与包内 Node / AgentHost 复制到 Windows 临时目录，然后在 STA 界面线程上加载实际 MainWindow 与 MainViewModel。每次输出目录必须是新目录，内含 `results.json`、六页及三个详情子页 PNG。整个过程只使用隔离的模拟 Google 授权和合成模型密钥，不打开真实登录浏览器、不读取正式应用资料、不发送真实邮件，结束后清理临时运行目录。窗口在屏幕外渲染，因此截图是实际 WPF 渲染结果，不是人工操作录像。

如果使用 WSL 交叉构建，可先执行以下命令，再从 Windows 调用上面的脚本并追加 `--test-exe artifacts/desktop-tests-v0.2.0-login/ToolsTouch.Desktop.Tests.exe`，Windows 侧无需另外安装 SDK：

```sh
dotnet publish tests/ToolsTouch.Desktop.Tests -c Release -r win-x64 --self-contained true -o artifacts/desktop-tests-v0.2.0-login
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

## v0.2.2 登录与浏览器

Gmail 首次授权后即完成应用登录与发件邮箱连接，无需在设置页重复连接；普通连接复用同一客户端的已保存账号。仅在凭据失效或需重新授权时点击“重新授权 Gmail”。

取消勾选“自动打开默认浏览器”后，点击连接并复制生成的登录链接，在同一台电脑的任意浏览器打开。登录链接只在当前授权期间可用，不应发送给他人。浏览器显示成功可能仅表示回调已收到，以程序中凭据保存完成为准。

模型组件支持现有 HTTP/HTTPS 代理与系统代理；不关闭 TLS 验证。本机授权回调不经过代理。Google 正式发布不代表通过品牌与 Gmail 权限验证。
