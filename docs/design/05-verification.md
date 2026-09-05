# 编码验收、命令与证据

本文件指导后续实施验证。本设计会话仅检查文档与 fixture，自身没有完成下列应用验收。

## 1. 命令入口与环境

先在 P00-A 检查真实工具位置。`dotnet` 不在 PATH 时读取 global.json，选择当前平台匹配的 SDK；脚本可用现有 DOTNET_EXE 配置。不要把不可执行的 Windows pnpm wrapper 当成 Linux pnpm 已就绪，也不要硬编码设计会话的机器路径进仓库。

| 阶段 | 命令／入口 | 执行前提 |
| --- | --- | --- |
| 旧 Node 基线 | `pnpm --dir agent-host exec tsc`，再 `node --test agent-host/dist/*.test.js` | 已有 node_modules 可用；否则在 P00-B 完成锁定环境后验证，不重新建立长期 npm 流程 |
| 新 Node 环境 | `pnpm install --frozen-lockfile --ignore-scripts` | P00-B 已生成根 workspace／lock |
| AgentHost 检查 | `pnpm --filter tools-touch-agent-host check`、`pnpm --filter tools-touch-agent-host test` | 根 workspace 已可用；test 会编译实际 JS |
| C# 旧及新增核心组 | `dotnet run --project tests/ToolsTouch.Core.Tests` | 实际 SDK 可用、先构建 AgentHost；现有握手测试会调用 Node |
| WPF 构建 | `dotnet build src/ToolsTouch.Desktop/ToolsTouch.Desktop.csproj -c Release` | Windows 或启用 Windows targeting 的交叉构建；构建不代表 UI 已运行 |
| Python 环境 | `uv sync --all-packages --locked` | P00-C 已生成 pyproject／uv.lock |
| 既有 CLI 测试 | `uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'` | 根环境有 httpx／openpyxl；不需要登录 |
| 新 Collector 测试 | `uv run --all-packages --locked python -m unittest discover -s collector-host/tests -p 'test_*.py'` | 对应模块与测试已实现 |
| 新打包 | `uv run --all-packages --locked python scripts/package.py --output artifacts/ToolsTouch-implementation-candidate-01` | 脚本已迁移、目标路径不存在、平台运行时可获取 |
| Windows 原生检查 | `uv run --all-packages --locked python scripts/test-windows.py --package artifacts/ToolsTouch-implementation-candidate-01 --output artifacts/implementation/windows-01` | 在原生 Windows 执行且目录未存在 |

Python 新测试先沿用 stdlib unittest，避免为统一工具再迁移全部旧测试。若执行者有明确必要引入 pytest，可记录原因后修改锁和入口，不能写了测试文件却忘记被 runner 发现。C# 新测试组须在现有 Program.cs 显式调用；只新增类不会自动执行。

CI 新建 `ci.yml`，与当前 `pages.yml` 分开。Linux job 做 Node／Python／跨平台 C#；Windows job 做 WPF 原生与生产包依赖。真实账号测试不得由自动 CI 猜测环境密钥并触发，使用显式 opt-in 开关和独立验收。

## 2. 必须有的业务测试组

| ID | 测试组及关键断言 | 完成阶段 |
| --- | --- | --- |
| T01 | LegacyMigrationTests：原库三迁移、旧 ID／Sent／Unknown／资料／任务不丢；二次应用幂等 | P01 |
| T02 | MigrationAssemblyTests：新程序集加载完整 SQL，缺迁移资源时失败而非空成功 | P01 |
| T03 | WindowEvaluatorTests：[窗口 fixture](fixtures/window-cases.json)，比较所有状态与规范化边界 | P03-A |
| T04 | CycleResolverTests：EntryYear≠CycleYear、SourceYear=0、抓取日期不能补招生年 | P03-A |
| T05 | SchoolAggregationTests：同学院多轮次 distinct 计数；未知／空集合不能全结束 | P03-D |
| T06 | AgentProtocolTests：拆 UTF-8 帧、超限未终止行、多行大块、未知字段、错误版本 | P02-A |
| T07 | JobRecoveryTests：结果与检查点事务失败同回滚；旧 attempt 和重复响应不重复提交 | P02-B |
| T08 | CollectorManifestTests：行数／hash／路径／外部 ID、非零退出、半文件、重复分页、source year 未知 | P02-D／P03-B |
| T09 | FacultyResolutionTests：同名异人、同人多院、旧招生、本年学位、邮箱出处 | P04-B |
| T10 | ResearchEvidenceTests：论文消歧、摘要／页范围、扫描不可读、评价冲突、不把网页指令执行 | P04-C |
| T11 | CohortStatisticsTests：未知不是零、985 分母、去重、同阶段／年份／口径、低样本 | P05-A |
| T12 | RankingTests：[评分 fixture](fixtures/ranking-cases.json)、三态资格、权重非法、稳定次序、快照复现 | P05-B |
| T13 | SendConfirmationTests：内容／附件 TOCTOU、过期／二次消费、双击、账号变更、immutable MIME | P06-B |
| T14 | SendRecoveryTests：提交后崩溃、HTTP 结果未知、响应落库失败、核对未查到不重发 | P06-C |
| T15 | ApplicationCaseTests：邮件 Sent 不等于官网 Submitted，阶段事件保留 | P06-D |
| T16 | RecordWorkspaceTests：字段类型／关系、系统字段权限、Revision、粘贴／批量／撤销 | P07-B |
| T17 | ViewQueryTests：过滤 AST 限额、SQL 白名单、空值排序、跨页并列、视图恢复 | P07-C |
| T18 | WorkspaceRoundTripTests：全量数据、自定义字段／关系／历史／附件；恢复 Unknown 不自动运行 | P07-D |
| T19 | WorkbookExportTests：跨页全量、校名碰撞、长正文、公式前缀、取消／原子发布 | P07-D |
| T20 | ImportTests：映射预览、失败行、幂等、关系、保护发送状态、丢附件明确标 Missing | P07-E |

不需要为每个 DTO getter、固定文案或 private helper 单独写镜像测试。上述用例关心业务结果和失败边界，可合并到少量测试文件，关键是可执行且断言真实。

## 3. 原生与真实验收分开

Windows 原生检查包含 STA、实际 DataGrid、选择／滚动／编辑、DPI、绑定错误、DPAPI、Pi／Python 子进程退出、休眠恢复、长路径、离线启动与备份恢复。截图仅用于 UI 证据，不能证明登录成功或采集准确。

真实验收逐项记录：所选 provider 与模型一次工具流程；Gmail 实际授权与恢复；首轮各校公开目录／公告抽查；导师论文／招生及推荐证据核对；用户对具体邮件明确确认后发送并核对回复。欠缺任何真实条件标 NotVerified，不能用 fixture 名称“live”冒充真实完成。

## 4. 证据格式与继续条件

每包目录为 `artifacts/implementation/Pxx-X/<唯一运行标识>/`，保存 results.json 和必要日志／截图／合成数据库。results 字段：work_package、timestamp_utc、environment、command、exit_code、checks、evidence_files、not_verified。命令行和环境只记录白名单版本／路径，不转储完整 env。

工作包标 Verified 的条件：实际代码接入、必要检查执行且通过、文档状态更新；不能只完成 schema 文件。若 Windows 或真实账号缺失，离线工作包可 Verified，但 P08 相应验收保持未完成；整体项目不因此宣布完成。

正常失败先修复与本次变更相关问题并重跑该组。必要检查通过后进入下一个包，不为获取更多“绿灯”反复运行无关全套测试。有新的失败、迁移或跨边界变化才扩大范围。

## 5. 设计 fixture 的使用说明

JSON fixture 是合成输入／期望，字段名 snake_case，对应[核心契约](04-contracts.md)。应用测试应通过实际生产函数求结果，再与 expected 比较；不在测试里照抄完整算法或直接读取 expected 作为输出。

窗口 fixture 的 current／historical 输入是简化的测试适配模型，映射到 WindowInput；Date 的 end 是源给出的截止日，expected.end_exclusive 才是归一边界。评分 fixture 用 decimal 计算，误差容限 1e-9。适配器为已知分项附加 `fixture:<case_id>:<key>` 的合成证据 ID，证据存在性由另外的 repository 集成测试验证；缺失分项的 unknown_reason 使用 fixture_missing。新增 bug 时把最小复现输入补入 fixture，并让 C#／相关协议端共享它。
