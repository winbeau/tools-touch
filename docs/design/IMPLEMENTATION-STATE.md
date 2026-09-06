# 实施状态与续接记录

最后更新：2026-09-05，WPF 工作台界面优化及 23 组原生自动检查已完成，见[界面验证记录](UI-WORKBENCH-VALIDATION.md)。本文记录**新架构扩展**，既有功能验收仍见[历史验收](../ACCEPTANCE.md)。

## 1. 当前状态

- 设计文档已编写并补充编码交接；P00 全部四个工作包、P01-A—P01-D、P02-A—P02-D、P03-A—P03-D、P04-A—P04-D、P05-A—P05-D、P06-A—P06-D、P07-A—P07-E、P08-A—P08-D 已完成。
- 实施工作包已无下一项；真实业务验收仍按 [`real-acceptance-matrix.json`](real-acceptance-matrix.json) 保持 NotVerified，不能把 37/37 工作包完成等同于真实账号、来源或 Windows 人工验收完成。
- 本轮没有运行真实模型、Gmail 登录、采集或邮件发送，没有迁移用户数据库。
- 工作区已有 README 修改和未跟踪 `baoyan-cli/`、`docs/design/`；本轮增加根 AGENTS.md。开工重新检查，不删除这些未跟踪目录。
- 交接复查时出现其他在途修改：AgentBridge／GmailAuth／账户 UI／对应测试，以及新增 `AgentProxy.cs`、`agent-host/src/runtime.ts`、Google 验证说明和 v0.2.2 发布说明。本设计会话未修改这些业务文件；P00-A 重新扫描并保留，不能用早期文件列表覆盖新实现。
- 本轮观察到 Node 24.16.0／uv 0.9.17；Linux PATH 未找到 dotnet，pnpm 指向 Windows 挂载盘，实际可执行性待 P00-A 检查。
- Q1／Q2／Q4 已确认；Q3 来源范围未回复；具体首轮校院种子未选定。处理方式见[接手入口](START-HERE.md)。

## 2. 工作包状态

合法状态：NotStarted、InProgress、ImplementedPendingVerification、Verified、Blocked。Blocked 仅用于这个工作包的具体外部条件，不能自动把其他工作包也标记阻塞。

| 阶段 | 工作包 | 当前状态 |
| --- | --- | --- |
| P00 | A 基线；B pnpm；C uv；D 脚本与 CI | A—D Verified |
| P01 | A 工程机械迁移；B 数据基础；C 证据与版本；D 回归／备份 | A—D Verified |
| P02 | A 协议；B 任务；C 账号；D Python 桥接 | A—D Verified |
| P03 | A 日期规则；B baoyan；C 导入；D 学校表 | A—D Verified |
| P04 | A 目录；B 身份证据；C 论文评价；D 覆盖／详情 | A—D Verified |
| P05 | A 资料统计；B 规则；C 语义研究；D 推荐 UI | A—D Verified |
| P06 | A 草稿；B 确认发送；C 核对回复；D 申请记录 | A—D Verified |
| P07 | A 通用模型；B 表格编辑；C 查询视图；D 导出恢复；E 表格导入 | A—E Verified |
| P08 | A 界面；B 性能；C 安装；D 真实验收记录 | A—D Verified；U01—U11 real acceptance NotVerified |

已执行最高新迁移：016（原有 001—003）。应用当前协议：v2；006 已加入 JobStage／JobAttempt／预算与恢复结构，007 加入导师身份消歧和无主页导师记录支持，008 加入论文阅读片段、作者归属待核实和评价证据，009 加入经验样本与 CohortStatistic，010 加入推荐运行、候选快照和分项，011 加入草稿请求元数据、资料引用冻结字段和 DraftChange 历史，012 加入一次性 SendConfirmation、不可变 MIME artifact 关联和 SendAttempt 状态，013 加入 DeliveryCheck 核对记录和 EmailMessage 线程／回复元数据，014 加入 ApplicationCase／ApplicationEvent／CaseOutreach、材料清单和 Reminder，015 加入记录器集合、RecordRef、字段定义／选项／值、关系、视图、更改和导入批次元数据，016 加入导入源、映射、重复策略和预览错误的批次持久化字段。

## 3. 每次续写模板

不要用模板内容替代实际结果。每完成一个工作包追加一条，未完成也应在结束当前编码会话前记录。

```text
工作包：Pxx-X
状态：
实现日期／工作区版本：
实际变更文件：
迁移／协议／fixture 版本变化：
验证命令与退出码：
关键结果与证据路径：
未验证的真实条件：
尚未解决的问题（文件／函数／复现输入）：
下一工作包与第一项动作：
```

## 3.1 P00-A 基线记录（2026-09-05）

- 工作包：P00-A
- 状态：Verified
- 实际变更文件：`artifacts/implementation/P00-A/20260905T093305Z/results.json`、本状态文件
- 迁移／协议／fixture 版本变化：无
- 验证命令与退出码：`pnpm --dir agent-host exec tsc`（0）；`node --test agent-host/dist/*.test.js`（0，14 项通过）；`baoyan-cli/.venv/bin/python -m unittest discover -s . -p 'test_*.py'`（0，5 项通过）；`/home/winbeau/.local/share/tools-touch-dotnet/dotnet run --project tests/ToolsTouch.Core.Tests`（0）；`/home/winbeau/.local/share/tools-touch-dotnet/dotnet build src/ToolsTouch.Desktop/ToolsTouch.Desktop.csproj -c Release`（0，0 警告／0 错误）；`git diff --check`（0）。
- 关键结果与证据路径：Node 类型检查、14 项 AgentHost 测试、5 项 baoyan 测试、既有 C# 可执行回归和 WPF Release 交叉构建均通过；详见 `artifacts/implementation/P00-A/20260905T093305Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／安装包、真实 Gmail／模型／来源账号和真实发信。
- 尚未解决的问题：`dotnet` 未在 Linux PATH，当前使用 `/home/winbeau/.local/share/tools-touch-dotnet/dotnet`；pnpm 命令来自 Windows 挂载路径，P00-B 需完成 Linux 可冻结 workspace 验证。
- 下一工作包与第一项动作：P00-B；读取现有 `agent-host/package-lock.json`，建立仅包含 `agent-host` 的根 pnpm workspace 并验证导入结果。

## 3.2 P00-B Node workspace 记录（2026-09-05）

- 工作包：P00-B
- 状态：Verified
- 实际变更文件：`package.json`、`pnpm-workspace.yaml`、`pnpm-lock.yaml`、`artifacts/implementation/P00-B/20260905T093705Z/results.json`、本状态文件
- 迁移／协议／fixture 版本变化：pnpm workspace／lockfileVersion 9；Pi 仍为 0.85.0，协议无变化
- 验证命令与退出码：`pnpm import`（0）；`pnpm install --frozen-lockfile --ignore-scripts`（0，2 个 workspace 项目、131 个包）；`pnpm --filter tools-touch-agent-host check`（0）；`pnpm --filter tools-touch-agent-host test`（0，14 项通过）；`pnpm --filter tools-touch-agent-host build`（0）；锁文件 importer／packageManager 断言和 `git diff --check`（0）。
- 关键结果与证据路径：根 workspace 只包含 `agent-host`，冻结安装跳过解析并完成链接，Node 检查、构建和测试通过；详见 `artifacts/implementation/P00-B/20260905T093705Z/results.json`。
- 未验证的真实条件：Windows 原生安装／启动、真实 provider 认证和模型请求。
- 尚未解决的问题：CI 和发布脚本仍使用旧 npm／Python 入口，留到 P00-D；`agent-host/package-lock.json` 按迁移约定保留。
- 下一工作包与第一项动作：P00-C；检查 `baoyan-cli` 的运行时导入和现有测试依赖，建立 uv 根聚合项目及 collector-host 成员声明。

## 3.3 P00-C uv workspace 记录（2026-09-05）

- 工作包：P00-C
- 状态：Verified
- 实际变更文件：`pyproject.toml`、`.python-version`、`baoyan-cli/pyproject.toml`、`baoyan-cli/baoyan`、`uv.lock`、`artifacts/implementation/P00-C/20260905T094000Z/results.json`、本状态文件
- 迁移／协议／fixture 版本变化：uv workspace；Python 锁定为 3.12.12／`==3.12.*`；无业务协议或数据库迁移
- 验证命令与退出码：`uv lock`（0，14 个包）；`uv sync --all-packages --locked`（0）；`uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'`（0，5 项通过）；`./baoyan-cli/baoyan --help`（0）；锁定依赖导入断言（0）；`uv lock --check` 和 `git diff --check`（0）。
- 关键结果与证据路径：根 `.venv` 采用 CPython 3.12.12，三项既有 CLI 依赖显式锁定，现有 CLI 测试通过且入口可运行；详见 `artifacts/implementation/P00-C/20260905T094000Z/results.json`。
- 未验证的真实条件：Windows Python 打包、Chrome 登录、真实来源访问和账号认证。
- 尚未解决的问题：脚本／打包／CI 仍需在 P00-D 统一；`collector-host` 按设计留到 P02-D 创建。
- 下一工作包与第一项动作：P00-D；盘点 `scripts/`、现有 GitHub workflow 和 Windows 打包入口，将开发测试切换到 pnpm／uv 并保持 Pages 发布独立。

## 3.4 P00-D 脚本、打包与 CI 记录（2026-09-05）

- 工作包：P00-D
- 状态：Verified
- 实际变更文件：`scripts/package.py`、`scripts/verify-package.py`、`scripts/test-windows.py`、`README.md`、`docs/WINDOWS.md`、`.github/workflows/ci.yml`、`artifacts/implementation/P00-D/20260905T095117Z/results.json`、本状态文件
- 迁移／协议／fixture 版本变化：发布入口切换到 pnpm 10.14.0／uv 0.9.17；便携包清单增加 Python／pnpm 工具链元数据；应用协议和数据库迁移无变化
- 验证命令与退出码：脚本 `py_compile`（0）；`DOTNET_EXE=... uv run --all-packages --locked python scripts/package.py --output ...`（0）；`uv run ... scripts/verify-package.py ...`（0，14,840 个文件哈希）；包内 AgentHost 匿名 status 握手（0）；CI YAML 解析（0）；`uv lock --check`、冻结 pnpm 安装和 `git diff --check`（0）。
- 关键结果与证据路径：独立 Windows x64 便携包生成并完整校验；AgentHost 使用当前 Node 启动成功；打包链路已解决 pnpm isolated deploy 目录链接丢失问题；详见 `artifacts/implementation/P00-D/20260905T095117Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI／安装生命周期、真实 provider／Gmail／来源账号和真实发信。
- 尚未解决的问题：`DOTNET_EXE` 在当前 Linux 环境必须显式指定；Windows 原生和真实账号验收仍按 P08 保留。
- 下一工作包与第一项动作：P01-A；读取 `docs/design/plans/P01-domain-data.md` 和相关迁移细则，确认现有未提交账户／代理修改后创建四工程边界，不复制业务类型。

## 3.5 P01-A 四工程机械迁移与组合根记录（2026-09-05）

- 工作包：P01-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/ToolsTouch.Application.csproj`、`src/ToolsTouch.Infrastructure/ToolsTouch.Infrastructure.csproj`、`src/ToolsTouch.Infrastructure/` 下原 Core I/O 服务与 `Migrations/001`—`003`、`src/ToolsTouch.Core/ToolsTouch.Core.csproj`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、Desktop／Core.Tests 工程引用、`scripts/verify-package.py`、`scripts/test-installer.py`、`artifacts/implementation/P01-A/20260905T101500Z/results.json`
- 迁移／协议／fixture 版本变化：没有新增数据库迁移；001—003 SQL 内容保持不变并随 `LocalDatabase` 移至 Infrastructure；协议仍为 v1。
- 验证命令与退出码：四个源码工程及两个测试工程 Release 编译（均 0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests --no-build --no-restore`（0，既有核心回归通过）；ProjectReference 无环扫描（0）；重新打包（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost 匿名 status（0）；Python 脚本编译、`uv lock --check`、冻结 pnpm 安装、`git diff --check`（均 0）。
- 关键结果与证据路径：Desktop 通过 Application→Core、Infrastructure→Application/Core 的依赖图编译，Core 不反向引用 Infrastructure；便携包含 `ToolsTouch.Application.dll` 与 `ToolsTouch.Infrastructure.dll`，详见 `artifacts/implementation/P01-A/20260905T101500Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI／安装生命周期、真实 provider／Gmail／来源账号和真实发信。
- 尚未解决的问题：`dotnet` 在当前 Linux PATH 中仍不可用，须显式使用 `/home/winbeau/.local/share/tools-touch-dotnet/dotnet`；业务数据模型及新迁移留给 P01-B。
- 下一工作包与第一项动作：P01-B；实现 004 号迁移的 WorkspaceMeta、School、Department、AdmissionProgram、AdmissionRound、EntityAlias、ExternalIdentity、EntityResolution、Appointment 及稳定分页 DTO／服务。

## 3.6 P01-B 数据基础与 004 号迁移记录（2026-09-05）

- 工作包：P01-B
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Core/Organizations.cs`、`src/ToolsTouch.Core/LegacyEntities.cs`、`src/ToolsTouch.Application/Organizations.cs`、`src/ToolsTouch.Infrastructure/Migrations/004_organization.sql`、`src/ToolsTouch.Infrastructure/Persistence/MigrationRunner.cs`、`src/ToolsTouch.Infrastructure/Persistence/OrganizationRepository.cs`、`src/ToolsTouch.Infrastructure/Persistence/Legacy/ResearchStore.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/App.xaml.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P01-B/20260905T103000Z/results.json`
- 迁移／协议／fixture 版本变化：新增数据库迁移 004；`WorkspaceMeta`、School／Department／AdmissionProgram／AdmissionRound／Appointment、别名／外部身份／解析表及索引落库；协议仍为 v1；新增合成旧库升级 fixture。
- 验证命令与退出码：`dotnet run --project tests/ToolsTouch.Core.Tests`（0，既有回归和新增组织数据组通过）；Desktop 与 Desktop.Tests Release 编译（均 0，0 警告／0 错误）；`uv lock --check`、冻结 pnpm 安装和 `git diff --check`（均 0）。
- 关键结果与证据路径：旧 001—003 库二次启动升级到 004，WorkspaceMeta／DataRevision、同名跨校、稳定 keyset 分页、同人多任职、跨源 ID 隔离及事务失败回滚均通过；详见 `artifacts/implementation/P01-B/20260905T103000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／安装生命周期、真实 provider／Gmail／来源账号和真实发信。
- 尚未解决的问题：来源快照、原始文件哈希、证据声明和人工覆盖尚未接入；`dotnet` 仍需显式使用 `/home/winbeau/.local/share/tools-touch-dotnet/dotnet`。
- 下一工作包与第一项动作：P01-C；增加 005 号迁移与 `ArtifactStore`／`SourceSnapshot`／`EvidenceClaim`，先将来源正文以哈希文件原子发布，再写数据库索引。

## 3.7 P01-C 来源快照、Artifact 与证据记录（2026-09-05）

- 工作包：P01-C
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Core/EvidenceRecords.cs`、`src/ToolsTouch.Application/Evidence.cs`、`src/ToolsTouch.Infrastructure/Migrations/005_evidence_snapshot.sql`、`src/ToolsTouch.Infrastructure/Persistence/ArtifactStore.cs`、`src/ToolsTouch.Infrastructure/Persistence/MigrationRunner.cs`、`src/ToolsTouch.Infrastructure/Persistence/SourceRepository.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/EvidenceTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P01-C/20260905T110000Z/results.json`
- 迁移／协议／fixture 版本变化：新增数据库迁移 005；Artifact／SourceSnapshot／EvidenceClaim／ClaimSupport／SourceCheck／EvidenceConflict、WindowObservation／HistoricalProjection／ObservationOverride、PreferenceRevision 及 UserProfile 扩展落库；旧 SourceDocument 在迁移事务中回填；协议仍为 v1。
- 验证命令与退出码：Core、Application、Infrastructure、Desktop、Desktop.Tests Release 编译（均 0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests --no-build --no-restore`（0，既有回归、组织数据和证据数据组通过）；Python 脚本编译、`uv lock --check`、冻结 pnpm 安装和 `git diff --check`（均 0）。
- 关键结果与证据路径：旧来源正文被写入哈希地址文件且多个快照不覆盖彼此；声明支持、年份／窗口字段和人工更正撤回均通过；详见 `artifacts/implementation/P01-C/20260905T110000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／安装生命周期、真实 provider／Gmail／来源账号和真实发信。
- 尚未解决的问题：备份／恢复一致性、WAL 快照、完整性巡检及包含 Sent／Unknown／未完成任务的长期 fixture 尚未完成；`dotnet` 仍需显式使用 `/home/winbeau/.local/share/tools-touch-dotnet/dotnet`。
- 下一工作包与第一项动作：P01-D；实现 SQLite 一致性备份、路径／哈希／FK 完整性检查和可重复的 001—005 旧库 fixture，确认失败后可恢复。

## 3.8 P01-D 备份、旧库 fixture 与迁移回归记录（2026-09-05）

- 工作包：P01-D
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Backup.cs`、`src/ToolsTouch.Infrastructure/Persistence/BackupService.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/EvidenceTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P01-D/20260905T113000Z/results.json`
- 迁移／协议／fixture 版本变化：无新数据库迁移；备份 manifest v1 与 001—005 合成升级 fixture 完成；应用协议仍为 v1。
- 验证命令与退出码：`dotnet run --project tests/ToolsTouch.Core.Tests`（0，既有回归、组织／证据／备份组通过）；`scripts/package.py`（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost 匿名 status（0）；CI YAML 解析、`uv lock --check`、冻结 pnpm 安装、`git diff --check`（均 0）。
- 关键结果与证据路径：SQLite 在线备份可通过数据库完整性和外键校验，篡改数据库会被拒绝；旧库两次启动升级保留旧 ID、SourceDocument 正文和 Sent／Unknown 旧表能力；便携包已包含当前四工程程序集，详见 `artifacts/implementation/P01-D/20260905T113000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI／安装生命周期、真实 provider／Gmail／来源账号和真实发信。
- 尚未解决的问题：BackupService 当前提供验证和生成备份，完整业务 ZIP 恢复与任务暂停留给 P07；`dotnet` 仍需显式使用 `/home/winbeau/.local/share/tools-touch-dotnet/dotnet`。
- 下一工作包与第一项动作：P02-A；建立 `contracts/agent-v2.schema.json`、C#／TypeScript 严格 JSONL v2 编解码和协议测试，先锁定 1 MiB 帧边界与 attempt 三元组。

## 3.9 P02-A agent-v2 协议、双端编解码与工具白名单（2026-09-05）

- 工作包：P02-A
- 状态：Verified
- 实际变更文件：`contracts/agent-v2.schema.json`、`agent-host/src/index.ts`、`agent-host/src/authentication.ts`、`agent-host/src/tools.ts`、`agent-host/src/session.ts`、`agent-host/src/contracts.test.ts`、`agent-host/src/authentication.test.ts`、`src/ToolsTouch.Application/AgentProtocol.cs`、`src/ToolsTouch.Infrastructure/Pi/AgentBridge.cs`、`src/ToolsTouch.Infrastructure/Pi/Legacy/ToolDispatcher.cs`、`src/ToolsTouch.Infrastructure/Legacy/DiscoveryService.cs`、`src/ToolsTouch.Desktop/AccountsViewModel.cs`、`tests/ToolsTouch.Core.Tests/ResearchTests.cs`、`tests/ToolsTouch.Core.Tests/AgentProtocolTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P02-A/20260905T120000Z/results.json`
- 迁移／协议／fixture 版本变化：协议从 v1 升为 v2；所有命令带 `protocol_version/type/id`，运行带 `run_id/stage_key/attempt_id`，认证与运行取消均按精确目标；JSONL 单帧不含 LF 上限为 1,048,576 UTF-8 字节；新增 `research`／`analysis`／`draft` 工具策略，数据库无新增迁移。
- 验证命令与退出码：`pnpm --filter tools-touch-agent-host check`（0）；`pnpm --filter tools-touch-agent-host test`（0，16 项通过）；`pnpm --filter tools-touch-agent-host build`（0）；`/home/winbeau/.local/share/tools-touch-dotnet/dotnet build` Application、Infrastructure、Desktop、Core.Tests（均 0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests --no-build`（0）；`dotnet build tests/ToolsTouch.Desktop.Tests -c Release`（0）；`uv run --all-packages --locked python scripts/package.py --output artifacts/implementation/P02-A/20260905T120000Z/package`（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 匿名 status（0）；契约 JSON 语法检查与 `git diff --check`（0）。
- 关键结果与证据路径：Node Host 输出 v2 ready／status，拒绝 v1 与未知命令，精确旧 attempt 取消返回 `RUN_TARGET_NOT_ACTIVE`，超限 UTF-8 帧在解析前拒绝；C# 编解码器拒绝旧版本、重复／未知字段和超限帧，Bridge 接收 v2 ready、按目标回复工具并过滤迟到事件；认证 UI 传递 `auth_request_id`；便携包包含当前 Host 与四工程程序集。详见 `artifacts/implementation/P02-A/20260905T120000Z/results.json`。
- 未验证的真实条件：Linux 没有 `Microsoft.WindowsDesktop.App` 运行时，`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build` 退出 150；Windows 原生 WPF／DPAPI／安装生命周期、真实 provider 登录／模型工具往返、真实来源账号和发信仍未验收。
- 尚未解决的问题：任务 JobStage／JobAttempt／lease／恢复和共享预算尚未实现；Host 真实模型运行需在后续任务包或 Windows／已配置账号验收中验证。
- 下一工作包与第一项动作：P02-B；新增 006 号迁移和 JobScheduler，先设计接受原子化、Attempt lease 与检查点恢复，不改变 v2 目标取消语义。

## 3.10 P02-B 持久化任务队列、阶段 lease 与恢复（2026-09-05）

- 工作包：P02-B
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Jobs.cs`、`src/ToolsTouch.Infrastructure/Migrations/006_jobs.sql`、`src/ToolsTouch.Infrastructure/Persistence/MigrationRunner.cs`、`src/ToolsTouch.Infrastructure/Persistence/JobRepository.cs`、`src/ToolsTouch.Infrastructure/Legacy/DiscoveryService.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/AccountsViewModel.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`tests/ToolsTouch.Core.Tests/JobTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P02-B/20260905T141500Z/results.json`
- 迁移／协议／fixture 版本变化：新增 006 号迁移；`AgentRun` 增加父任务、provider／model、尝试次数、lease 与等待字段；新增 `JobStage`、`JobAttempt`、根预算、`CrawlBatch`／`CrawlItem` 及 attempt 序号约束；旧 `AgentRun` 和运行中状态回填为可恢复的 JobStage；协议保持 v2。
- 实际实现：`JobScheduler` 校验阶段定义和预算，repository 在同一事务中接受任务、创建阶段／预算／事件，按稳定顺序取得短期 lease，绑定 `AttemptId`，检查 owner 和过期时间，支持续租、重试、取消、过期／启动恢复及父子任务共享根预算。阶段检查点和 Analyze 的 `ProfessorAnalysis` 结果在同一事务提交。`DiscoveryService` 已接入阶段调度、v2 run 三元组、policy 工具上下文和原子阶段完成；桌面组合根启动时回收 stale lease。
- 验证命令与退出码：`pnpm --filter tools-touch-agent-host check`（0）；Node 测试（0，16 项）；`uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'`（0，5 项）；Core／Application／Infrastructure／Desktop／Core.Tests／Desktop.Tests Release 编译（0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests -c Release --no-build --no-restore`（0）；`scripts/package.py`（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 匿名握手（0）；契约 JSON 检查和 `git diff --check`（0）。Desktop.Tests 在 Linux 运行退出 150，原因是缺少 `Microsoft.WindowsDesktop.App`，按环境缺口记录。
- 关键结果与证据路径：迁移升级、旧 checkpoint 回填、队列原子接受、阶段 lease／错误 owner／迟到 attempt 拒绝、重试上限、父子预算、启动恢复以及完整核心回归通过；便携包按顺序构建并完成哈希和 Host 握手验证；详见 `artifacts/implementation/P02-B/20260905T141500Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI／安装生命周期、真实 provider 登录／模型阶段往返、真实来源账号和发信；来源范围及首轮校院种子仍未定，不阻塞当前编码。
- 尚未解决的问题：当前桌面旧 UI 仍在执行阶段传入 provider／model，任务创建时的固定 provider／model 选择和能力 catalog 端口留给 P02-C；真实模型往返需 Windows 或配置账号验收。并行运行验证曾因同时写入构建产物出现一次瞬时 `CS5001` 与 AgentBridge 断开，按顺序复跑均通过。
- 下一工作包与第一项动作：P02-C；从现有 `AccountsViewModel` 和 Host status/catalog 响应抽出 provider 能力、已配置／已验证状态及 Google 登录 ports，保持凭据不进入业务数据库和导出。

## 3.11 P02-C provider catalog、账号 ports 与固定模型配置（2026-09-05）

- 工作包：P02-C
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Accounts.cs`、`src/ToolsTouch.Infrastructure/Pi/AgentAccountCatalog.cs`、`src/ToolsTouch.Infrastructure/Google/GmailAuth.cs`、`src/ToolsTouch.Infrastructure/Google/GmailService.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/AccountsViewModel.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`agent-host/src/index.ts`、`agent-host/src/contracts.test.ts`、`contracts/agent-v2.schema.json`、`tests/ToolsTouch.Core.Tests/AccountCatalogTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P02-C/20260905T153000Z/results.json`
- 迁移／协议／fixture 版本变化：无数据库迁移；v2 ready/status 的 provider catalog 增加 `auth_methods`、`configured`、`verified`、`state`、`last_validated_at` 和结构化 models，契约 schema 增加 provider 定义；新增 C# catalog 与旧字段兼容 fixture。
- 实际实现：Application 定义 provider／model／授权能力 DTO、严格 catalog 解析和 `IGmailAccountPort`；Infrastructure 提供基于 v2 status 的 `AgentAccountCatalog`，GmailAuth 通过 port 暴露给组合根。Host 只报告公开能力，不泄露凭据；凭据存在时显示 `Configured`，只有结构化模型阶段成功后才显示 `Verified`。账号 UI 按 catalog 能力生成 OAuth／API Key 选项并展示状态；任务创建时把 provider/model 写入 JobRun，恢复时拒绝配置被替换。
- 验证命令与退出码：`pnpm --filter tools-touch-agent-host check`（0）；Node 测试（0，16 项）；`uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'`（0，5 项）；Application／Infrastructure／Desktop／Core.Tests Debug 编译（0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests -c Debug --no-build --no-restore`（0，新增 catalog fixture 与既有回归通过）；Desktop.Tests Release 编译（0，0 警告／0 错误）；`scripts/package.py`（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 匿名握手（0，provider catalog 40 项）；契约 JSON 检查和 `git diff --check`（0）。Desktop.Tests 在 Linux 运行退出 150，原因是缺少 `Microsoft.WindowsDesktop.App`，按环境缺口记录。
- 关键结果与证据路径：C# 不再在账号 ViewModel 中解析原始 status JSON；旧 `oauth`／`api_key` 字段仍能兼容解析；Configured 与 Verified 不再混为一个状态；Gmail 凭据仍只经既有受保护存储，业务数据库和导出不包含凭据；详见 `artifacts/implementation/P02-C/20260905T153000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI／安装生命周期、真实 provider 登录及模型请求验证、真实 Gmail token 过期／重连和发信；来源范围及首轮校院种子仍未定，不阻塞当前编码。
- 尚未解决的问题：provider catalog 的 Verified 时间戳当前随 Host 进程保存，重启后需再次成功模型请求；真实 provider 权限和账户状态仍需人工验收。Python 采集 bridge 和 manifest 留给 P02-D。
- 下一工作包与第一项动作：P02-D；定义 C# `CollectorBridge`、ArgumentList 调用、独立暂存目录及 manifest／records.jsonl／raw 的数量、路径和 SHA-256 原子发布校验。

## 3.12 P02-D Python collector bridge 与机器结果 manifest（2026-09-05）

- 工作包：P02-D
- 状态：Verified
- 实际变更文件：`collector-host/pyproject.toml`、`collector-host/README.md`、`collector-host/src/tools_touch_collector/__init__.py`、`collector-host/src/tools_touch_collector/__main__.py`、`collector-host/src/tools_touch_collector/baoyan.py`、`collector-host/src/tools_touch_collector/manifest.py`、`collector-host/tests/test_baoyan.py`、`collector-host/tests/test_manifest.py`、`pyproject.toml`、`uv.lock`、`scripts/package.py`、`.github/workflows/ci.yml`、`src/ToolsTouch.Application/Collectors.cs`、`src/ToolsTouch.Infrastructure/Collection/CollectorBridge.cs`、`src/ToolsTouch.Infrastructure/Collection/CollectorManifestValidator.cs`、`src/ToolsTouch.Desktop/DesktopSettings.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`config/settings.example.json`、`tests/ToolsTouch.Core.Tests/CollectorTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P02-D/20260905T170000Z/results.json`
- 迁移／协议／fixture 版本变化：无数据库迁移；新增 `collector-manifest-v1` 实现与 Python uv workspace 成员；目标字段为 snake_case，机器产物包含 `manifest.json`、`records.jsonl` 和 `raw/*.json`。
- 实际实现：`tools_touch_collector export-baoyan` 接收 request 文件和空 staging 目录，匿名分页查询 baoyanwang，保留来源原始响应，按校院／类型／年份 scope 选择记录，并在最后原子发布 manifest。C# `CollectorBridge` 使用 `ProcessStartInfo.ArgumentList`，隔离环境不继承来源 token，限制 stdout／stderr、超时和取消，拒绝非零退出或污染 stdout；Python 与 C# 两端均校验 manifest 字段、相对路径、符号链接、哈希／字节长度、记录行数、重复外部 ID、raw_ref 和 complete 状态。
- 验证命令与退出码：`uv sync --all-packages --locked`（0）；baoyan 既有测试（0，5 项）；collector-host 测试（0，4 项）；`uv lock --check`（0）；Node 检查／测试／构建（0，16 项 Node 测试）；Application／Infrastructure／Desktop／Core.Tests Debug 编译（0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests -c Debug --no-build --no-restore`（0）；Desktop.Tests Release 编译（0，0 警告／0 错误）；`scripts/package.py`（0，现已包含两套 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 匿名握手（0）；契约 JSON 检查和 `git diff --check`（0）。Desktop.Tests 在 Linux 运行退出 150，原因是缺少 `Microsoft.WindowsDesktop.App`，按环境缺口记录。
- 关键结果与证据路径：Python fixture 验证 manifest 最后写入、raw_ref、匿名 baoyan 记录映射、hash／count／路径失败；C# fixture 验证 request snake_case、文件哈希／路径／记录计数失败；人用 `baoyan-cli` 行为未改变；详见 `artifacts/implementation/P02-D/20260905T170000Z/results.json`。
- 未验证的真实条件：真实 baoyan HTTP／登录、随包 Windows Python 和 Windows 原生 collector 执行、Windows 原生 WPF／DPAPI／安装生命周期、真实来源范围和首轮校院种子；这些不阻塞 P03 日期与 admissions 领域开发。
- 尚未解决的问题：当前便携包还没有随包 Windows Python、collector 依赖和运行时模块，`DesktopSettings.PythonPath` 会优先使用未来的 `python/python.exe`，否则依赖配置的 `python`；完整导入到 004 admissions 关系模型留给 P03-B／P03-C。
- 下一工作包与第一项动作：P03-A；实现 `CycleResolver`／`WindowEvaluator`，把 source year、活动年份、历史事实和今年预测明确分开，逐条运行 `fixtures/window-cases.json`。

## 3.13 P03-A 日期字段、窗口解析与历史／预测边界（2026-09-05）

- 工作包：P03-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Core/Admissions/CycleResolver.cs`、`src/ToolsTouch.Core/Admissions/WindowEvaluator.cs`、`tests/ToolsTouch.Core.Tests/WindowTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P03-A/20260905T180000Z/results.json`、`artifacts/implementation/P03-A/20260905T180000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：无数据库迁移；协议保持 v2；新增并执行 `window-v1` 核心规则和 `docs/design/fixtures/window-cases.json` 的 20 个合成窗口样例。
- 实际实现：`CycleResolver` 将 SourceYear、CycleYear、EntryYear 分开保存，未知或异常来源年份不因抓取时间自动归年；`WindowEvaluator` 使用输入时区转换日期，按半开区间处理 Date／Instant，当前年度事实优先，支持取消／冲突／未知状态和跨年历史投影；非闰年 2 月 29 日显式返回 Unprojectable，不静默改日期。历史预测不会改变 ActualState。
- 验证命令与退出码：Core 可执行回归（0，含 20 个窗口 fixture）；Application、Infrastructure、Desktop、Core.Tests、Desktop.Tests Release 编译（均 0，0 警告／0 错误）；Node check／16 项测试／build（均 0）；uv lock check、baoyan 5 项测试和 collector 4 项测试（均 0）；重新打包（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 匿名握手（0）；`git diff --check`（0）。证据见 `artifacts/implementation/P03-A/20260905T180000Z/results.json`。
- 未验证的真实条件：Linux 缺少 `Microsoft.WindowsDesktop.App`，Desktop.Tests 运行仍为退出码 150；真实 baoyan／来源账号、来源范围和首轮校院种子仍未定；真实 provider、Gmail、Windows 原生和发信仍未验收。
- 尚未解决的问题：窗口结果尚未接入招生观察数据库、baoyan manifest 导入和学校查询视图；这些分别由 P03-B—P03-D 完成。
- 下一工作包与第一项动作：P03-B；把 collector manifest／records.jsonl 映射到统一招生轮次，保留 raw_ref、SourceYear 和坏批次隔离。

## 3.14 P03-B baoyan 原始机器导出与 schemaVersion=1（2026-09-05）

- 工作包：P03-B
- 状态：Verified
- 实际变更文件：`collector-host/src/tools_touch_collector/manifest.py`、`collector-host/tests/test_manifest.py`、`collector-host/tests/test_baoyan.py`、`src/ToolsTouch.Infrastructure/Collection/CollectorManifestValidator.cs`、`contracts/collector-manifest-v1.schema.json`、`tests/ToolsTouch.Core.Tests/CollectorTests.cs`、`artifacts/implementation/P03-B/20260905T183000Z/results.json`、`artifacts/implementation/P03-B/20260905T183000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：无数据库迁移；collector manifest 固定为 baoyan `schema_version=1`，机器记录继续使用 snake_case、`records.jsonl` 与 `raw/*`，应用协议保持 v2。
- 实际实现：共享 JSON Schema 和 Python／C# 双端校验现在要求来源身份、完整 scope、有效年份、支持的夏令营／预推免类型；`expected` 不得小于实际扫描数；原始 `raw_ref`、官网／报名链接和缺失年份继续保留，分页总数变化仍会中止导出。
- 验证命令与退出码：collector-host 5 项 Python 测试（0）；Infrastructure 与 Core.Tests Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 P03-A 20 个窗口样例）；共享 schema JSON 检查（0）；P03-B 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；`git diff --check`（0）。证据见 `artifacts/implementation/P03-B/20260905T183000Z/results.json`。
- 未验证的真实条件：真实 baoyan HTTP／来源账号和 Windows 原生 collector 执行；来源范围及首轮校院种子仍未定；Windows WPF／DPAPI、真实 provider／Gmail 和发信仍未验收。
- 尚未解决的问题：机器记录尚未进入统一 School／Department／AdmissionProgram／AdmissionRound 及 WindowObservation，需要 P03-C 的导入事务和歧义队列。
- 下一工作包与第一项动作：P03-C；新增 AdmissionImportService，在独立暂存中按稳定外部 ID 幂等导入，匹配校院／项目／批次并保留无法安全合并的歧义。

## 3.15 P03-C AdmissionImportService、别名／批次匹配与暂存发布（2026-09-05）

- 工作包：P03-C
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Admissions.cs`、`src/ToolsTouch.Infrastructure/Collection/AdmissionImportService.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/AdmissionImportTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`src/ToolsTouch.Core/Admissions/CycleResolver.cs`、`tests/ToolsTouch.Core.Tests/WindowTests.cs`、`artifacts/implementation/P03-C/20260905T190000Z/results.json`、`artifacts/implementation/P03-C/20260905T190000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：无数据库迁移；协议保持 v2；collector manifest v1 导入到既有 004／005 招生与证据表；修正 `CycleResolver` 使未知 SourceYear 的 CycleYear 保持 null。
- 实际实现：导入服务在校验 manifest 后才开启写事务，原子保留 raw artifact／SourceSnapshot，按稳定外部身份匹配或创建学校／学院／未细分项目／轮次，保存 WindowObservation；同一 manifest 重复导入不增加 DataRevision，SourceYear、CycleYear、EntryYear 和未知年份分开保存；同名实体无法安全判断时写 Pending EntityResolution 并跳过业务合并；坏批次不删除上次有效数据。应用组合根已注册该服务。
- 验证命令与退出码：Infrastructure 与 Core.Tests Debug 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 20 个窗口样例、导入幂等、歧义和坏批次测试）；Desktop Release 编译（0，0 警告／0 错误）；P03-C 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，40 项 provider）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App`；`git diff --check`（0）。证据见 `artifacts/implementation/P03-C/20260905T190000Z/results.json`。
- 未验证的真实条件：真实 baoyan HTTP／来源账号和 Windows 原生执行；来源范围及首轮校院种子仍未定；Windows WPF／DPAPI、真实 provider／Gmail 和发信仍未验收。
- 尚未解决的问题：当前查询只提供基础实体分页，尚未按统一评估器聚合学校行、展示 distinct 学院状态或生成基础 CSV；由 P03-D 完成。
- 下一工作包与第一项动作：P03-D；定义学校／轮次只读查询快照，组合 WindowObservation 与历史投影结果，生成不受分页影响的基础 CSV 行。

## 3.16 P03-D 学校总览、详情与基础 CSV 导出（2026-09-05）

- 工作包：P03-D
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/AdmissionQueries.cs`、`src/ToolsTouch.Infrastructure/Collection/AdmissionQueryService.cs`、`src/ToolsTouch.Desktop/AdmissionWorkspaceViewModel.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`tests/ToolsTouch.Core.Tests/AdmissionImportTests.cs`、`artifacts/implementation/P03-D/20260905T200000Z/results.json`、`artifacts/implementation/P03-D/20260905T200000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：无数据库迁移；协议保持 v2；查询复用 `WindowEvaluator` 的当前事实／历史预测分离规则，新增学校总览、选中学校详情和基础 CSV 查询契约及可执行 fixture 覆盖。
- 实际实现：只读查询服务在一个 SQLite 读取快照中加载有效轮次及观察事实，按目标周期和时区评估当前状态、历史预测、未知和关闭状态，聚合 distinct 学院计数；总览和详情提供稳定游标分页，CSV 导出单独遍历完整结果，不受当前页限制。WPF 新增招生工作区、刷新、选校详情和 UTF-8 CSV 保存入口，保留既有设置与账户界面。
- 验证命令与退出码：Application、Infrastructure、Desktop、Core.Tests、Desktop.Tests Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 20 个窗口样例、导入幂等／歧义／坏批次隔离、学校总览／详情／CSV 测试）；P03-D 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App`；`git diff --check`（0）。证据见 `artifacts/implementation/P03-D/20260905T200000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实 baoyan HTTP／来源账号和 Windows 原生 collector 执行；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：P03 的学校视图目前是基础总览／详情／CSV，院系目录、来源覆盖和教师目录留给 P04；当前环境无法运行 Windows 原生 WPF 测试。
- 下一工作包与第一项动作：P04-A；读取 `docs/design/plans/P04-faculty.md`，实现 `SourceAdapter`、`FacultyDirectoryCrawler` 和覆盖清单，继续保留来源快照及实体歧义队列。

## 3.17 P04-A 官方导师目录适配、分页断点与覆盖清单（2026-09-05）

- 工作包：P04-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/FacultyCollection.cs`、`src/ToolsTouch.Infrastructure/Collection/OfficialFacultyDirectoryAdapter.cs`、`src/ToolsTouch.Infrastructure/Collection/FacultyDirectoryCrawler.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/FacultyCollectionTests.cs`、`artifacts/implementation/P04-A/20260905T220000Z/results.json`、`artifacts/implementation/P04-A/20260905T220000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：无数据库迁移；复用 006 已存在的 `CrawlBatch`／`CrawlItem` 检查点表和 005 来源证据表；协议保持 v2；新增可重复的官方目录 HTML fixture 与分页失败／恢复 fixture。
- 实际实现：新增 `SourceAdapter` 风格的目录契约，包含 scope 发现、Fetch、Parse、Normalize、Health；官方适配器只发现同源分页，限制公开 HTTP(S) 页面，抽取稳定外部 ID／姓名／主页／职称和原始文本。爬虫写入 `CrawlBatch`／`CrawlItem`，每次页面最多一次，失败页面可在后续调用重试，已提交页面跳过重复抓取；页面正文按哈希保存为 `SourceSnapshot`，记录 `SourceCheck` 的 Fetched／Changed／Unchanged，规范化候选保留在检查点 payload，最终生成链接到批次的覆盖清单 artifact，并区分 CompleteForDeclaredSources、Partial、UnknownDenominator。
- 验证命令与退出码：Core.Tests Debug 编译（0，0 警告／0 错误）；Core 可执行全回归（0，含官方 HTML 同源分页、失败页续接、页面快照和覆盖状态测试）；Desktop Release 编译（0，0 警告／0 错误）；P04-A 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App`；`git diff --check`（0）。证据见 `artifacts/implementation/P04-A/20260905T220000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实官方目录 HTTP／JavaScript 页面、Windows 原生 collector 执行；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：当前候选仍停留在有来源快照的目录解析结果，没有自动写入 Professor／Appointment；导师身份、跨院任职、主页迁移、公开邮箱和歧义核验留给 P04-B。
- 下一工作包与第一项动作：P04-B；读取身份与证据细则，按稳定来源 ID／canonical URL／可靠标识／姓名任职多项证据顺序匹配导师，不能按姓名全局唯一，歧义进入待核实队列。

## 3.18 P04-B 导师身份、任职关系与证据准入（2026-09-05）

- 工作包：P04-B
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/FacultyCollection.cs`、`src/ToolsTouch.Infrastructure/Migrations/007_faculty_identity.sql`、`src/ToolsTouch.Infrastructure/Collection/OfficialFacultyDirectoryAdapter.cs`、`src/ToolsTouch.Infrastructure/Collection/FacultyIdentityImportService.cs`、`src/ToolsTouch.Core/LegacyEntities.cs`、`src/ToolsTouch.Infrastructure/Persistence/Legacy/ResearchStore.cs`、`src/ToolsTouch.Infrastructure/Persistence/OrganizationRepository.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/FacultyIdentityTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`artifacts/implementation/P04-B/20260906T000000Z/results.json`、`artifacts/implementation/P04-B/20260906T000000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：新增 007 号迁移；将旧 Professor 表安全迁移为允许无主页记录，新增 `FacultyIdentityResolution` 待核实队列；协议保持 v2；新增同名、无邮箱、跨学院任职、主页迁移和重复导入 fixture。
- 实际实现：`FacultyIdentityImportService` 按来源稳定外部身份→规范主页→姓名＋任职的顺序匹配导师；多个候选或主页归属冲突只写 Pending 队列，不自动合并。导入同时建立 Appointment 和 `EvidenceClaim`，同一人跨学院保留多条任职；显式公开邮箱才进入当前联系人字段，原始 mailto／还原文本和定位随证据保存；新主页更新当前索引但旧页面快照及历史 claim 不覆盖。
- 验证命令与退出码：Core.Tests Debug 编译（0，0 警告／0 错误）；Core 可执行全回归（0，含 007 迁移、身份优先级、同名歧义、跨学院任职、主页迁移、公开邮箱和重复导入测试）；Desktop Release 编译（0，0 警告／0 错误）；P04-B 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App`；`git diff --check`（0）。证据见 `artifacts/implementation/P04-B/20260906T000000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实官方目录 HTTP／JavaScript 页面、Windows 原生 collector 执行；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：主页的深入抓取、论文作者消歧、评价来源去重和导师详情视图留给 P04-C／P04-D；当前 Pending 队列还没有桌面核验编辑入口。
- 下一工作包与第一项动作：P04-C；读取论文与评价证据细则，接入主页／论文来源的页码与 AbstractOnly／Unreadable 证据，不能因同名作者自动归属论文。

## 3.19 P04-C 主页、论文、阅读范围与评价证据（2026-09-05）

- 工作包：P04-C
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/ResearchEvidence.cs`、`src/ToolsTouch.Infrastructure/Migrations/008_research_evidence.sql`、`src/ToolsTouch.Infrastructure/Research/FacultyResearchService.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/FacultyResearchTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`artifacts/implementation/P04-C/20260906T010000Z/results.json`、`artifacts/implementation/P04-C/20260906T010000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：新增 008 号迁移；加入 `PaperReadSegment`、`PaperAttributionResolution`、`ProfessorEvaluation`；协议保持 v2；新增主页字段、唯一／重复／未知作者、阅读范围、不可读全文、评价去重 fixture。
- 实际实现：主页正文按来源快照保存，提取研究方向和带年份的招生声明并保存行定位；论文元数据独立保存，只有规范化姓名唯一匹配时写 `ProfessorPaper`，重复或未匹配作者进入待核实队列；阅读范围保存页码、抽取器版本和可选文本 artifact，明确区分 Abstract、Pages、FullText、Unreadable；第三方评价以独立 `ProfessorEvaluation` 和 `ThirdPartyReport` claim 保存，重复输入不重复发布。
- 验证命令与退出码：Core.Tests Debug 编译（0，0 警告／0 错误）；Core 可执行全回归（0，含主页声明、论文作者归属、作者歧义、阅读范围、评价来源和幂等测试）；Desktop Release 编译（0，0 警告／0 错误）；P04-C 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App`；`git diff --check`（0）。证据见 `artifacts/implementation/P04-C/20260906T010000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实主页／论文 HTTP／JavaScript 页面、Windows 原生 collector 执行；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：主页资料当前通过应用证据端口导入，尚未接入导师详情页；模型研究提示、论文深读编排和推荐分数留给 P04-D／P05。
- 下一工作包与第一项动作：P04-D；将已发布导师、任职、证据、论文阅读状态和爬虫覆盖／失败清单接入可分页 FacultyList／Detail，保持旧数据在来源失败时可查。

## 3.20 P04-D 导师列表、详情与采集覆盖入口（2026-09-05）

- 工作包：P04-D
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/FacultyQueries.cs`、`src/ToolsTouch.Infrastructure/Collection/FacultyQueryService.cs`、`src/ToolsTouch.Infrastructure/Collection/FacultyDirectoryCrawler.cs`、`src/ToolsTouch.Desktop/FacultyWorkspaceViewModel.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`tests/ToolsTouch.Core.Tests/FacultyQueryTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P04-D/20260905T132000Z/results.json`、`artifacts/implementation/P04-D/20260905T132000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：无新增数据库迁移；复用 006 的爬虫检查点、007 的导师身份关系和 008 的论文／评价证据表；协议保持 v2；新增查询分页、详情关联和覆盖状态 executable fixture。
- 实际实现：`FacultyQueryService` 提供按姓名／机构／证据筛选的稳定游标列表，详情读取当前导师、跨学院任职、证据声明、论文元数据、阅读范围／不可读状态和评价来源；覆盖查询保留批次、页数、导师数量、错误和 CompleteForDeclaredSources／Partial／UnknownDenominator 语义。桌面新增 Faculty 工作区，来源失败时仍可查询旧导师和证据；刷新、详情和覆盖入口为只读，不触发邮件发送。
- 验证命令与退出码：Core.Tests Debug 编译（0，0 警告／0 错误）；Core 可执行全回归（0，含列表游标、详情关联和三类覆盖状态）；Desktop Release 编译（0，0 警告／0 错误）；Desktop.Tests Release 编译（0，0 警告／0 错误）；P04-D 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；AgentHost 16 项测试（0）；baoyan-cli 和 collector-host 各 5 项 Python 测试（均 0）；`uv lock --check`（0）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App 10.0.0`；`git diff --check`（0）。证据见 `artifacts/implementation/P04-D/20260905T132000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实官方目录 HTTP／JavaScript 页面、Windows 原生 collector 执行、桌面视觉验收；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：导师身份 Pending 队列尚未提供桌面人工核验编辑入口；主页／论文真实抓取、模型研究分析和推荐输入留给后续工作包。
- 下一工作包与第一项动作：P05-A；读取 `docs/design/plans/P05-recommendations.md` 和资料统计细则，先把已确认 CV、论文阅读片段、任职／招生证据按版本冻结为可审计研究输入快照。

## 3.21 P05-A 资料确认、需求偏好与背景统计（2026-09-05）

- 工作包：P05-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/ProfilesStatistics.cs`、`src/ToolsTouch.Infrastructure/Migrations/009_profiles_statistics.sql`、`src/ToolsTouch.Infrastructure/Recommendation/ProfileFactsService.cs`、`src/ToolsTouch.Infrastructure/Recommendation/PreferenceService.cs`、`src/ToolsTouch.Infrastructure/Recommendation/StatisticsBuilder.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/ProfilesStatisticsTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P05-A/20260905T133000Z/results.json`、`artifacts/implementation/P05-A/20260905T133000Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：新增 009 号迁移，建立 `AdmissionCaseSample` 和 `CohortStatistic`；复用 005 已有 `StructuredFactsJson`、`ExtractionVersion`、`ConfirmedAt` 和 `PreferenceRevision`；协议保持 v2；新增资料、需求、样本去重、分母／未知／自述零和小样本 fixture。
- 实际实现：`ProfileFactsService` 以追加版本确认本科类别、专业、排名分子／分母、GPA 满分、语言和论文状态，非法排名／GPA／论文状态在写入前拒绝；`PreferenceService` 保留原始需求文本并将明确要求解析为 Hard／Soft／Ambiguous，不能把模糊表达静默变成硬淘汰；`StatisticsBuilder` 按年度、项目、轮次和结果阶段取样，按 `DedupGroup` 去重，985、排名分位、GPA 满分分组和论文分布分别输出分母、未知数、已知零及样本量不足标记。
- 验证命令与退出码：Core、Application、Infrastructure、Desktop、Core.Tests、Desktop.Tests Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含资料版本、约束强弱、样本幂等／冲突、重复经验去重、排名／GPA／985／论文分母和未知／零）；P05-A 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；AgentHost 16 项测试（0）；baoyan-cli 和 collector-host 各 5 项 Python 测试（均 0）；`uv lock --check`（0）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App 10.0.0`；`git diff --check`（0）。证据见 `artifacts/implementation/P05-A/20260905T133000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实 CV PDF 提取、资料确认 UI、真实招生经验来源、Windows 原生 collector 执行；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：当前 P05-A 已提供资料／需求／统计应用端口，但没有推荐资格、分项评分、语义评估和推荐中心；它们按顺序留给 P05-B—P05-D。
- 下一工作包与第一项动作：P05-B；读取评分 fixture 和资格细则，实现 Eligible／Ineligible／NeedsVerification 三态、未知区间、权重归一和稳定排序，并将当时的候选特征与规则版本写入推荐快照。

## 3.22 P05-B 三态资格、稳定评分与推荐快照（2026-09-05）

- 工作包：P05-B
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Ranking.cs`、`src/ToolsTouch.Application/Recommendations.cs`、`src/ToolsTouch.Infrastructure/Recommendation/EligibilityEvaluator.cs`、`src/ToolsTouch.Infrastructure/Recommendation/Ranker.cs`、`src/ToolsTouch.Infrastructure/Recommendation/RecommendationRepository.cs`、`src/ToolsTouch.Infrastructure/Migrations/010_recommendation.sql`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/RankingTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P05-B/20260905T134100Z/results.json`、`artifacts/implementation/P05-B/20260905T134100Z/package.zip`、本状态文件
- 迁移／协议／fixture 版本变化：新增 010 号迁移，建立 `RecommendationRun` 和 `RecommendationItem`；协议保持 v2；使用 `docs/design/fixtures/ranking-cases.json` 作为实际 Ranker 输入，保存候选特征 Artifact、资料／需求关联、规则／算法版本和分项证据。
- 实际实现：`EligibilityEvaluator` 将可靠正式硬条件冲突判为 Ineligible，缺失或不可靠条件判为 NeedsVerification，软条件不参与正式淘汰；`Ranker` 验证权重总和与分项范围，计算 known score、coverage、未知区间 [lower, upper]，按资格、充分／不足覆盖、保守下界、已知分、来源质量和稳定 ID 排序；`RecommendationRepository` 以不可变候选快照和分项记录发布 run，重复相同输入可重开，改变算法输入返回冲突。
- 验证命令与退出码：Core、Application、Infrastructure、Desktop、Core.Tests、Desktop.Tests Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含评分黄金样例、三态资格、非法权重／分数、稳定排序、快照重现和 run 冲突）；P05-B 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 AgentHost v2 握手（0，status 返回 40 项 provider）；`uv lock --check`（0）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App 10.0.0`；`git diff --check`（0）。证据见 `artifacts/implementation/P05-B/20260905T134100Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／Gmail／发信、真实招生资格规则和真实用户资料；来源范围及首轮校院种子仍未定；Pi 语义输出和推荐中心 UI 留给 P05-C／P05-D。
- 尚未解决的问题：目前仓储接受已经计算好的候选与分项，尚未由真实学院项目条件生成资格输入，也未接入模型语义批评估和候选召回。
- 下一工作包与第一项动作：P05-C；读取 AgentHost 结构化输出与 research tool 约束，增加统一候选集的 Pi 语义评估、引用校验、预算与覆盖范围，拒绝无引用或越界结果。

## 3.23 P05-C Pi 语义批评估、研究提示与输出校验（2026-09-05）

- 工作包：P05-C
- 状态：Verified
- 实际变更文件：`agent-host/src/outputs.ts`、`agent-host/src/tools.ts`、`agent-host/src/index.ts`、`agent-host/src/contracts.test.ts`、`src/ToolsTouch.Application/SemanticEvaluation.cs`、`src/ToolsTouch.Application/AgentProtocol.cs`、`src/ToolsTouch.Infrastructure/Pi/AgentBridge.cs`、`src/ToolsTouch.Infrastructure/Pi/Legacy/ToolDispatcher.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/SemanticEvaluationTests.cs`、`tests/ToolsTouch.Core.Tests/AgentProtocolTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P05-C/20260905T142000Z/results.json`、`artifacts/implementation/P05-C/20260905T142000Z/portable.zip`、本状态文件
- 迁移／协议／fixture 版本变化：数据库迁移保持 010；AgentHost／C# 继续使用协议 v2，新增 `semantic` policy，语义输出 schema 版本为 2；无真实来源或模型 fixture。
- 实际实现：Host 增加只读 semantic policy 与批量 JSON schema，禁止写导师／草稿工具；`SemanticEvaluationService` 规划最多 200 个统一候选、保留必看／方向多样性对象，并独立限制默认 20 个重点研究对象、每人 3 篇论文和每篇 10 页；C# 生成带 scope、维度、证据和预算的研究提示，并在业务边界重新检查目标范围、完整维度、离散 rubric、引用 ID／URL 绑定、未知理由和额外字段；未返回对象报告为未评估，未知分项保留 null 并可转成带证据的 Semantic ranking component。
- 验证命令与退出码：AgentHost 16 项测试（0）；Core、Application、Infrastructure、Desktop、Core.Tests、Desktop.Tests Release 编译（均 0，0 警告／0 错误）；C# 可执行全回归（0，含语义提示／预算／scope／引用拒绝组）；P05-C 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App 10.0.0`。首次把 ZIP 文件直接传给校验脚本退出 1，随后按解压目录接口验证通过；证据见 `artifacts/implementation/P05-C/20260905T142000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／模型／来源账号、实时引用正确性和真实招生规则；推荐中心 UI、语义结果持久化对比和学院／导师两级展示留给 P05-D；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：当前 P05-C 提供 Host policy、提示构造和 C# 输出校验，但尚未把真实候选池编译为 RecommendationRun 的语义分项，也尚未提供推荐中心 Compare ViewModel。
- 下一工作包与第一项动作：P05-D；读取 `RecommendationRepository` 与现有学校／导师 workspace 查询，建立推荐中心与 Compare ViewModel，显示 run scope、资格三态、分项、证据、缺失项、区间和不同研究覆盖。

## 3.24 P05-D Recommendation Center 与 Compare ViewModel（2026-09-05）

- 工作包：P05-D
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Recommendations.cs`、`src/ToolsTouch.Infrastructure/Recommendation/RecommendationRepository.cs`、`src/ToolsTouch.Desktop/RecommendationCenterViewModel.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`tests/ToolsTouch.Core.Tests/RankingTests.cs`、`tests/ToolsTouch.Desktop.Tests/Program.cs`、`artifacts/implementation/P05-D/20260905T150000Z/results.json`、`artifacts/implementation/P05-D/20260905T150000Z/portable.zip`、本状态文件
- 迁移／协议／fixture 版本变化：数据库迁移保持 010；增加推荐运行列表查询和 Recommendations 页面，无 schema／协议升级；桌面合成 UI fixture 使用 Base 与 Semantic 两个不可变 run。
- 实际实现：`RecommendationRepository.ListRuns` 按创建时间和 ID 稳定列出运行；`RecommendationCenterViewModel` 读取 run／candidate snapshot／item，显示候选范围、资料修订、算法、年度、状态、资格三态、覆盖分组、排名、已知分、未知区间、分项、证据 ID、缺失事实和理由；Compare 以 `TargetKind:TargetId` 合并两个 run，显示资格／排名／分数变化；WPF 新增 Recommendation Center 页面并接入主刷新，UI 合成场景覆盖基础与语义运行的 50→75 分变化。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含推荐运行列表）；P05-D 便携包构建（0）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）；Desktop.Tests Linux 运行退出 150，因缺少 `Microsoft.WindowsDesktop.App 10.0.0`，Recommendations UI 场景未能在 Linux 执行。证据见 `artifacts/implementation/P05-D/20260905T150000Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPAPI、真实 provider／模型／来源账号和实时来源覆盖；真实候选池生成、语义结果持久化和端到端模型到推荐执行仍需后续接线；来源范围及首轮校院种子仍未定。
- 尚未解决的问题：P05-D 展示已发布推荐快照，但尚未把真实候选生成、P05-C 验证后的 semantic 输出写回 RecommendationRun，也没有推荐运行创建入口；这些属于后续业务接线和真实数据验收范围。
- 下一工作包与第一项动作：P06-A；读取邮件草稿设计和现有 `OutreachService`／版本约束，抽取 DraftRequest／DraftService，保证模型重试不覆盖人工稿且引用资料版本一致。

## 3.25 P06-A DraftRequest、草稿服务与旧版本兼容（2026-09-05）

- 工作包：P06-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Core/OutreachRecords.cs`、`src/ToolsTouch.Application/Drafting.cs`、`src/ToolsTouch.Infrastructure/Migrations/011_drafting.sql`、`src/ToolsTouch.Infrastructure/Persistence/MigrationRunner.cs`、`src/ToolsTouch.Infrastructure/Legacy/OutreachService.cs`、`src/ToolsTouch.Infrastructure/Legacy/DraftService.cs`、`src/ToolsTouch.Infrastructure/Pi/Legacy/ToolDispatcher.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/AccountsViewModel.cs`、`tests/ToolsTouch.Core.Tests/DraftingTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P06-A/20260905T161500Z/results.json`、`artifacts/implementation/P06-A/20260905T161500Z/portable.zip`
- 迁移／协议／fixture 版本变化：新增 011 号迁移，在 `Outreach` 保存预约、需求、分析 artifact、语言、模型、提示版本、请求 JSON 和引用 JSON；新增 `DraftChange` 保存 Generated／Edited 修订快照；协议保持 v2。旧 001—003 fixture 增加已有草稿，验证 007 的 Professor 表重建不丢失外键记录，并由 011 回填首个 Generated 快照。
- 实际实现：Core 提供共享 `Draft`／`DraftChange` 记录，Application 提供 `DraftRequest` 校验和 `IDraftService`；Infrastructure `DraftService` 比较稳定请求键对应的不可变请求 envelope，重试返回已保存版本，不使用当前正文比较，因此人工编辑不会被覆盖；不同键仍写入同一导师的下一版本。`OutreachService` 保留旧参数入口，补充 metadata、修订 CAS、生成／编辑快照和旧默认值。Pi 的 `create_outreach_draft` 改走新服务，仍受 Draft context、目标匹配、来源已抓取、确认 CV 和 no-send 边界约束。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含旧草稿迁移、请求校验、metadata 冻结、相同键恢复、人工编辑保护、CAS、新版本和旧入口兼容）；AgentHost 16 项测试（0）；P06-A 便携包构建（0，含两组 Python 测试各自 8／5 项）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）。证据见 `artifacts/implementation/P06-A/20260905T161500Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`，WPF UI 仍需 Windows 原生运行；此次没有真实模型、Gmail 登录、真实资料来源或发信。
- 尚未解决的问题：本包冻结请求携带的分析／需求／任职引用和 CV 路径信息，但还没有发送确认消费、不可变 MIME artifact、Gmail Unknown 核对／回复和申请记录；这些留给 P06-B—P06-D。实际写作质量与真实来源一致性仍待账号和来源条件。
- 下一工作包与第一项动作：P06-B；读取发送状态设计，抽取 `SendCoordinator`，先把当前 draft revision、CV hash、账号和正文生成不可变 MIME artifact，再实现过期确认、双击和传输前消费事务。

## 3.26 P06-B SendCoordinator、确认消费与不可变 MIME（2026-09-05）

- 工作包：P06-B
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Sending.cs`、`src/ToolsTouch.Infrastructure/Migrations/012_send_confirmation.sql`、`src/ToolsTouch.Infrastructure/Persistence/ArtifactStore.cs`、`src/ToolsTouch.Infrastructure/Google/MimeComposer.cs`、`src/ToolsTouch.Infrastructure/Google/SendCoordinator.cs`、`src/ToolsTouch.Infrastructure/Google/GmailService.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/GmailTests.cs`、`tests/ToolsTouch.Core.Tests/SendConfirmationTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P06-B/20260905T181500Z/results.json`、`artifacts/implementation/P06-B/20260905T181500Z/portable.zip`
- 迁移／协议／fixture 版本变化：新增 012 号迁移，建立 `SendConfirmation` 与 `SendAttempt`；确认保存草稿版本／修订、账号、收件内容、CV hash、快照 hash、固定 MIME artifact 和过期时间，attempt 保存固定 RFC Message-ID、MIME hash、provider 回执和状态；协议保持 v2。
- 实际实现：Application 增加 `ISendCoordinator`、`ISendTransport`、预览／attempt／不可变发送 envelope；`SendCoordinator.Prepare` 校验可发送草稿与 CV，生成受 20 MiB 上限约束的 MIME，并写入 hash-addressed artifact。`Confirm` 在事务内检查未消费、未过期、账号、草稿版本／正文／收件人／附件和 CV hash，消费确认、创建唯一未决 attempt、设置 `Outreach=Sending`；`SendAsync` 先校验 artifact，再用 CAS 抢占 `Transmitting`，只把预构建字节传给 Gmail，明确拒绝记 Failed，可能已到达的异常记 Unknown。桌面发送路径已改为预览→用户确认→消费→传输；旧 `OutreachService` 发送入口保留。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 Gmail MIME adapter、确认快照、附件 TOCTOU、一次性消费、并发 claim、Unknown 和过期）；AgentHost 16 项测试（0）；P06-B 便携包构建（0，baoyan-cli 9 项、collector-host 5 项 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）。证据见 `artifacts/implementation/P06-B/20260905T181500Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`，WPF UI、DPAPI 和真实 Gmail 仍需 Windows／账号验收；此次没有真实邮件发送。
- 尚未解决的问题：Unknown 的 Gmail Sent 核对、跨账号保护的回复关联和自身消息过滤留给 P06-C；申请记录、官网提交与记录器字段留给 P06-D 及后续 P07。当前 SendCoordinator 的测试传输为替身。
- 下一工作包与第一项动作：P06-C；扩展 Gmail 核对记录，使用原 `SenderAccount`、固定 RFC Message-ID、收件人和 SENT 标签查询 Unknown，保存 provider message/thread 并实现去除自身发件人的回复状态。

## 3.27 P06-C Gmail 核对、回复和原账号记录（2026-09-05）

- 工作包：P06-C
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/Tracking.cs`、`src/ToolsTouch.Infrastructure/Migrations/013_delivery_tracking.sql`、`src/ToolsTouch.Infrastructure/Google/TrackingModels.cs`、`src/ToolsTouch.Infrastructure/Google/GmailService.cs`、`src/ToolsTouch.Infrastructure/Legacy/OutreachService.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/GmailTests.cs`、`artifacts/implementation/P06-C/20260905T190000Z/results.json`、`artifacts/implementation/P06-C/20260905T190000Z/portable.zip`
- 迁移／协议／fixture 版本变化：新增 013 号迁移，建立 `DeliveryCheck` 和 `EmailMessage`；协议保持 v2。`DeliveryCheck` 保存原 `SenderAccount`、固定 RFC Message-ID、收件人、provider message／thread、核对结果和错误；`EmailMessage` 保存线程邮件的账号、发件人、收件人、`In-Reply-To`、`References`、自身消息标记和关系状态。
- 实际实现：Gmail Sent 核对限定原发送账号、固定 RFC Message-ID、收件人和 `SENT` 标签，明确区分 Found／NotFound／Ambiguous／Failed；多候选或无候选时保留 `Unknown`，唯一候选才更新为 `Sent` 并关联 attempt。回复同步按账号和已确认的 thread 保存邮件元数据，过滤自身消息后计算回复状态；线程已被其他申请占用时保存 `Ambiguous`，不自动变更业务申请阶段。核对和回复记录均可查询审计。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含原账号、NotFound／Ambiguous、固定 RFC 候选、线程元数据、自身消息过滤和回复同步）；P06-C 便携包构建（0，Node 16 项测试、baoyan-cli 9 项、collector-host 5 项 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）。证据见 `artifacts/implementation/P06-C/20260905T190000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`，WPF UI、DPAPI 和真实 Gmail 仍需 Windows／账号验收；本包没有真实邮件发送。
- 尚未解决的问题：申请记录、官网提交与材料／备注／提醒字段留给 P06-D；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。Gmail 多候选、跨账号和回复归属目前由模拟数据覆盖，真实账号验收仍待用户条件。
- 下一工作包与第一项动作：P06-D；读取申请记录细则，实现 `ApplicationCaseService` 和申请事件／材料／备注／提醒持久化，再接入桌面申请记录 UI，保持阶段变更需用户确认且不由发送或导入自动推进。

## 3.28 P06-D ApplicationCase、申请事件和跟踪工作区（2026-09-05）

- 工作包：P06-D
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/ApplicationCases.cs`、`src/ToolsTouch.Infrastructure/Migrations/014_application_cases.sql`、`src/ToolsTouch.Infrastructure/Tracking/ApplicationCaseService.cs`、`src/ToolsTouch.Desktop/ApplicationWorkspaceViewModel.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`tests/ToolsTouch.Core.Tests/ApplicationCaseTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P06-D/20260905T200000Z/results.json`、`artifacts/implementation/P06-D/20260905T200000Z/portable.zip`
- 迁移／协议／fixture 版本变化：新增 014 号迁移，建立 `ApplicationCase`、追加式 `ApplicationEvent`、`CaseOutreach`、`ApplicationMaterial` 和 `Reminder`；协议保持 v2。申请身份按学院／项目／轮次／导师／年度约束范围，材料有独立状态与 revision，提醒支持 Pending／Completed／Dismissed。
- 实际实现：`ApplicationCaseService` 校验部门、项目和轮次的学院范围，申请字段采用 revision CAS；阶段变更由显式服务调用追加 `StageChanged` 事件，官网提交写入提交时间／编号／回执和 `SubmissionRecorded` 事件，均不自动从邮件状态推导。`CaseOutreach` 只保存关系，不复制草稿；邮件关联事件可审计。桌面新增 Applications 工作区，提供阶段确认对话框、申请字段、官网回执、材料清单、下一步、备注和提醒操作。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 ApplicationCaseTests、Sent／Submitted 分离、阶段事件、旧 revision 拒绝、材料 CAS、提醒和跨学院项目拒绝）；P06-D 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 5 项 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）。证据见 `artifacts/implementation/P06-D/20260905T200000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`，WPF UI、DPAPI 和真实 Gmail 仍需 Windows／账号验收；本包没有真实官网提交或真实邮件发送。
- 尚未解决的问题：记录器通用模型、自定义字段、批量编辑、查询视图、导出／恢复和导入留给 P07；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。
- 下一工作包与第一项动作：P07-A；读取记录器细则，实现 `Collection`／`RecordRef`／字段定义和系统集合适配，保持领域事实只保存在既有领域表，编辑权限区分 Editable、OverrideWithReason 和 SystemManaged。

## 3.29 P07-A 记录器通用模型、系统集合和字段权限（2026-09-05）

- 工作包：P07-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/RecordWorkspace.cs`、`src/ToolsTouch.Infrastructure/Migrations/015_record_workspace.sql`、`src/ToolsTouch.Infrastructure/Tracking/SystemCollectionAdapter.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/RecordWorkspaceTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P07-A/20260905T210000Z/results.json`、`artifacts/implementation/P07-A/20260905T210000Z/portable.zip`
- 迁移／协议／fixture 版本变化：新增 015 号迁移，建立 `Collection`、`RecordRef`、`FieldDefinition`、`FieldChoice`、`FieldValue`、`RecordRelation`、`ViewDefinition`、`RecordChange` 和 `ImportBatch`；协议保持 v2。字段类型固定为 Text／Number／DateTime／Boolean／Url／Choice／MultiChoice／Relation，编辑权限固定为 Editable／OverrideWithReason／SystemManaged。
- 实际实现：`SystemCollectionAdapter` 注册学校、学院／系、招生项目、招生轮次、导师、官网申请、邮件联系和任务 8 个系统集合，按领域实体 ID 幂等建立 `RecordRef`。系统字段保存 `SystemBinding` 与编辑权限，领域值继续从原领域表查询，初始化不会写入 `FieldValue`；ApplicationCase 的阶段标记为需显式命令的 `OverrideWithReason`，优先级／备注等业务字段标记为 `Editable`，发送／任务状态保持 `SystemManaged`。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含系统集合、RecordRef 全量映射、字段权限、无重复 FieldValue 和同步幂等）；P07-A 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 5 项 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）。证据见 `artifacts/implementation/P07-A/20260905T210000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`，WPF UI、DPAPI 和真实 Gmail 仍需 Windows／账号验收；本包未运行真实账号、来源或发送。
- 尚未解决的问题：自定义集合／字段创建 UI、类型值写入、单元格 CAS、批量编辑、撤销、查询视图、导出恢复和导入留给 P07-B—P07-E；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。
- 下一工作包与第一项动作：P07-B；实现 `FieldSchemaService` 和 `RecordGrid` 写入命令，按字段类型验证唯一 typed slot，固定 RecordRef／字段定义 revision，系统字段路由到领域命令并拒绝直接覆盖 SystemManaged 状态。

## 3.30 P07-B 记录器编辑、字段校验、批量修改和撤销（2026-09-05）

- 工作包：P07-B
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/RecordWorkspace.cs`、`src/ToolsTouch.Infrastructure/Tracking/RecordWorkspaceService.cs`、`src/ToolsTouch.Infrastructure/Tracking/FieldSchemaService.cs`、`src/ToolsTouch.Desktop/RecordGridViewModel.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`tests/ToolsTouch.Core.Tests/RecordEditingTests.cs`、`tests/ToolsTouch.Core.Tests/RecordWorkspaceTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P07-B/20260905T220000Z/results.json`、`artifacts/implementation/P07-B/20260905T220000Z/portable.zip`
- 迁移／协议／fixture 版本变化：沿用 P07-A 的 015 号记录器迁移；本包没有新增数据库迁移或协议版本，应用协议保持 v2。
- 实际实现：`RecordWorkspaceService` 支持自定义集合／字段／选项／记录创建、Text／Url／Number／DateTime／Boolean／Choice／MultiChoice／Relation 类型值校验、集合与记录归属校验、记录和字段定义 revision CAS、稳定命令幂等、批量编辑及条件撤销；成功写入更新 `WorkspaceMeta.DataRevision`。系统字段拒绝直接覆盖并返回需领域命令的错误。`RecordGridViewModel` 和 Records 页提供自定义表、字段、记录和类型化单元格编辑入口。
- 验证命令与退出码：六个 C# 工程 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 RecordWorkspaceTests 和 RecordEditingTests）；P07-B 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 5 项 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`（均 0）。证据见 `artifacts/implementation/P07-B/20260905T220000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`；WPF UI、DPAPI、真实 Gmail／账号和真实邮件仍需 Windows／账号验收。本包没有真实来源提交或真实邮件发送。
- 尚未解决的问题：P07-C 查询视图、P07-D 导出恢复和 P07-E 表格导入尚未实现；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。
- 下一工作包与第一项动作：P07-C；实现 `QueryCompiler`、`FilterBuilder` 和 `ViewService`。本次按用户要求在 P07-B 完成后暂停。

## 3.31 P07-C 记录器查询、筛选、排序和保存视图（2026-09-05）

- 工作包：P07-C
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/RecordQuery.cs`、`src/ToolsTouch.Infrastructure/Tracking/RecordQueryCompiler.cs`、`src/ToolsTouch.Infrastructure/Tracking/RecordQueryService.cs`、`src/ToolsTouch.Infrastructure/Tracking/ViewService.cs`、`src/ToolsTouch.Desktop/RecordGridViewModel.cs`、`src/ToolsTouch.Desktop/AppServices.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`tests/ToolsTouch.Core.Tests/RecordQueryTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P07-C/20260905T230000Z/results.json`、`artifacts/implementation/P07-C/20260905T230000Z/portable.zip`
- 迁移／协议／fixture 版本变化：复用 015 号迁移中的 `ViewDefinition`，没有新增数据库迁移或协议版本；应用协议保持 v2。
- 实际实现：`RecordQueryCompiler` 限制过滤 AST 为最多 5 层、100 个节点，只接受集合字段白名单和固定系统绑定，生成参数化 SQL；支持 `and`／`or`、`eq`、`contains`、数值／日期范围、空值和关系 `EXISTS` 过滤。`RecordQueryService` 组合系统领域字段、自定义 `FieldValue` 和 `RecordRelation`，支持显式空值顺序、稳定 RecordId 兜底和并列键集分页。`ViewService` 支持视图保存、更新 revision CAS、复制和重启恢复；Records 页接入筛选、排序和保存视图。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行全回归（0，含 RecordQueryTests）；P07-C 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 5 项 Python 测试）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，semantic policy、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check` 和证据 JSON 校验（均 0）。证据见 `artifacts/implementation/P07-C/20260905T230000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`；WPF UI、DPAPI、真实 Gmail／账号和真实邮件仍需 Windows／账号验收。
- 尚未解决的问题：P07-D 全量导出／恢复和 P07-E 表格导入尚未实现；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。
- 下一工作包与第一项动作：P07-D；实现 `ExportSnapshotService`、JSONL business bundle、Python XLSX 导出和 Restore。

## 3.32 P07-D 全量导出、XLSX 投影和隔离恢复（2026-09-05）

- 工作包：P07-D
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/WorkspaceExport.cs`、`src/ToolsTouch.Infrastructure/Persistence/WorkspaceExportService.cs`、`collector-host/src/tools_touch_collector/xlsx.py`、`collector-host/src/tools_touch_collector/__main__.py`、`collector-host/pyproject.toml`、`collector-host/tests/test_xlsx.py`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/WorkspaceRoundTripTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P07-D/20260905T235000Z/results.json`、`artifacts/implementation/P07-D/20260905T235000Z/portable.zip`
- 迁移／协议／fixture 版本变化：沿用 015 号记录器迁移和 v2 应用协议，没有新增数据库迁移；增加业务 bundle manifest v1、JSONL 表导出和隔离恢复 fixture。
- 实际实现：`WorkspaceExportService` 先生成 SQLite 一致性快照，再按表输出 JSONL 和哈希 manifest；支持全工作区／集合范围、业务历史、可选 ArtifactStore 附件和安全路径校验。Python `openpyxl` 导出器支持表分 Sheet、Sheet 名清洗与冲突处理、公式前缀转义、长文本保留和输出路径校验。全量 bundle 恢复到新工作区时校验 schema／文件哈希／行数，恢复附件，并把恢复中断的 `Sending` 状态归一为 `Unknown` 与需复核事件。Core round-trip 覆盖自定义值、关系、保存视图、历史、附件、XLSX 桥接和恢复。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests -c Release --no-build --no-restore`（0）；collector-host Python 测试（0，7 项）；P07-D 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 7 项和 Core 回归通过）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，protocol 2、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check`、证据 JSON 校验（均 0）。证据见 `artifacts/implementation/P07-D/20260905T235000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`；WPF UI、DPAPI、真实 Gmail／账号和真实邮件仍需 Windows／账号验收。本包没有真实来源提交或真实邮件发送。
- 尚未解决的问题：P07-E 表格导入、P08 界面／性能／安装／真实验收尚未实现；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。
- 下一工作包与第一项动作：P07-E；实现 `ImportPreview`、`ImportBatch` 和字段映射，先覆盖重复键、类型错误、未知列、部分成功隔离与不触发邮件发送。

## 3.33 P07-E 表格导入预览、批次提交和字段映射（2026-09-05）

- 工作包：P07-E
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Application/RecordWorkspace.cs`、`src/ToolsTouch.Infrastructure/Migrations/016_import_batches.sql`、`src/ToolsTouch.Infrastructure/Tracking/ImportPreviewService.cs`、`src/ToolsTouch.Infrastructure/Tracking/RecordWorkspaceService.cs`、`collector-host/src/tools_touch_collector/xlsx.py`、`collector-host/src/tools_touch_collector/__main__.py`、`collector-host/tests/test_xlsx.py`、`src/ToolsTouch.Desktop/AppServices.cs`、`tests/ToolsTouch.Core.Tests/ImportTests.cs`、`tests/ToolsTouch.Core.Tests/DomainDataTests.cs`、`tests/ToolsTouch.Core.Tests/Program.cs`、`artifacts/implementation/P07-E/20260905T171500Z/results.json`、`artifacts/implementation/P07-E/20260905T171500Z/portable.zip`
- 迁移／协议／fixture 版本变化：新增 016 号迁移，为 `ImportBatch` 保存源文件 hash、格式／Sheet、集合、字段映射、重复策略和错误报告；应用协议保持 v2。
- 实际实现：`ImportPreviewService` 支持 UTF-8／UTF-16 CSV、引号和换行解析，及通过 collector-host 读取指定 XLSX Sheet；预览阶段检查未知列、必填值、Text／URL／Number／DateTime／Boolean／Choice／MultiChoice／Relation 类型、关系目标、稳定键重复和系统字段保护。提交阶段核对源文件 hash，按稳定键更新或按批次生成内部 RecordRef，使用稳定 cell command id、RecordRef revision 和 RecordChange 历史；整批校验失败不写入，重复提交返回已完成批次，PreviewOnly 和取消均不会触发外部动作。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Core.Tests -c Release --no-build --no-restore`（0，含 ImportTests）；collector-host 测试（0，8 项）；P07-E 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 8 项和 Core 回归通过）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，protocol 2、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check` 和证据 JSON 校验（均 0）。证据见 `artifacts/implementation/P07-E/20260905T171500Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`；WPF UI、DPAPI、真实 Gmail／账号和真实邮件仍需 Windows／账号验收。本包没有真实来源提交或真实邮件发送。
- 尚未解决的问题：P08-A 桌面记录器界面与导入预览交互、P08-B 性能、P08-C 安装和 P08-D 真实验收尚未实现；当前表格导入提交范围固定为自定义集合，系统管理字段只报告保护错误；来源范围和首批校院种子名单仍未定，按 `START-HERE.md` 的独立推进约定不阻塞后续编码。
- 下一工作包与第一项动作：P08-A；把记录查询、字段编辑、导入预览／错误报告和导出恢复接入桌面交互，继续保持表格操作不发送邮件。

## 3.34 P08-A 桌面主题、虚拟化表格和记录器导入交互（2026-09-05）

- 工作包：P08-A
- 状态：Verified
- 实际变更文件：`src/ToolsTouch.Desktop/Resources/Theme.xaml`、`src/ToolsTouch.Desktop/App.xaml`、`src/ToolsTouch.Desktop/MainWindow.xaml`、`src/ToolsTouch.Desktop/RecordGridViewModel.cs`、`src/ToolsTouch.Desktop/MainViewModel.cs`、`tests/ToolsTouch.Desktop.Tests/Program.cs`、`artifacts/implementation/P08-A/20260905T173000Z/results.json`、`artifacts/implementation/P08-A/20260905T173000Z/portable.zip`
- 迁移／协议／fixture 版本变化：无数据库迁移或协议变化；沿用 016 和 v2。
- 实际实现：抽出统一 WPF 主题资源，DataGrid 默认启用行／列虚拟化和 Recycling；Records 页修正工具栏布局并接入 CSV／XLSX 选择、预览错误列表、批次提交／取消；主窗口加入 F5 刷新；桌面回归增加共享样式和快捷键断言。刷新仍保留未保存邮件编辑上下文，导入控件只调用预览／批次服务，不调用发送服务。
- 验证命令与退出码：六个 C# 工程串行 Release 编译（均 0，0 警告／0 错误）；Core 可执行回归（0）；collector-host 测试（0，8 项）；P08-A 便携包构建（0，Node 16 项、baoyan-cli 9 项、collector-host 8 项和 Core 回归通过）；`scripts/verify-package.py`（0，14,844 个文件哈希）；包内 Host v2 握手（0，protocol 2、output schema 2、40 项 provider）；`uv lock --check`、`git diff --check` 和证据 JSON 校验（均 0）。证据见 `artifacts/implementation/P08-A/20260905T173000Z/results.json`。
- 环境边界：`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 退出 150，Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`；原生 WPF 渲染、键盘、DPI 和绑定错误运行验收仍需 Windows。
- 尚未解决的问题：P08-B 性能基准、P08-C Windows 安装／升级、P08-D U01—U11 真实流程验收尚未实现；来源范围和首批校院种子名单仍未定，不阻塞这些可独立完成的工作。
- 下一工作包与第一项动作：P08-B；建立 10 万记录合成负载，测量 SQL 分页、网格首屏数据规模、可取消导出和进程内存，并保存机器信息与 p95 证据。

## 3.35 P08-B 合成负载、分页查询和导出性能证据（2026-09-05）

- 工作包：P08-B
- 状态：Verified
- 实际变更文件：`tests/ToolsTouch.Performance.Tests/ToolsTouch.Performance.Tests.csproj`、`tests/ToolsTouch.Performance.Tests/Program.cs`、`artifacts/implementation/P08-B/20260905T180000Z/results.json`、`artifacts/implementation/P08-B/20260905T180000Z/results-repeat.json`
- 迁移／协议／fixture 版本变化：无数据库迁移或协议变化；新增 10 万条记录、20 万字段值的合成性能 fixture，数据只写入临时数据库。
- 实际实现：独立性能程序生成 100,000 条记录，执行 25 次 `score >= 50000`、50 行限制和数值排序的真实 SQL 分页，测量 p50／p95／最大耗时；同时测量全量 JSONL／ZIP bundle 导出、取消导出后最终文件不存在和进程工作集。重复运行用于确认结果方向一致。
- 验证命令与退出码：`dotnet build tests/ToolsTouch.Performance.Tests -c Release --no-restore`（0，0 警告／0 错误）；`dotnet run --project tests/ToolsTouch.Performance.Tests -c Release --no-build --no-restore -- --output .../results.json`（0）；重复基准（0）；两份证据 JSON 校验和 `git diff --check`（均 0）。首轮结果：查询 p95 199.14 ms、导出 300020 行 1740.61 ms、工作集 143.09 MiB；重复结果：查询 p95 197.72 ms、导出 1201.54 ms、工作集 143.60 MiB。证据见 `artifacts/implementation/P08-B/20260905T180000Z/results.json`。
- 环境边界：结果来自当前 Linux 容器（Unix 6.6.87.1、.NET 10.0.11），不能替代设计目标 Windows x64 机器的 p95 验收；没有测量真实 WPF 滚动／编辑、网络等待或 10 万条导师领域数据。
- 尚未解决的问题：P08-C Windows 安装／升级／卸载保留数据、P08-D U01—U11 真实流程验收尚未实现；Windows 原生基准机仍待提供。
- 下一工作包与第一项动作：P08-C；检查 `scripts/package.py`、`scripts/test-installer.py` 和 Windows 文档的生产运行时、依赖隔离、升级、数据保留与安装验证入口。

## 3.36 P08-C Windows 生产运行时、便携包清单与安装生命周期检查（2026-09-05）

- 工作包：P08-C
- 状态：Verified
- 实际变更文件：`scripts/package.py`、`scripts/verify-package.py`、`scripts/build-installer.py`、`scripts/test-installer.py`、`README.md`、`docs/WINDOWS.md`、`artifacts/implementation/P08-C/20260905T184500Z/results.json`、`artifacts/implementation/P08-C/20260905T184500Z/portable.zip`
- 迁移／协议／fixture 版本变化：无数据库迁移或应用协议变化；沿用 016 和 v2。便携包增加 Windows Python embeddable runtime、`tools-touch-collector` 及其锁定纯 Python 依赖；manifest 增加 Python runtime、collector 和官方 runtime archive SHA-256 元数据。
- 实际实现：`package.py` 下载并校验官方 Python 3.12.10 amd64 embeddable archive，安全解包并启用 `Lib/site-packages`，用 `uv build`、`uv export --locked` 和 Windows 目标纯 wheel 安装生成隔离 collector runtime；不带开发机 pnpm store、符号链接、AgentHost 源码或测试输出。`verify-package.py` 校验精确文件集合、文件哈希、Node／Python runtime contract、依赖发行名和生产包边界；安装器入口要求 Python runtime；`test-installer.py` 额外验证安装重复修复、旧数据库文件／用户工作区保留、随包 Python 依赖导入和卸载后的数据保留。README／Windows 文档已同步便携包内容与验证边界。
- 验证命令与退出码：`uv run --all-packages --locked python scripts/package.py --output artifacts/implementation/P08-C/20260905T184500Z/portable`（0，Node 16 项、baoyan-cli 9 项、collector-host 8 项、Core 回归、self-contained win-x64 发布、pnpm 生产部署、Node／Python runtime 下载校验均通过）；`uv run --all-packages --locked python scripts/verify-package.py .../portable`（0，15,285 个文件哈希和 runtime contract）；`unzip -t .../portable.zip`（0）；包内 Host 匿名握手（0，protocol 2、output schema 2、40 项 provider）；六个 C# 工程串行 Release 编译（0，0 警告／0 错误）；Core 可执行回归（0）；包内 collector site-packages 导入（0）；五个发布脚本 `py_compile`、`uv lock --check`、`git diff --check`（均 0）。`dotnet run --project tests/ToolsTouch.Desktop.Tests -c Release --no-build --no-restore` 返回 150，原因为 Linux 缺少 `Microsoft.WindowsDesktop.App 10.0.0`，按环境边界记录。
- 关键结果与证据路径：生成 153,667,569 字节、15,285 文件的便携 ZIP；清单包含 Python workspace 3.12.12、随包 runtime 3.12.10 和 9 个锁定依赖（含 collector 共 10 个发行包）；包内 Node／AgentHost 匿名 status 成功，Python 目标目录在当前 CPython 下可导入。证据见 `artifacts/implementation/P08-C/20260905T184500Z/results.json`。
- 未验证的真实条件：Windows 原生 WPF／DPI／键盘／绑定错误、DPAPI、Windows Python 可执行文件、Inno Setup 编译，以及干净用户的实际安装／升级旧库／恢复／卸载生命周期；真实 provider／Gmail／来源账号和发信仍未验收。
- 尚未解决的问题：原生安装器测试及 `test-installer.py` 的数据保留检查必须在没有 Tools Touch 注册项的原生 Windows 测试用户执行；P08-D 仍需逐项收集 U01—U11 真实流程证据。来源范围和首批校院种子名单仍未定，不阻塞当前编码和脚本验证。
- 下一工作包与第一项动作：P08-D；读取 `docs/design/05-verification.md` 的 U01—U11 矩阵，先建立不依赖真实账号的流程证据索引和明确的 NotVerified 条目，再执行当前环境可做的模拟／合成回归。

## 3.37 P08-D U01—U11 真实验收记录与缺口索引（2026-09-05）

- 工作包：P08-D
- 状态：Verified（验收记录与自动校验完成；真实流程仍为 NotVerified）
- 实际变更文件：`docs/design/real-acceptance-matrix.json`、`scripts/check-acceptance-matrix.py`、`docs/design/README.md`、`artifacts/implementation/P08-D/20260905T190000Z/results.json`、本状态文件
- 迁移／协议／fixture 版本变化：无数据库迁移、协议或业务 fixture 变化；沿用 016 和 v2。
- 实际实现：建立 U01—U11 各一项的机器可读验收矩阵，逐项链接已有可执行／合成证据，说明证据范围、所需真实证据和阻塞条件；`check-acceptance-matrix.py` 拒绝缺项、重复项、缺少证据路径或在没有明确真实证据时的 `Verified` 声明。来源范围和首批校院种子未定项在矩阵及全局缺口中显式保留。
- 验证命令与退出码：`uv run --all-packages --locked python scripts/check-acceptance-matrix.py`（0，U01—U11 恰好各一项，11 项均明确 NotVerified）；`uv run --all-packages --locked python -m py_compile scripts/check-acceptance-matrix.py`（0）；`uv run --all-packages --locked python -m json.tool docs/design/real-acceptance-matrix.json`（0）；`git diff --check`（0）。证据见 `artifacts/implementation/P08-D/20260905T190000Z/results.json`。
- 关键结果与证据路径：自动／合成证据已按 U01—U11 建立索引；真实已验证项为 0，NotVerified 项为 11；没有使用真实账号、真实来源范围或真实发信来填充结论。
- 未验证的真实条件：U01—U05 的真实 provider／Gmail／邮件流程，U06—U10 的真实学校来源／导师覆盖／推荐流程，U11 当前包的原生 Windows WPF／DPAPI／干净用户安装生命周期；具体缺口见矩阵。
- 尚未解决的问题：需要可访问的原生 Windows 测试用户、用户选择的 provider／Gmail／Brave 凭据、来源范围和首轮校院种子后，按矩阵逐项执行；真实发送仍须用户在界面明确确认或另行授权。
- 下一工作包与第一项动作：无实施工作包；若补充外部验收条件，第一项动作是读取矩阵中对应的 `required_real_evidence`，执行一项并把 `NotVerified` 改为有证据支持的状态。

## 4. 设计审查记录

2026-09-05：补充现有文件迁移路径、固定实现顺序、核心协议及验证矩阵；明确 Core 不能反向引用 Infrastructure、旧 Migration 资源定位与测试入口、未知窗口和未知发送结果规则、全量业务备份及导入边界。设计文档和 fixture 的一致性检查结果在本轮答复中报告；不计为应用测试通过。

设计检查结果：25 份 Markdown（含根 AGENTS.md）的本地链接、围栏／JSON 示例、37 个工作包顺序和 20 组验收编号通过；20 个窗口样例完成基本格式与历史不冒充事实检查，8 个评分样例的数值／排序及错误场景已定义，正常场景数值与排序复算通过。生产规则尚未实现，这些不是应用回归结果。


## 5. 既有应用维护补记（2026-09-05）

### baoyan-cli 报表脚本封装与重导出（2026-09-05）

最终排版修订：用户取消列宽自适应，要求保留边框并单行呈现。`baoyan_report.py` 已改为按字段固定列宽、固定行高、关闭换行、长内容缩小字体适应单行；保留已有边框并对空白边补细线，保持学校／活动结束时间加粗和同校底色。9项CLI测试通过（退出0），真实快照重导出到 `baoyan-cli/exports/985-211-预推免-2026-理工教育筛选-固定列宽单行/`（退出0）；学科标签及881条原记录保持不变。此前自适应列宽描述仅为历史版本。

补充：用户随后要求学科筛选标签和最终排版。新增 `baoyan-cli/baoyan_subjects.py` 名称规则，教育类例外优先，理工保留，经管财经及其他非目标学科建议排除，交叉／方向不明待核实；保留全量明细，新增优先保留／学科待核实／建议排除视图。导出列宽按中英文显示宽度估算、长字段换行与行高自适应，学校／活动结束时间列加粗。`uv run --locked python -m unittest test_baoyan test_baoyan_report`（0，9项通过）；真实快照导出命令 `./baoyan report --snapshot exports/985-211-预推免-2026-大学分组着色 --output-dir exports/985-211-预推免-2026-理工教育筛选`（0），881条日期匹配记录新增偏好标签：440条保留、325条建议排除、116条学科待核实；33条教育类保留。学科标签为名称规则初筛，并非官方专业核验；原有43条日期／院校身份待核实保持单独视图。

- 范围：用户单独要求的 CLI 报表维护，已完成；不推进或改变 P00—P08 工作包状态，无数据库或业务协议变更。
- 变更：`baoyan-cli/baoyan_report.py`、`baoyan-cli/baoyan.py`、兼容入口 `baoyan-cli/exports/query_985_211_deadlines.py`、`baoyan-cli/test_baoyan_report.py`、CLI README／依赖声明与根 `uv.lock`。新增 `baoyan report`，支持年份、报名截止下限、离线快照和新输出目录；985／211分组、全拼排序、同校活动结束时间降序、同校整行同底色；保留校区名称，校区相邻并与母校同色。
- 验证命令与退出码（cwd=`baoyan-cli`）：`uv sync --all-packages`（0）；`uv run --locked python -m unittest test_baoyan test_baoyan_report`（0，8项通过，含模拟数据的拼音、活动排序、校区聚合、整行颜色／链接、离线无网络和拒绝覆盖检查）；`./baoyan report --help`（0）；`uv lock --check`、`git diff --check`（0）。
- 真实来源快照重导出：`./baoyan report --year 2026 --deadline-from 2026-09-05 --snapshot exports/985-211-预推免-2026-截止0905起-20260905-070218 --output-dir exports/985-211-预推免-2026-大学分组着色`（0）。已用 openpyxl／CSV／JSON 断言核验（0）：881条数据与原文件按ID逐字段一致、74个原始学校／校区连续分组、72所大学同色、组内活动结束降序、三个格式顺序一致、2,426个超链接保留。
- 证据：`baoyan-cli/exports/985-211-预推免-2026-大学分组着色/`，含Excel、CSV、JSON、原始快照与查询说明。此次没有重新抓取，未进行官网、真实报名系统或Windows Excel原生验收。

当前登录维护会话完成 v0.2.2 修复：Gmail 复用、复制登录链接、组件代理、模型凭据重启恢复。14 项组件测试、C# 核心回归及 13 组 Windows 原生检查通过；真实账号端到端验证未完成，Google 验证按用户要求暂停。详见 `../ACCEPTANCE.md` 的 v0.2.2 记录与 `../releases/v0.2.2.md`。未改变数据库迁移或协议，也未开始上述 P00—P08 新架构工作包；下一新架构工作仍为 P00-A。本补记不将设计功能标记完成。

## 6. v0.3.1 安装器与 Windows 原生发布验收（2026-09-05）

- 状态：Verified（安装交付与自动验收）；本次用户明确授权 add／commit／push／Release。已有 v0.3.0 预发布及标签保留，修复版使用 v0.3.1。
- 实际变更：安装器构建增加四层程序集及完整 Node／Python 生产依赖校验；安装检查增加旧版 EXE 升级、两种注册表视图／安装范围保护及缺失注册值保护；Windows 测试直接加载编译后的 App 资源，检查全部 11 页；修复 Records 的 HasImportErrors 只读属性被 Expander 双向绑定导致的启动异常；版本、CI 原生检查、README／Windows 指南及真实验收矩阵同步。
- 数据库迁移／协议：无变化，仍为 016／v2。恢复沿用已发布 v0.2.2 的 Google 桌面客户端配置，没有导入或发布用户登录令牌。
- 实际失败与修复：原 v0.3.0 原生测试退出 1（资源字典加载错误）；修正测试入口后退出 1（HasImportErrors 双向绑定）；修复绑定后旧页面数量断言退出 1；改为核对全部 11 个页面后原生 14 组检查通过。失败证据保留在 artifacts/windows-check-v0.3.0-release、artifacts/windows-check-v0.3.0-release-r2、artifacts/windows-check-v0.3.1-prepackage。
- 验证命令与退出码：设置 Linux Node 24.16.0／pnpm 10.14.0 的 PATH 和 DOTNET_EXE 后执行 `uv run --all-packages --locked python scripts/package.py --output artifacts/ToolsTouch-win-x64-v0.3.1 --public --google-client artifacts/ToolsTouch-win-x64-v0.2.2-r2/config/google-client.json`（0，Node 16、baoyan 9、collector 8、Core 可执行回归通过）；`uv run --all-packages --locked python scripts/verify-package.py artifacts/ToolsTouch-win-x64-v0.3.1`（0，15,286 文件哈希）；`dotnet publish tests/ToolsTouch.Desktop.Tests -c Release -r win-x64 --self-contained true -o artifacts/desktop-tests-v0.3.1-r2`（0）；Windows `python scripts/build-installer.py --package artifacts/ToolsTouch-win-x64-v0.3.1.zip --version 0.3.1 --output artifacts/release-v0.3.1`（0）。Windows Python 为 3.12.10，Linux uv workspace 为 3.12.12。
- Windows 生命周期：通过 `uv run --isolated --no-project --no-config --python C:/Users/genev/AppData/Local/Programs/Python/Python312/python.exe python scripts/test-installer.py --installer artifacts/release-v0.3.1/ToolsTouch-Setup-0.3.1-win-x64.exe --test-exe artifacts/desktop-tests-v0.3.1-r2/ToolsTouch.Desktop.Tests.exe --output artifacts/installer-check-v0.3.1`（0）；另加 `--previous-installer artifacts/release-v0.2.2-r2/ToolsTouch-Setup-0.2.2-win-x64.exe`，输出改为 `artifacts/installer-upgrade-v0.3.1`（0）。两次均完成安装／逐文件校验／修复／随包 Python 导入／14 组原生回归／卸载保留用户文件；升级使用真实旧版安装 EXE。四个测试程序集和安装程序集逐字节一致；原本机 0.2.2 程序文件 SHA-256 前后相同。
- 其他验证：三种安装注册保护模拟检查（0，无注册表写入）；发布脚本 py_compile、uv lock --check、验收矩阵与 git diff --check（均 0）。正式账号和邮件均未使用。
- 可提交证据：docs/releases/v0.3.1-windows-check.json、v0.3.1-installer-check.json、v0.3.1-installer-upgrade.json；完整日志与页面截图保留在上述 artifacts 目录。Windows 11 10.0.26200／.NET 10.0.11 上执行。
- 产物：安装器 104,203,125 字节，SHA-256 `4c3493c34eb3b49e6f241fd3a0fb3292af9c0903b2fa65c7c44f864130607de1`；便携 ZIP 154,240,231 字节，SHA-256 `3fc99a9a4ad62a1631c0c419ed5f0ccd014b9f3a798c057529ea08ab598d0e24`。安装器未签名。
- 验收边界：自动原生运行和安装生命周期已验证；保留资料检查使用合成文件，不等于真实用户旧库完整业务验收。真实 Google／模型／Brave／学校来源、人工编辑导入导出恢复以及真实邮件仍未验收，U01—U11 继续 NotVerified。

- 发布后 CI 补记：GitHub Actions 33986231797 的 Node／Python 通过，Core 因缺少 uv 失败，Windows 打包因报表测试使用默认文本编码失败。已为 Core job 增加固定 uv／Python 与 locked sync，并为报表 JSON fixture 读取显式指定 UTF-8；Linux 及 Windows 的 9 项 baoyan 测试均通过（0）。此补记仅改构建环境与测试，不改已验收的安装器或应用程序集。

- Windows 全量 Core 回归补记：CI 33986495808 暴露 BackupService 目标 SQLite 连接池保留句柄、阻止暂存目录移动／清理的问题。已对备份目标／只读验证、导出快照及隔离恢复使用非池化连接；正式工作区仍默认保留连接池。新增备份／恢复后独占打开数据库的回归断言，Linux Core 已通过（0）；安装产物将重新生成，前述旧 SHA 与安装证据只对应初始候选，最终值以末尾补记及 Release 元数据为准。

- CI 33986807457：Linux／Windows 全量 Core（含备份与恢复句柄回归）、Node 与 Python 均通过；Windows publish 被 SDK 的重复框架引用提示 NU1510 阻断。保留用于跨平台发布的显式 DPAPI 10.0.9 固定版本，仅在该 PackageReference 上处理 NU1510；不放宽其他警告或更改运行库。

- CI SDK 补记：PackageReference 局部 NoWarn 未阻止 SDK 产生 NU1510（33987061031）。最终移除该无效元数据，在 Desktop 项目显式设置 RestoreEnablePackagePruning=false，维持现有显式依赖图，不全局抑制警告。依据 https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files 的 pruning 开关定义。

- SDK 最终设置修订：33987332923 关闭 pruning 后出现额外的 System.Memory 还原失败。恢复默认依赖裁剪，仅在 Desktop 项目 NoWarn 中列出已核实的重复框架引用提示 NU1510；其余警告仍视为错误，运行依赖与已验证产物保持原配置。前两项 SDK 设置为排错记录，非最终方案。

### v0.3.1 最终发布候选验收

- 最终状态：Verified。最终产物覆盖上述初始候选；应用代码构建自 `5c53b72`，后续 SDK 配置仅调整构建提示处理，保留相同运行依赖。CI 的功能代码检查提交为 `5291044`。
- 重新执行同一 package.py／build-installer.py 流程，包输出为 `artifacts/final-v0.3.1/ToolsTouch-win-x64-v0.3.1`，安装器输出为 `artifacts/release-v0.3.1-final`（均退出 0）；pnpm 使用已校验本地缓存（npm_config_offline=true）。新桌面测试发布到 `artifacts/desktop-tests-v0.3.1-final`（0）。
- `test-installer.py` 对最终 EXE 重新执行全新安装与真实 v0.2.2 EXE 升级，证据为 `artifacts/installer-check-v0.3.1-final`、`artifacts/installer-upgrade-v0.3.1-final`（均 0）；每轮包含全部 15,286 文件哈希、修复、随包 Python、14 组原生桌面检查和卸载保留资料。原有 0.2.2 程序 SHA-256 仍未改变。
- 最终 EXE：104,207,700 字节，SHA-256 `4630bdcda6c9c71cacd100b872aaea6bf670a50b8ce585982b959faff71a22c6`；最终 ZIP：154,240,617 字节，SHA-256 `cb621648587e7935f53b7fb06bb59d3ee26635bf88cce7fb3ea3c4e29d2796fd`。
- GitHub Actions [33987555038](https://github.com/winbeau/tools-touch/actions/runs/33987555038) 的 Node、Python、Core、Windows package／包哈希／原生桌面检查全部 success；可提交摘要 `docs/releases/v0.3.1-ci-check.json`。三份安装／桌面 JSON 证据已替换为最终 EXE 的实际结果。
- 最终公开交付仍为未签名测试版；真实账号、来源和人工完整业务验收边界保持不变。v0.3.0 旧标签与资产保留，v0.3.1 草稿在最终文件校验后公开。

## 7. WPF 工作台界面优化（2026-09-05）

- 状态：Verified（实现、离线回归与 Windows 原生自动检查）。本轮只做界面与验证，未提交、推送、安装或发布；真实跨显示器 DPI 切换和人工长时间使用仍未验证。
- 实际变更：MainWindow 改为紧凑外壳与七组左侧导航，保留稳定页面 ID 和所有原有命令；11 页拆为 UserControl。Theme／Workbench 资源统一浅色、青绿色、字号、间距、按钮层级、输入与表格状态；导航使用矢量图标。
- 邮件工作区整理草稿列表、正文、附件与发送操作；补充空选择、加载、失败、Sent／Unknown 只读说明。加载时临时锁定编辑，保留原确认发送与 Unknown 不重试边界。
- 记录器配置折叠、设置分为三个局部标签、导师目录的论文／评价／覆盖改用独立内容区域。所有主表保持有限布局高度与原生虚拟化，增加空数据和无结果指引。
- 交互修复：申请与记录刷新保留选择、筛选、保存视图、未保存内容及原修订号；页面切换保留实例、详情来源与滚动位置。无数据库迁移、协议变动或包依赖更新。
- 实际验证：WSL 调用 Windows 程序退出 0；Core 的 dotnet run 可执行回归退出 0；WPF self-contained publish 退出 0；最终原生测试退出 0，23 / 23 组通过，零绑定错误。54 / 54 个旧命令入口保留，当前 57 个。
- 原生覆盖：11 页及局部标签；1040×720、1360×900、1600×1000 窗口；Tab 焦点、下拉框、校验、折叠操作滚动可达；空结果、未保存编辑、邮件加载、Unknown 锁定。初始原生 DPI 为 144；隔离 HWND 注入 WM_DPICHANGED 后实际报告 96→120→144→96，每种 DPI 均检查并截图 11 页。未修改系统显示设置；消息注入不等同于人工跨显示器拖动。
- 证据：artifacts/ui-redesign/before 为旧界面，artifacts/ui-redesign/final 为最终结果，含 results.json、layout-results.json、dpi-results.json 和前后页面截图；命令与限制详见 [UI-WORKBENCH-VALIDATION.md](UI-WORKBENCH-VALIDATION.md)。所有数据及账号为隔离合成替身，没有发送真实邮件。保留未跟踪 pi/auth.json，未读取其内容。