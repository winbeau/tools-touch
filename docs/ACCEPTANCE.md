# 目标逐项验收

目标为用户提供的 pasted-text-1.txt 全文；以下不改变目标范围。**目标尚未完成**，主要缺少真实账号 / 搜索 API 的端到端证据和人工完整验收。已补充 Windows 原生自动检查，见下方本次续开发记录。

## 实现与当前证据

| 要求 | 当前实现 / 证据 | 尚需验收 |
|---|---|---|
| WPF、MVVM、六页面 | Windows 11 原生渲染六页与三个详情子页；绑定、导航、草稿编辑/锁定/空选择自动检查 | 人工完整操作、不同 DPI 和干净 Windows 用户实际使用 |
| Pi Runtime / Provider / Loop 复用 | AgentHost 使用 ModelRuntime 和 createAgentSession，session.ts 创建业务会话 | 真实 OpenAI 登录与模型调用 |
| OpenAI 凭据只由 Pi 管理 | login 仅调用 Pi Provider，向 WPF 转发 URL / 提示，凭据返回值不输出 | Windows 浏览器登录、重新连接、过期刷新 |
| Gmail 单独 OAuth | GmailAuth：随机 loopback、state、PKCE、send/readonly；回调模拟测试通过 | 真实 Google Desktop client 与授权 |
| Windows 安全存储 | WindowsSecretStore 使用 DPAPI CurrentUser；合成凭据加密、跨进程读取、替换/损坏/删除实测通过 | 真实账号重启后刷新与重连、跨 Windows 用户隔离 |
| Gmail API 发送且人工确认 | MainViewModel.SendAsync → OutreachService → GmailService；没有发送工具 | Windows 人工确认界面；真实发送仅由用户操作或另行明确授权 |
| SQLite、CV、论文、本地草稿 | 001–003 SQL 迁移、ResearchStore/LibraryService/OutreachService | Windows 文件权限和真实文档兼容性 |
| 八个工具，search_professors 查本地 | TS Schema + C# ToolDispatcher 双边校验；非法工具 / 参数测试通过 | 真实模型工具往返 |
| 无 Shell / 任意文件工具 / 环境资源 | 实际 Pi Session 工具集合测试、植入 AGENTS/扩展隔离测试通过 | 随 Windows 包的相同行为 |
| 真实网页检索与配额 | Brave 正式 API 适配、配置文件入口、限流处理 | 尚无 Brave 凭据；真实 API、配额和结果质量未验证 |
| 论文检索与阅读 | arXiv + Crossref；真实 API 找到 World Models 并提取 PDF 页文字 | 在真实导师发现任务中核实作者身份、论文相关性 |
| 事实有来源、不编造 | 源文本持久化；未抓取证据、未出现邮箱被拒绝；研究输出来源核验 | 真实 LLM 输出内容逐条人工核查；链接存在不等于支持任意结论 |
| 区分摘要与全文 | AbstractOnly / FullTextPages 返回；带页码阅读；真实 PDF 提取通过 | 真实文档扫描/加密/损坏场景的 UI 展示 |
| 无 CV 可发现，不做个人匹配 | 缺少确认资料返回 available=false；分析输出个人匹配必须为空 | 真实无 CV 发现流程 |
| CV 解析、人工确认、版本 | PDF 提取与结构化字段、确认创建版本、任务固定 profileId；测试通过 | Windows 实际导入 / 编辑；CV 哈希变更提示 |
| 向 LLM 发送资料说明 | Settings 与 WINDOWS.md 明确展示 | Windows 可见性和用户理解 |
| 幂等与草稿版本 | 导师写入键、草稿任务键、编辑 Revision；重试不覆盖人工修改测试通过 | 真实模型重试与重启 |
| 进度、取消、预算、失败恢复 | JSONL 事件、每阶段工具数/超时、任务事件、阶段检查点；模拟中断恢复通过 | 真正进行中的模型请求取消、Windows 进程退出恢复 |
| 结构化状态，不解析自然语言 | TypeBox 阶段输出校验；应用核实实体 ID 后写状态 | 真实模型遵循输出契约的成功率与失败提示 |
| 发送快照、重复点击、不确定核对 | SQLite 原子 claim，修订号、附件哈希、Message-ID；重复发送 / 5xx / 核对测试通过 | Windows 双击与真实网络中断 |
| Gmail messageId/threadId、回复管理 | 账号隔离、threadId 同步、EmailThread、详情页投递历史；模拟 HTTP 测试通过 | 真实收到回复；目前跨线程邮件不自动关联 |
| 指定业务与辅助表 | Professor/Paper/ProfessorAnalysis/Outreach/EmailThread/UserProfile/ProfessorPaper/AgentRun/AgentRunEvent 均在迁移中 | Windows 迁移与持久化复核 |
| 可构建代码、配置、运行 / 安装步骤 | SDK/依赖固定、锁文件、config 示例、WINDOWS.md、package.py | 干净 Windows 用户使用便携包 |

## 已执行的命令及其证明范围

- `npm --prefix agent-host test`：实际 Pi 会话隔离、严格工具与阶段输出契约、子进程状态握手。没有使用真实模型。
- `dotnet run --project tests/ToolsTouch.Core.Tests`：真实 SQLite/PDF/loopback，以及模拟邮件 HTTP；覆盖版本、幂等、检查点、发送状态和回复关联。没有发送真实邮件。
- `dotnet run --project tests/ToolsTouch.Core.Tests -- --live-sources`：额外访问真实 arXiv、Crossref、公开主页并下载 World Models PDF。它不证明完整导师发现任务，也不验证 Brave。
- `dotnet build src/ToolsTouch.Desktop`：仅证明 Windows 目标的 C#/XAML 编译。
- `python scripts/package.py`：测试后发布 self-contained win-x64，按锁文件装 Windows 依赖、校验官方 Node 下载，生成便携 zip。BUILD-MANIFEST 中 Windows 运行标记为 false。

## 上轮外部条件（历史记录）

执行环境仍是 Linux，没有获得可访问的原生 Windows 会话、Google Desktop OAuth 配置路径或 Brave 密钥文件路径。已通过异步问题请求这些信息，尚未收到回答。不能把时间经过当作凭据、授权或 Windows 验证已完成。

**完成判定仍为未证明。** 获得对应条件后，按 WINDOWS.md 的真实核心流程补齐证据，并修复发现的问题；禁止仅凭绿色测试标记目标完成。

## 上轮阻塞审计（历史记录）

最近三轮持续缺少同一组真实验收条件：可访问的原生 Windows 环境、Google Desktop OAuth 配置及 Brave API 凭据。此前两轮仍完成了独立实现、测试和打包；本轮重新读取目标、验收记录并确认宿主仍为 Linux，配置目录仍仅有示例，用户未提供上述条件。

上一轮属于实际进展，已产生 r2 便携包并验证全部文件哈希。当前未发现可替代真实环境/账号验收的本地操作；再次构建或重复模拟测试不能补足这些缺口。目标标记 blocked，保留原始范围，未标记 complete。

恢复所需：提供 Windows 验证环境访问方式及 Google Desktop OAuth JSON / Brave 密钥文件的路径，在该环境由用户完成必要的 OpenAI 与 Gmail 登录。无需在对话中粘贴 Token 或密钥；真实邮件发送仍须用户在应用中操作或另行明确授权。

## 本次续开发结果（2026-09-04，本地时间）

上面的环境阻塞为历史记录。本轮已经获得 Windows 宿主，并完成原生自动检查，Windows 不再是同一项外部阻塞。真实账号和搜索 API 的验收条件仍未提供。

执行 `python scripts/test-windows.py --package artifacts/ToolsTouch-win-x64-r3 --test-exe artifacts/desktop-tests-win/ToolsTouch.Desktop.Tests.exe --output artifacts/windows-smoke-r3`，退出码 0，7 组检查全部通过。系统报告 Windows NT 10.0.26200.0 / .NET 10.0.11，记录时间为 2026-09-05 06:11:41 UTC。

| 本次证据 | 证明范围 |
|---|---|
| `artifacts/windows-smoke-r3/results.json` | 7 组原生检查，failures=0；realAccountsUsed=false、realMailSent=false |
| 同目录 `page-1.png` 至 `page-6.png`、`detail-1.png` 至 `detail-3.png` | 实际 MainWindow 在 Windows 上的屏幕外渲染，六页与三个详情子页可加载；无绑定错误 |
| DPAPI 独立子进程 | 合成凭据可跨进程持久化读取，替换、损坏检测和删除正确；不证明真实 Gmail 刷新或跨用户隔离 |
| 草稿与导师交互回归 | 编辑保存、刷新保留未保存内容、Sent 锁定、取消选择清空草稿、筛选移除导师清空详情 |
| 包内 Node / Pi 与 App 同步退出方式 | 原生匿名状态握手和模型列表；界面线程等待桥接清理时不再死锁，不证明真实登录/模型调用 |
| r3 包与测试程序集哈希一致 | 实际测试的 Desktop / Core 程序集与 r3 包中的程序集完全相同 |

`BUILD-MANIFEST.json` 仍保留 `windowsRuntimeVerified=false`，避免把上述自动检查当作完整原生验收。仍需验证真实账号、Brave、真实模型工具循环、用户发送/收到回复，以及 App 启动入口/单实例、不同 DPI 和干净 Windows 用户的完整人工流程。

## 安装版 v0.1.0

`scripts/test-installer.py` 对最终安装 EXE 的检查已通过，结果在 `artifacts/installer-check-v0.1.0/results.json`（passed=true）。覆盖当前用户安装/卸载登记、26,234 个安装文件哈希、重复安装修复被修改的程序文件、安装目录下用户自建文件保留，以及卸载后应用文件与登记清理。实际安装的 Node/Pi 和与安装包字节一致的桌面程序集同时通过 7 组原生检查。所有过程均在临时目录执行，未使用真实账号或发送邮件。

GitHub Release 随包提供安装器构建元数据、校验值和上述两份 JSON 验证结果。没有代码签名、真实账号完整验收或跨 Windows 用户的人工验收证据。
