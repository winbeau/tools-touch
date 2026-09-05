# 详细设计与编码约定

本文规定后续代码组织与共用契约。具体字段、规则、流程由[主索引](README.md)列出的七份详细文档展开；实施顺序见 P00—P08 子计划。

新对话从[接手入口](START-HERE.md)开始。文件级重构顺序以[实施映射](03-implementation-map.md)为准；下文接口轮廓由[核心契约](04-contracts.md)补齐，不能只照轮廓编写空实现后标完成。

## 1. 目标工程结构

```text
src/
  ToolsTouch.Core/                 # 领域实体、值对象、纯规则；逐步移出 I/O
    Admissions/ Faculty/ Profiles/ Recommendation/ Outreach/
  ToolsTouch.Application/          # 用例服务、端口接口、DTO、任务编排
    Abstractions/ Accounts/ Collection/ Research/ Tracking/ Queries/
  ToolsTouch.Infrastructure/       # SQLite、Pi bridge、HTTP、Gmail、文件、Python
    Persistence/Migrations/ Pi/ Google/ Collection/ Files/
  ToolsTouch.Desktop/              # WPF、ViewModel、导航、Windows 集成、DI 入口
    Features/ Controls/ Resources/ Platform/
agent-host/                       # TypeScript Pi 适配；pnpm workspace 成员
  src/ prompts/                   # 登录、会话、工具代理、输出校验、提示模板
baoyan-cli/                       # Python 来源适配；加入 uv workspace
collector-host/                   # 计划新增：机器协议、来源适配与浏览器采集
contracts/                       # 计划新增：IPC Schema、输出 Schema、契约样本
tests/
  ToolsTouch.Core.Tests/           # 保留现有可执行测试入口，逐步分文件
  ToolsTouch.Desktop.Tests/        # Windows STA／真实 WPF 检查
  fixtures/                       # 合成或明确允许保存的网页、公告、机器结果
scripts/                         # Python 开发／打包脚本，由 uv run 调用
pnpm-workspace.yaml               # 仅纳入应用自有 Node 包
pnpm-lock.yaml
pyproject.toml / uv.lock           # Python workspace 与开发环境
```

`pi/` 保持上游参考源码，不纳入应用 pnpm workspace，不修改其登录流程或包管理惯例。`collector-host/` 只在开始机器采集协议时创建，不为目录完整性提前堆空工程。

依赖方向：Core 不引用其他应用项目；Application→Core；Infrastructure→Application＋Core；Desktop→Application，并在组合根引用 Infrastructure 注册实现。Core 不依赖 WPF、SQLite 或 Pi；接口应在使用它的 Application 中定义。公共 DTO 不泄漏 `SqliteConnection`、SDK 类型或原始凭据。

目前大量 I/O 位于 Core。P00 只固定环境；P01-A 机械迁移这些文件及迁移资源到 Infrastructure，保持原有行为和必要的暂时命名空间，再逐用例抽出 Application 端口与 Core 规则。转接实现位于 Infrastructure，Core／Application 不能反向引用 Infrastructure。具体文件表见实施映射，避免循环依赖；保持既有用户 ID、草稿版本、状态与迁移版本号。

## 2. 用例服务和端口

下列是目标接口轮廓，不是声称已存在、可直接编译的代码。ID 初版继续使用现有字符串格式；通过解析和校验避免混用，后续可引入强类型包装。

```csharp
public interface IAdmissionService
{
    Task<ImportSummary> ImportAsync(ImportArtifact artifact, CancellationToken ct);
    Task<PageResult<SchoolRow>> QuerySchoolsAsync(SchoolQuery query, CancellationToken ct);
    Task<PageResult<RoundRow>> QueryRoundsAsync(RoundQuery query, CancellationToken ct);
}

public interface IWindowEvaluator
{
    WindowAssessment Evaluate(WindowInput input, DateTimeOffset asOf);
}

public interface IJobScheduler
{
    Task<string> EnqueueAsync(JobRequest request, CancellationToken ct);
    Task RequestCancelAsync(string jobId, CancellationToken ct);
    Task ResumeAsync(string jobId, CancellationToken ct);
}

public interface IRecommendationService
{
    Task<string> StartAsync(RecommendationRequest request, CancellationToken ct);
    Task<PageResult<RecommendationRow>> QueryAsync(string runId, PageRequest page, CancellationToken ct);
}

public interface IOutreachService
{
    Task<SendPreview> PrepareAsync(string draftId, int revision, CancellationToken ct);
    Task<SendOutcome> ConfirmAndSendAsync(ConfirmSendCommand command, CancellationToken ct);
}
```

其余端口：`IProfileService`、`IFacultyService`、`IResearchService`、`IApplicationCaseService`、`IWorkspaceExportService`；基础端口：`IClock`、`IAgentBridge`、`ICollectorBridge`、`IPublicDocumentFetcher`、`IPaperSearch`、`ISecretStore`、`IGmailTransport`、各领域 repository。已有同名接口先复用，避免双套实现。

事务由 Application 用例决定，Infrastructure 执行。网络与模型调用不放在 SQLite 写事务内。WPF 用例调用全程支持取消；取消与邮件已发出的事实分别处理。

## 3. 共用数据约定

| 项目 | 约定 |
| --- | --- |
| C# 命名 | 类型／成员 PascalCase；局部变量 camelCase；异步方法 Async 后缀 |
| 新跨进程协议 | 沿用现有 JSONL 的 snake_case；Schema 明确字段，不混用 camelCase |
| SQLite | 延续 PascalCase 表列名；新增迁移有递增编号与审核过的唯一约束 |
| 标识 | 应用 ID 与外部来源 ID 分离；禁止按姓名或显示标题生成唯一 ID |
| 时间 | 审计事件 UTC；招生日期保留原时区与精度，默认业务时区 Asia/Shanghai |
| 年份 | SourceYear、CycleYear、EntryYear 分开；未知使用 null |
| 数值 | 比例统一内部 0—1，UI 显示百分比；排名越小越靠前，保留分子／分母 |
| 空值 | 缺失是 null＋缺失原因；空数组是已知无项时才表示零个 |
| 外部链接 | 保存原地址与规范化地址；主页、官网公告、报名系统、论文地址分字段 |
| 写入冲突 | 使用 Revision 乐观并发；冲突提示刷新／合并，禁止静默覆盖人工编辑 |
| 可重试写入 | 稳定幂等键＋输入哈希；同键不同输入返回冲突 |
| 长文本 | 原文、抽取结果、模型分析分别存放；存哈希、解析器／模型版本 |

## 4. 查询与错误契约

`PageRequest` 包含页大小（默认 50、最大 200）、排序键白名单、方向和稳定游标；所有排序追加实体 ID 作为并列顺序。频繁变化列表可绑定 `DatasetRevision`，跨版本翻页提示刷新。浅页允许 offset，深页采用 keyset；复杂查询带取消。

`PageResult<T>` 返回 items、nextCursor、hasMore、datasetRevision；totalCount 可单独查询并缓存。SQL 参数化，FTS 输入按文本处理；搜索／LIKE 转义不能仅依赖 UI。

业务错误统一为 `Code / UserMessage / Retryable / Details`。Details 只包含允许展示的信息，不包含 token、命令行密钥或全量 CV。

| 错误 | 行为 |
| --- | --- |
| AUTH_REQUIRED／AUTH_EXPIRED | 对应 provider／Gmail 的任务等待授权，其他离线视图可读取已有缓存 |
| PROVIDER_CAPABILITY_UNSUPPORTED | 展示缺少的能力，保留用户已选模型 |
| SOURCE_RATE_LIMITED／SOURCE_UNAVAILABLE | 读操作限额重试，显示下一次计划时间 |
| SOURCE_SCHEMA_CHANGED | 拒绝该批次正式入库，保留诊断与旧数据 |
| YEAR_AMBIGUOUS／ENTITY_AMBIGUOUS | 进入待核实队列，可人工建立映射 |
| EVIDENCE_INSUFFICIENT | 返回未知项与可补充来源，不编造完成结果 |
| STALE_REVISION／IDEMPOTENCY_CONFLICT | 返回冲突，不覆盖旧版本 |
| PROTOCOL_VERSION_UNSUPPORTED | 停止启动不匹配组件，提示组件修复 |
| SEND_UNKNOWN | 锁定此次重发入口，进入核对流程 |

## 5. 详细设计分工

| 详细文档 | 主要类／实现入口 | 对应计划 |
| --- | --- | --- |
| [数据模型](details/01-data-model.md) | LocalDatabase、MigrationRunner、*Repository、SnapshotStore | P01 |
| [账号与任务](details/02-integrations-jobs.md) | ProviderCatalog、AgentBridge、CollectorBridge、JobScheduler | P02 |
| [年度窗口](details/03-admission-windows.md) | CycleResolver、WindowEvaluator、AdmissionService | P03 |
| [采集与推荐](details/04-collection-recommendation.md) | FacultyCrawler、EvidenceService、StatisticsBuilder、Ranker | P04、P05 |
| [邮件与记录](details/05-mail-tracking.md) | OutreachService、SendCoordinator、ApplicationCaseService | P06 |
| [桌面与交付](details/06-desktop-delivery.md) | 页面 ViewModel、PagedCollection、WorkspaceExporter、打包脚本 | P00、P08 |
| [表格记录器](details/07-record-workspace.md) | RecordWorkspaceService、FieldSchemaService、ViewService、导入／导出 | P01、P07 |

## 6. 编码与验证方法

先写决定业务正确性的用例：年度状态、统计分母、同名导师、任务重放、旧库升级、发送重复点击和不确定网络结果。规则尽量实现为纯函数，时间使用 `IClock` 注入。不要为简单属性、静态文案写镜像测试。

现有测试项目是 `dotnet run --project ...` 的可执行回归入口；未改造前不将 `dotnet test` 成功等同于测试执行。Node 通过 `pnpm --filter tools-touch-agent-host test`；Python 由 `uv run --all-packages --locked ...` 执行，显式安装 workspace 成员。具体命令见[验收矩阵](05-verification.md)。

增量代码必须附带与变化相符的验证证据。纯文档阶段检查链接、编号和契约一致性即可，不启动真实账号登录、采集任务或邮件发送。
