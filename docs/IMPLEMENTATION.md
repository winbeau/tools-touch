# 实施与验收记录

目标来源：用户提供的 pasted-text-1.txt；完整范围为 WPF + Pi 导师发现、论文分析、CV 匹配、草稿、人工发送和回复同步。

## 当前证据

- 初始工作区仅包含未跟踪的 `pi/` 源码，无应用代码、无 Git 提交。
- 执行环境为 Linux，Node 24.16.0；初始无 .NET SDK，无 Windows 图形会话。
- 本地 Pi 0.85.0 SDK 使用 `ModelRuntime` 管理认证，支持 customTools 和 ResourceLoader。上游源码不在本任务中修改。
- Windows 原生运行、OpenAI/Gmail 登录、真实核心流程均未验证。不能将跨平台核心测试作为这些项目的证明。

## 实施顺序

1. 工程、SQLite 迁移和核心状态不变量；建立可重复测试。
2. 严格工具契约、Pi AgentHost、受控资源、双向 JSONL 桥接与取消。
3. 实际搜索源验证、论文读取、发现/分析任务和阶段恢复。
4. WPF 六页 MVVM、CV 确认和草稿版本。
5. Gmail 桌面 OAuth、安全存储、发送快照与结果核对、回复同步。
6. Windows 打包、真实流程验收和逐项完成审计。

## 架构决策

- 应用项目位于 `src/`，AgentHost 位于 `agent-host/`，不侵入 Pi 源码。
- C# 独占业务写入；AgentHost 不获得 Gmail 凭据或发送工具。
- 数据库采用版本化 SQL 迁移。任务事件和检查点持久化；发送的不确定结果必须保留为 Unknown。
- 单用户、单 Gmail 账号；本地草稿，重新生成创建新版本。
- 测试发送仅使用替身；真实发送需用户单独明确授权或在 WPF 中操作。

## 完成审计（待逐项举证）

已经有实现和局部证据：八工具契约与 C# 分发、Pi 会话资源隔离、本地研究库、CV 确认与版本、论文摘要/全文区分、草稿幂等和发送状态、任务检查点。

已新增实现：WPF 六页、两个登录入口、Gmail OAuth/发送/核对/回复同步、Windows DPAPI 存储、便携包构建。

仍未完成：原生 Windows 界面与 DPAPI 实测、真实账号和网页检索 API 验收、真实 Pi 发现/分析/草稿流程，以及真实验收中发现问题的修复。论文来源和详情页投递历史已补入实现；逐项证据见 ACCEPTANCE.md。现有局部测试不代表完整目标完成。

## 2026-09-04 基础实现验证

- 本地安装 .NET SDK 10.0.400；核心项目和可执行回归测试构建成功。
- `dotnet run --project tests/ToolsTouch.Core.Tests` 通过：迁移重复执行、草稿重试幂等及冲突、新版本保留旧草稿、重复点击只调用一次传输、网络异常进入 Unknown、明确拒绝进入 Failed、CV 变化禁止发送、数据库重开后的发送中断恢复。
- 测试使用真实 SQLite，邮件使用替身。尚未实现草稿编辑界面、未知结果核对和 Gmail 传输，不能据此认定完整投递功能完成。
- 默认 SQLite 原生依赖 2.1.11 触发 NuGet NU1903；显式固定 bundle 2.1.13 后恢复和测试通过，没有关闭漏洞审计。
- 已定义八工具的严格 Schema 和不发现任何环境资源的 ResourceLoader，正在执行 Pi SDK 实际导入与契约测试。
- Pi 0.85.0 SDK 入口在运行时导入 `@earendil-works/pi-server`，但 coding-agent 发布包未声明该依赖；在应用侧补齐同版本依赖，保持上游源码不变。
- `agent-host/npm test`：5 项通过，包括实际 Node 子进程启动 Pi ModelRuntime、模型列表、隔离目录下无认证、公开状态字段白名单和非法发送命令拒绝；其余测试覆盖精确八工具、严格参数、无环境资源发现、非法输入不会到达应用分发器。
- Host 已实现 JSONL 命令/工具回传、工具次数和任务超时限制、取消转发、过滤后的执行事件和 Pi 会话存储。真实模型、工具往返及取消中断尚需应用集成验收；不能以启动测试证明这些行为。
- 下一步：C# 工具分发与任务持久化、搜索后端实际验证、Pi 登录交互和 WPF 六页。Gmail 及 Windows 交付仍在原目标内，未缩减范围。

## 后续进展：研究服务、桥接与阶段恢复

- 增加 ResearchStore、LibraryService、ToolDispatcher、AgentBridge、DiscoveryService；版本化迁移可顺序应用，002 保存来源文档和草稿证据/资料版本。
- 导师写入以主页去重、幂等键冲突拒绝；工具保存导师前要求抓取来源，邮箱须出现在来源正文中。
- CV 复制到应用目录，提取 PDF 文本，人工确认研究兴趣、项目和技能时创建新版本。任务固定资料版本；工具只返回经历，不返回文件路径。
- read_paper 尝试从论文落地页寻找 PDF；无法获取则返回明确的 AbstractOnly。当前没有 OCR，不能把扫描页当作已读文字。
- PublicWeb 仅访问公开 HTTP(S) 标准端口：检查地址及跳转、连接已检查的 DNS 地址、限制响应体大小，不使用环境代理或 Cookie。
- C# 子进程桥接只继承 OS 必要环境变量；缺少已注册任务上下文时不执行工具。发现任务不能调用创建草稿工具。
- 深入分析分为 Read、Analyze；完成的阶段输出写入 SQLite 检查点。回归用可控桥接中断 Analyze，再恢复，确认 Read 只执行一次，最终结构化分析落库。该测试验证应用编排，不是真实 LLM 验收。
- Pi 最终输出必须符合 research/analysis/draft Schema；应用进一步核实引用的导师、论文和草稿 ID。未通过校验不会被记为完成。
- `npm --prefix agent-host test`：7 项通过。新增实际 Pi Session 的工具集合断言，以及在临时目录放置 AGENTS.md/扩展后确认其不被加载；未发送模型请求。
- `dotnet run --project tests/ToolsTouch.Core.Tests`：通过原发送回归和新研究回归，包括实际 PDF 导入、CV 版本固定、任务恢复、URL 与工具边界、来源检查、真实 C#↔Pi 状态握手。
- `--live-sources`：真实 Crossref 返回 DOI 元数据，响应头观察到 rate limit 1/s、concurrency 1；公开 `https://danijar.com/` 解析成功（该次读取 6876 字符、100 链接）。检索结果包含不相关条目，仍需模型核实与更合适的学术检索补充，不能视为 World Model 导师发现验收。
- Brave 正式 API 适配已写入，但当前无搜索凭据，未执行真实 Brave 请求；公开搜索端点的可访问性不能替代 API 可用性与配额验证。
- 下一步先提供 WPF 可操作界面与 Pi 登录交互，继续补齐原目标中的 Gmail、安全存储、交付和真实流程验收。

## 后续进展：WPF、登录、Gmail 与 Windows 包

- 六页 WPF + MVVM 已交叉编译成功，0 警告 / 0 错误。连接研究任务、进度、取消、恢复、导师库、分析、前 10 页论文阅读、CV 确认、草稿编辑；尚无原生 Windows 渲染证据。
- Pi 登录桥接只调用 ModelRuntime.login(openai-codex, oauth)，转发浏览器地址和必要的授权输入，丢弃返回的凭据对象；WPF 不做 OpenAI Token 交换或存储。
- Gmail 独立实现系统浏览器 + localhost 随机端口回调、PKCE、state、send/readonly scopes；刷新凭据通过 ISecretStore 在桌面端用 DPAPI CurrentUser 保存。真实 Google 授权和 Windows DPAPI 尚未实测。
- MIME 使用 MimeKit，发送快照绑定草稿修订号和发送账号。界面先保存并人工确认，再调用 OutreachService；后台没有发送工具。新增单实例保护，避免第二个窗口误恢复正在发送的记录。
- Gmail 4xx（408 除外）视为明确拒绝；5xx、连接异常或响应无法解析保留 Unknown。按相同 Message-ID、收件人及 SENT 标签核对；找不到不转为 Failed、不重发。
- 回复通过账号 + threadId 关联，并保存本地 EmailThread；跨线程邮件未自动关联。
- 新 Gmail 回归全部通过：用真实 localhost HTTP 回调和模拟 Google HTTP 验证 state/PKCE/scopes；MIME Unicode、状态分类、核对、回复同步、陈旧草稿确认被拒绝。没有真实 Google 请求或真实邮件发送。
- `scripts/package.py` 已成功执行：重装锁定依赖并运行测试，发布 self-contained win-x64，安装 Windows 生产依赖，下载官方 Node 24.16.0 压缩包并校验 SHA-256。
- 产物：`artifacts/ToolsTouch-win-x64.zip`（该次约 266 MB）。BUILD-MANIFEST.json 明确 windowsRuntimeVerified=false；这不是原生运行验收。
- README、config/settings.example.json、docs/WINDOWS.md 提供设置、开发运行、便携运行和验证步骤。
- 已向用户请求可用 Windows 验证环境以及 Google Desktop OAuth / Brave 密钥文件路径，尚无回复。继续推进不依赖这些配置的实现与审计，目标保持 active。
- 为解决 Crossref 对 World Models 示例检索不佳的问题，已核实 arXiv 官方 API 可返回 Atom 结果；尚未将其接入应用，后续需实现与验证，不能把该探测当作应用检索已完成。

## 后续进展：论文真实验证与恢复审计

- 新增 IPaperSearch、ArxivSearch、ScholarlySearch；桌面论文检索优先 arXiv，空结果/网络失败时回退 Crossref。arXiv 单并发、至少间隔 3 秒，保留文献版本，禁止 XML 外部实体。
- 实际应用代码的 `--live-sources` 成功找到 World Models（1803.10122v4），下载对应版本 PDF 并提取前两页。论文阅读支持指定起始页，每次最多 10 页；这仍不替代真实 LLM 研究流程验收。
- 详情页增加投递历史，显示版本、状态、创建时间、同账号线程回复状态，可打开选中邮件。
- 阶段分析与检查点改为同一 SQLite 事务；回归模拟结果写入失败，确认二者一起回滚。
- 同一草稿任务使用稳定任务幂等键；恢复重试返回原草稿 ID，保留人工编辑。新生成任务仍创建新版本。
- 新任务被 RUN_BUSY 拒绝时不再取消其他正在执行的登录或任务；对应回归通过。Queued 任务也可以从 Dashboard 启动/恢复。
- CV 确认、生成与选择附件前核对原始哈希，阻止被外部修改的文件悄悄替换已确认版本；回归通过。
- 完整核心回归及新增 arXiv 解析/安全/回退/取消测试通过。WPF 交叉编译通过；没有真实邮件发送。
- 新包 `artifacts/ToolsTouch-win-x64-r2.zip` 已生成，包含以上修复。构建清单改为覆盖全部产物文件，提供 verify-package.py。
- 已按目标建立 ACCEPTANCE.md；真实 Windows、OpenAI/Gmail 账号及 Brave 配额证据仍缺失。异步配置问题未收到回复，本轮仍完成了独立实现和验证，不标记目标完成或阻塞。

## 2026-09-04 续开发：Windows 原生检查与桌面修复

- 本轮可访问 Windows 宿主，已补齐上轮缺少的部分原生证据。实测系统为 Windows NT 10.0.26200.0，运行时 .NET 10.0.11；完成时间为 2026-09-05 06:11 UTC（本地 9 月 4 日）。
- 新增 `tests/ToolsTouch.Desktop.Tests` 与 `scripts/test-windows.py`。测试发布为 self-contained win-x64，使用实际 MainWindow/MainViewModel、STA Dispatcher 和独立临时数据目录；不访问正式应用资料、真实账号或邮件。
- Windows 复现后修复三处问题：取消草稿选择仍留下可编辑旧正文；筛选移除导师后留下旧投递历史；Pi 已连接时退出界面，管道读取续体依赖 UI Dispatcher 导致死锁。退出修复前原生回归明确失败，修复后通过。
- DesktopSettings 与 WindowsSecretStore 支持显式指定隔离目录，默认仍使用原有 `%LOCALAPPDATA%\ToolsTouch`。存储目录不写入设置 JSON。
- 原生 7 组检查全部通过：DPAPI 合成凭据加密、另一进程读取、替换/损坏/删除；隔离设置持久化；六页和三个详情子页渲染；编辑、保存、刷新保留未保存内容、已发送锁定与空选择；导师筛选后的详情清理；真实包内 Node/Pi 匿名状态握手及 UI 阻塞等待退出；交互全程无 WPF 绑定错误。
- 证据保存在 `artifacts/windows-smoke-r3/results.json` 和同目录九张 PNG。这是屏幕外的实际 WPF 渲染与程序化交互，没有执行完整 App 启动/单实例或人工全流程验收。
- 从 WSL 共享路径直接运行会出现 WPF PenImc DLL 加载失败及 Pi 启动超时；DLL 实际存在。Windows 本地目录运行通过。脚本复制到 Windows 临时目录，并支持深层 npm 依赖的 Windows 长路径。
- AgentHost 7 项测试、全部核心回归和 Release 发布通过。生成 r3 便携包；测试目录与包内 `ToolsTouch.dll`、`ToolsTouch.Core.dll` 的 SHA-256 一致。完整包哈希清单校验通过。
- 仍待真实 OpenAI/Gmail 登录、模型工具流程、Brave 检索、人工发送与回复验收，以及不同 DPI / 干净 Windows 用户验证。Windows 自动检查通过不等于原始目标全部完成。

## 2026-09-04 安装 EXE 与 GitHub Release

- 按用户要求新增 Inno Setup 安装器和 `scripts/build-installer.py`，以通过逐文件哈希校验的 r3 便携 ZIP 为输入，生成 `ToolsTouch-Setup-0.1.0-win-x64.exe`（187,959,790 字节）。只打包清单列出的文件，不纳入工作区或用户资料。
- 当前用户安装到 `%LOCALAPPDATA%\Programs\ToolsTouch`，无需管理员权限；固定 AppId 支持覆盖升级，应用运行时以已有 `Local\ToolsTouch` 互斥量阻止安装/卸载。提供开始菜单、可选桌面快捷方式及卸载入口；不配置删除业务数据的规则。
- 新增 `scripts/test-installer.py`，使用独立临时目录执行安装、全部 26,234 个文件的哈希核对、重复安装修复，以及使用实际安装的 Node/Pi 和相同桌面程序集执行 7 组原生检查。测试拒绝覆盖当前用户已有的正式安装。
- 安装 EXE SHA-256：`8f58de4e8d4f52ddae3461babd8ce09aa0fae2c98824f18a345e0fa37eef8bf1`。当前未做代码签名；构建元数据和 SHA256SUMS 随 GitHub Release 提供。
- 版本说明保存在 `docs/releases/v0.1.0.md`，明确真实账号和完整业务流程的验收缺口。本次发布不改变这些限制。
- 安装生命周期检查最终退出码 0：安装、文件完整性、重复安装修复、原生桌面检查与卸载全部通过。卸载移除了应用和注册表入口，保留安装目录内的用户自建文件。证据在 `artifacts/installer-check-v0.1.0/results.json`；期间未使用真实账号或发送邮件。
