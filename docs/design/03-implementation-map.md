# 固定实现决策、文件迁移与工作包

这是 Luna 编码时的细化执行基线，与[详细设计](02-detailed-design.md)配套。原子工作包应能独立构建／验证；表里的“新增路径”是计划，不是已有代码。

## 1. 首版固定决策

| ID | 实现决定 | 目的 |
| --- | --- | --- |
| D01 | SQLite＋Microsoft.Data.Sqlite＋版本化 SQL；不引入 EF Core／第二 ORM | 沿用事务、旧库和现有测试 |
| D02 | C# 四工程；手动组合根 `Desktop/AppServices.cs`，先不增加 DI 容器／MediatR | 依赖明确，减少迁移变量 |
| D03 | 先机械迁移旧服务到 Infrastructure，再逐用例抽端口；Core 不放引用 Infrastructure 的 facade | 机械迁移与行为改变分别验收 |
| D04 | Pi 0.85.0、Node 24 基线保留；provider 由 catalog 发现，协议两侧一起到 v2 | 包环境迁移不夹带 SDK 升级 |
| D05 | 暂用单 Node host、模型并发 1；来源抓取独立队列 | 无需多个进程共享认证写锁 |
| D06 | Python 采集使用短生命周期进程和版本化 manifest；取消结束对应进程树 | 无需 FastAPI 或常驻 Python 服务 |
| D07 | Excel 先复用已锁定 openpyxl；C# 从业务快照生成流式行文件，Python 只写展示文件 | 不再引入未经评估的 C# Excel 库，也不让 Python 读写正式业务库 |
| D08 | HTML／PDF 先复用 AngleSharp／PdfPig；浏览器仅用于确实依赖 JS 的来源 | 首个窗口表可尽早交付 |
| D09 | FTS／关键词＋结构化规则→Pi 限额评估，首版不安装向量数据库 | 不把 embedding 支持当 Pi provider 既有能力 |
| D10 | 现有 WPF Observable／命令先保留；共用 DataGrid 逐步扩展 | 不同时重写 MVVM 框架和业务 |
| D11 | 旧数据库 001—003 不改；新迁移按实际最大号递增，schema 设计顺序以本文件为准 | 避免“文档建议号”与业务依赖冲突 |
| D12 | 申请历史与邮件尝试分开；全量备份包含业务修订与关联，凭据不打包 | 保留完整记录并避免恢复时重发 |

版本选择：pnpm 精确版本在 P00 验证后写入 packageManager；uv 先以已观察 0.9.17 验证 workspace 需求，不假定新版参数存在。Python 固定经 Windows 打包验证的 3.12.x 补丁版本，依赖先保留 baoyan 已锁定的三包版本；无需在设计阶段为“最新”升级所有依赖。

pnpm 导入前先声明仅包含 `agent-host` 的 workspace，使用现有 package-lock 作为迁移输入，核对 importers／传递依赖结果和冻结安装；若固定版本不能直接处理子目录锁，先在隔离暂存目录验证单包导入再迁入 workspace，不能手写一个假锁文件或悄悄升级全部依赖。[pnpm import 官方说明](https://pnpm.io/cli/import)

uv 根项目作为开发聚合入口，命令统一带 `--all-packages`，避免只同步 root 而未安装成员。`collector-host` 必须配置实际 build backend 和 src 包发现，使 `python -m tools_touch_collector` 在仓库外仍能启动；不能只建虚拟成员目录。包依赖须显式声明，不依靠根环境偶然安装的库；build backend 版本也精确固定。workspace 共享锁文件，但默认 run／sync 以根项目为目标，因此这里显式指定全成员。[uv workspace 官方说明](https://docs.astral.sh/uv/concepts/projects/workspaces/)

## 2. 现有文件到目标文件的迁移

P00 只改环境，不搬业务。P01-A 做一次**无业务语义变化**的机械搬迁：现有 Core 中含 I/O 的整份文件先移入 Infrastructure 的目标目录；新工程引用 Core／Application；Desktop 和现有测试增加 Infrastructure 引用。临时可保留原 `ToolsTouch.Core` namespace，类型只定义一份。命名空间与程序集不同名在这一步用于减少调用点变化，后续按文件迁移命名空间。

| 当前 Core 文件 | P01-A 目标 Infrastructure 路径 | 后续用例抽取 |
| --- | --- | --- |
| LocalDatabase.cs、Migrations/*.sql | Persistence/LocalDatabase.cs、Persistence/Migrations/ | P01-B MigrationRunner 与 repositories |
| ResearchStore.cs | Persistence/Legacy/ResearchStore.cs | P01／P04／P05 拆查询、写入与分析库 |
| AgentBridge.cs | Pi/AgentBridge.cs | P02 抽 IAgentBridge／IToolDispatcher 到 Application |
| AgentProxy.cs（交接复查时新增） | Pi/AgentProxy.cs | 保留账号连接代理及 loopback 绕过规则，不与 PublicWeb 的公开抓取策略混为一谈 |
| ToolDispatcher.cs | Pi/Legacy/ToolDispatcher.cs | P02 按任务白名单与端口分发 |
| DiscoveryService.cs | Legacy/DiscoveryService.cs | P02／P04／P05 迁往 Application 编排，SQL 经 repository |
| OutreachService.cs | Legacy/OutreachService.cs | P06 Application Outreach＋事务 repository |
| LibraryService.cs | Research/Legacy/LibraryService.cs | P04 资料／论文端口与文件实现 |
| PublicWeb.cs、SearchServices.cs | Web/、Research/ | 保留实际连接限制／限流，P04 经端口调用 |
| GmailAuth.cs、GmailService.cs | Google/ | P02／P06 抽 ISecretStore／IGmailAuthorization／IMailTransport |
| DiagnosticLog.cs | Diagnostics/ | 抽最小诊断端口，保持脱敏 |

文件中附带的 record／interface 初次可整体保留在 Infrastructure；相关用例重构时再抽到 Core／Application，禁止复制两份同名公共类型。**Application 永远不引用 Infrastructure；旧具体服务留在 Infra 并由组合根使用，直到端口替换完成。** 所谓兼容转接是 Infrastructure 实现 Application 接口，不是 Core 调用 Infrastructure。

SQLite／AngleSharp／PdfPig／MimeKit 包及 EmbeddedResource 项随实现移动到 Infrastructure；Core 只留下真正使用的纯领域代码。`typeof(LocalDatabase).Assembly` 随类迁移后必须能找到新程序集的 `.Migrations.` 资源；旧 SQL 文件内容和 SchemaVersion 编号保持不变。迁移前后用同一 fixture 比较结果。

同步更新 `scripts/verify-package.py`、`build-installer.py`、`test-installer.py` 的必需 DLL／哈希检查，增加 Application／Infrastructure 程序集；现有测试的无列名 `INSERT INTO Professor VALUES(...)` 在增列前改成显式列名。这些不是业务需求变化，但不改会让新增字段或新程序集导致伪回归。

交接复查期间还出现 `agent-host/src/runtime.ts`，统一创建账号 ModelRuntime 并恢复本地凭据可用性；P02 复用这个工厂，不退回旧的启动构造逻辑。账户／Gmail／UI 有其他在途修改，P00-A 以当时实际文件为准核对本表。此清单是迁移职责表，不是允许将工作区回滚到设计开始时的版本。

## 3. 新迁移依赖顺序

迁移按功能引入，表内“序号”是当前只有 001—003 时的建议；实际开工以库中最高号顺延。

| 顺序 | 工作包 | 新 schema |
| --- | --- | --- |
| 004 | P01-B | WorkspaceMeta／School／Department／AdmissionProgram／AdmissionRound／EntityAlias／ExternalIdentity／EntityResolution／Appointment；Appointment 暂无证据 FK，WindowObservation 先不建 |
| 005 | P01-C | Artifact／SourceSnapshot／EvidenceClaim／ClaimSupport／SourceCheck／EvidenceConflict；再添加 Appointment 证据列、WindowObservation／HistoricalProjection／ObservationOverride；Profile 扩展与 PreferenceRevision |
| 006 | P02-B | JobStage／JobAttempt／CrawlBatch／CrawlItem 和 AgentRun 扩展 |
| 007 | P04-B／C | RecruitmentClaim／PaperIdentifier／PaperReadSegment／ProfessorEvaluation／AnalysisArtifact |
| 008 | P05-A／B | DepartmentAssessment／AdmissionCaseSample／CohortStatistic／RecommendationRun／Item |
| 009 | P06-B／D | SendConfirmation／SendAttempt／ApplicationCase／CaseOutreach／ApplicationEvent／Reminder 与邮件关联扩展 |
| 010 | P07-A | Collection／RecordRef／FieldDefinition／Choice／Value／Relation／ViewDefinition／RecordChange／ImportBatch |

P03 的早期 RecordGrid 直接消费类型化分页 DTO，视图配置暂存应用设置；P07 将视图导入 ViewDefinition，保留视图 ID。P01 定义通用记录器契约但不创建 P07 全部 schema，防止首个功能被通用化工程拖住。

迁移 005 涉及旧 SourceDocument 内容落文件：停止业务写入后先读取旧行、在事务外准备哈希文件；MigrationRunner 执行 schema SQL 后，在同一个迁移事务中写 Artifact／SourceSnapshot 回填与 SchemaVersion。SQL 与回填都成功才能标迁移完成；失败残留的无引用文件延后清理。不能把 schema 已完成却遗漏正文回填的库当作可用新库。

## 4. 工作包及完成输出

每包完成后更新[实施状态](IMPLEMENTATION-STATE.md)。测试名是应实现的有意义测试组，不是当前已有测试。

| 工作包 | 文件／核心输出 | 完成条件与重点验证 |
| --- | --- | --- |
| P00-A | 基线报告、实际工具路径、依赖版本、git 状态 | 明确旧测试能否运行；无缺配置的静默 skip |
| P00-B | 根 Node workspace、锁文件、agent-host 声明 | 冻结安装、现有 Node 测试和真实匿名 Pi 导入通过 |
| P00-C | 根 uv workspace、baoyan pyproject、锁文件 | 现有 CLI unittest 可由根 uv 环境运行；不访问正式源 |
| P00-D | package／verify 脚本、docs、`.github/workflows/ci.yml` | CI 独立于现有 Pages 发布，开发和打包均用 pnpm／uv |
| P01-A | 四工程、机械迁移、AppServices、旧测试引用 | 旧行为回归／WPF 编译；无循环 ProjectReference |
| P01-B | 基础实体、MigrationRunner、WorkspaceMeta、分页 DTO | 同名不同校、稳定排序、事务失败、旧库升级 |
| P01-C | 来源快照、ArtifactStore、证据／Override／年份字段 | 来源变化不覆盖旧内容；用户更正可撤回 |
| P01-D | BackupService、旧库 fixture、MigrationTests | WAL 一致性备份、资源定位、重开、旧 Sent／Unknown 保留 |
| P02-A | contracts/agent-v2、Node/C# 编解码、IToolDispatcher | 字节级帧限额、ACK 顺序、目标取消、旧 attempt 拒绝 |
| P02-B | JobScheduler、JobAttempt、JobStage、队列 repository | 接受原子化、lease／恢复、写工具重放、预算共享 |
| P02-C | Accounts ViewModel／catalog／Google 端口 | OAuth／Key 实际能力、取消重登、离线既有库、正确 scope |
| P02-D | CollectorBridge、Python CLI 机器入口、manifest 校验 | 路径／大小／数量、异常退出、半文件和取消 |
| P03-A | Core/Admissions/CycleResolver.cs、WindowEvaluator.cs | [窗口样例](fixtures/window-cases.json)逐条通过，含跨年／边界 |
| P03-B | baoyan 原始机器导出、schemaVersion=1 | 时间与官网／报名地址完整保留，缺年份保留、分页校验 |
| P03-C | AdmissionImportService、别名／批次匹配、暂存发布 | 重复导入幂等、歧义不误合并、事实与预测分开 |
| P03-D | SchoolOverview／SchoolDetail ViewModel＋RecordGrid | 每校一行、distinct 学院计数、批次子表、C# 基础 CSV 导出；P07 完成 Excel／全量包 |
| P04-A | SourceAdapter／FacultyDirectoryCrawler／覆盖清单 | 分页／分类遍历可恢复，未知分母不宣称 100% |
| P04-B | FacultyResolver、Appointment／Recruitment claims | 同人多任职、同名消歧、本年学位、公开邮箱出处 |
| P04-C | ResearchService、Paper evidence、评价／快照导入 | 摘要／部分页／全文、论文归属、第三方陈述分别标记 |
| P04-D | FacultyList／Detail、覆盖与失败 UI | 来源失败仍可查旧数据，研究入口连接持久化 |
| P05-A | Profile confirmation、Preferences、StatisticsBuilder | 排名／GPA／论文口径、分母、未知与零、阶段分组 |
| P05-B | EligibilityEvaluator、Ranker、RecommendationRepository | [评分样例](fixtures/ranking-cases.json)、已知与未知、版本快照 |
| P05-C | Pi 语义批评估／研究提示与输出校验 | 对象范围、分数 rubric、引用、预算、候选公平比较 |
| P05-D | Recommendation Center／Compare ViewModel | 可重现两级结果、分项／来源／缺失／范围可见 |
| P06-A | DraftRequest／DraftService、原版本兼容 | 重试不覆盖人工稿、引用资料一致、新生成新版本 |
| P06-B | SendCoordinator、Confirmation、immutable MIME artifact | TOCTOU、双击、确认过期、消费事务、仅一次传输 |
| P06-C | Gmail 核对／回复和原账号记录 | Unknown 不重发、正确 Message-ID／ThreadId、断线恢复 |
| P06-D | ApplicationCaseService、材料／备注／提醒 UI | 官网投递与邮件联系分开、阶段变更有事件 |
| P07-A | 记录器 schema、SystemCollectionAdapter、字段权限 | 系统实体不重复存一份业务值、关系引用合法 |
| P07-B | RecordGrid 编辑器、FieldSchemaService、BulkEdit | 类型转换、CAS、批量分段、撤销、受保护字段 |
| P07-C | QueryCompiler、FilterBuilder、ViewService | SQL 白名单、组合过滤、并列分页、视图重启恢复 |
| P07-D | ExportSnapshotService、JSONL bundle、Python XLSX、Restore | 全量不受分页过滤影响、含历史／关系、自定义值、原子恢复 |
| P07-E | ImportPreview／ImportBatch／field mapping | 追加／显式键更新、逐行错误、重复重放、禁止发送副作用 |
| P08-A | 全局导航、样式、键盘、详情上下文 | 原生 WPF 无绑定错误，旧未保存编辑不丢失 |
| P08-B | 合成大数据与性能证据 | 查询／导出内存／取消达目标，测量机器信息一并保存 |
| P08-C | Windows 生产环境／安装器／升级与恢复 | 无开发机 store／venv 依赖，完整清单与独立用户验证 |
| P08-D | U01—U11 真实流程验收记录 | 缺少条件的项目明确未验收，发布按当时授权 |

## 5. 建议目标文件名

新增业务采用一致路径：`Core/<Domain>/*` 放纯规则；`Application/<Feature>/*Service.cs` 放用例；`Application/Abstractions/*` 放端口；`Infrastructure/Persistence/<Feature>/*Repository.cs` 放 SQL；`Desktop/Features/<Feature>/*View.xaml`＋`*ViewModel.cs` 放交互。已有同职责文件重构复用，不为满足文件名再写一套服务。

跨进程契约源文件放 `contracts/agent-v2.schema.json`、`collector-manifest-v1.schema.json`、`analysis-v1.schema.json`、`recommendation-v1.schema.json`；TypeScript 校验沿用 typebox，C# 通过 System.Text.Json 严格 DTO／显式校验。共享 golden fixtures 必须分别跑两端，不能以 TS 校验通过推断 C# 已校验全部字段。

Python 包：`collector-host/pyproject.toml`、`src/tools_touch_collector/__main__.py`、`adapters/baoyan.py`、`xlsx.py`；baoyan 原 CLI 的核心函数逐步提为可导入模块，保留旧 `baoyan.py` 命令包装。uv 根环境为 workspace 成员和脚本提供统一解析；增加成员时更新根 uv.lock。

## 6. 不应由 Luna 临时猜测的规则

不能自行改为网页前端、把 Pi 换另一套 agent、把所有学校建成物理表、用旧日期直接判过期、把缺样本算零、按姓名合并导师、把模型分数当录取概率、跳过新用户表格功能、把所有权限交给通用模型 write、把 Unknown 改为 Failed 后自动发送、为打包通过删掉 Python 采集功能。

实际源码／外部 API 与设计不符时，应调整最小适配并记录差异；涉及用户产品选择的变化单独确认。实现细节例如私有方法命名、拆文件大小由执行者判断，无需逐项询问用户。
