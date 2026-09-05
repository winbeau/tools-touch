# 核心编码契约与不可省略的边界

这份文件把详细设计中的关键约定收敛为首版实现契约。它优先细化模糊接口，但不改变用户范围。Schema 在对应 P02／P03 工作包落入根 `contracts/`；这里及 fixtures 属于设计资产，不是应用实现。

## 1. 数据版本与原子发布

`WorkspaceMeta` 固定一行：Id=1、DataRevision（64 位整数）、SchemaVersion、WorkspaceId。`DataRevision` 在一次对查询可见的领域／自定义记录发布事务中增加 1，与实体、RecordRef 修订和更改事件同时提交。逐 token 进度、认证事件、心跳不增加 DataRevision；否则正在运行的任务会让所有列表永远无法翻页。

RecommendationRun 的输入从同一个 SQLite 读事务导出候选特征、资料／需求版本、证据引用；写入不可变候选 artifact 后用写事务创建 run，记录读到的 DataRevision。模型阶段发生在这两个事务之后。外部文件先写 staging、计算哈希、原子改名为最终 hash 路径，再提交引用；事务失败留下的无引用文件可延后清理。

导出使用 SQLite backup snapshot 和由快照列出的 artifact 清单；导出／恢复期间为这些文件保留 lease，禁止 GC 删除。数据包内没有附件时保留 Artifact 元数据和 missingFiles 清单，恢复显示 Missing，不重新赋予假的文件路径。

## 2. 日期输入与结果

固定类型：

```csharp
public enum ActualWindowState { NotStarted, Open, Closed, Cancelled, Unknown, Conflict }
public enum ActualWindowBasis { CurrentOfficial, CurrentAggregator, UserVerified, NoCurrentEvidence }
public enum EstimatedWindowPhase { NotAvailable, Upcoming, WithinEstimatedRange, EstimatePassed, Unprojectable }
public enum DatePrecision { Date, Instant, Approximate, Unknown }

public sealed record WindowInput(
    int TargetCycleYear,
    string TimeZoneId,
    CurrentWindow? Current,
    HistoricalWindow? Historical);

public sealed record WindowAssessment(
    int TargetCycleYear,
    ActualWindowState ActualState,
    ActualWindowBasis ActualBasis,
    EstimatedWindowPhase EstimatedPhase,
    DateTimeOffset? Start,
    DateTimeOffset? EndExclusive,
    DateTimeOffset? ProjectedStart,
    DateTimeOffset? ProjectedEndExclusive,
    string[] Reasons,
    string[] EvidenceIds,
    DateTimeOffset AsOf,
    string RuleVersion);
```

CurrentWindow 包含 CycleYear、Basis、RegistrationRange、ExplicitStatus?、StatusObservedAt?、StatusValidUntil?、EvidenceIds、HasConflict；HistoricalWindow 包含已核实 CycleYear、RegistrationRange、对应同一项目／批次的 EvidenceIds。`ExplicitStatus` 只允许 Unknown／Open／Closed／Cancelled，不允许模型以自由文本绕过日期检查。

Range 两个边界各保存 Raw、Precision、LocalDate?、Instant?、UncertaintyLower?／Upper?。Date 和 Instant 互斥；日级结束解析为次日 00:00，Instant 截止按结束边界排除。原文明确包含某分钟时，按原文精度转换成相应 endExclusive 并记录解释；不能默认加一毫秒。

评估顺序固定：输入校验→本年同范围有效用户更正→本年已解决版本的官方／聚合观察→状态与日期冲突检查→实际状态→仅无本年适用信息时历史投影。显式取消／关闭须有仍适用的声明；“现已开放”超过 StatusValidUntil 或已有过期截止时不能覆盖日期变成无限开放。状态声明默认有效 24 小时（产品初值）后待复核；明确日期仍独立适用。

未知精度日期不生成精确倒计时。本年无边界但已发布相关公告时，仍可显示去年日期供阅读，EstimatedPhase=NotAvailable，不悄悄用去年预测填补当年未知截止。

人工更正使用 `ObservationOverride(Id, SubjectId, FieldKey, ValueJson, BasedOnClaimId?, Revision, Reason, CreatedAt, RevokedAt?)`，有效更正在自身指定范围内优先，展示 UserVerified。新官网与更正不同则提示冲突，不静默盖掉人工值；用户撤销更正恢复使用来源观察。

学校行同时返回 `OpenDepartmentCount`（distinct 学院）、`OpenRoundCount`（轮次）、`UpcomingDepartmentCount`、`ForecastDepartmentCount`、`UnknownRoundCount`、`ClosedRoundCount`，列头明确单位。一个学院可同时有开放和待开放批次；计数列不强求互斥相加为学院总数。只有至少一个已收录轮次、所有适用轮次均关闭／取消且无未知，才显示“已收录批次均结束”；空集合不成立。

## 3. JSONL v2 命令／事件

共用协议字段命名为 snake_case，未知字段拒绝；字符串 UTF-8；一个 frame 不含终止换行最大 1,048,576 字节。编解码器须在读取完整行之前累积字节并检查限额，不能用无限 ReadLineAsync 再检查长度。大 buffer 包含很多小帧时逐帧检查，不能因单次 read 总字节数大而拒绝合法帧。

所有请求有 `protocol_version=2,type,id`。运行范围使用 `run_id,stage_key,attempt_id` 三元组。`response(id,ok,data?|error?)` 是命令接受／拒绝，`run_finished` 是执行终态；两者不同。

| 命令 | 除共用字段外的必需／可选字段 | 语义 |
| --- | --- | --- |
| status | provider? | 返回 catalog、busy、active context、host／pi／output 版本；无凭据 |
| login | provider、auth_type；method?、secret? | OAuth／api_key，接受后记录 auth_request_id=id；secret 不持久化 |
| auth_reply | auth_request_id、prompt_id、value | 必须匹配活动授权及 prompt |
| cancel_auth | auth_request_id | 不影响研究任务；已结束目标返回 already_finished |
| run | run_id、stage_key、attempt_id、provider、model、policy_id、input_context、limits；input_artifact_id? | policy_id 决定工具与输出 schema；input_context 受任务 schema 约束 |
| cancel_run | run_id、stage_key、attempt_id | 只取消精确目标；旧目标不能取消新任务 |
| tool_result | run_id、stage_key、attempt_id、tool_call_id、result | result={ok:true,data} 或 {ok:false,error}；只能解决匹配调用 |

| 事件 | 字段 | 顺序／约束 |
| --- | --- | --- |
| ready | protocol_version、host_version、pi_version、supported_policies、provider_catalog | 首个 Host 消息；协议不兼容停止该组件 |
| auth_url／auth_prompt／auth_device_code | auth_request_id、provider、对应 prompt／URL／code | 只路由活动授权 UI，不存入任务事件库 |
| auth_finished | auth_request_id、provider、ok、error? | 清理授权状态后发出，之后可立即再次登录 |
| run_started | 运行三元组、sequence | 只有 run response(ok=true) 已发出后可发 |
| tool_request | 运行三元组、sequence、tool_call_id、tool、arguments | C# 必须匹配任务上下文与 policy 工具集 |
| progress | 运行三元组、sequence、stage、counts? | 只含业务摘要，不包含 provider 凭据或任意 SDK 对象 |
| run_finished | 运行三元组、sequence、state、output?、error? | 清理 session、pending tools、busy 后发出；每 attempt 最多一次 |

必须先预留运行槽再响应接受，防止两条 run 同时被接受。`run_finished` 中 state=Completed 只表示 Host 输出通过本端 schema；C# 还要验证引用／目标／事务提交后才能把业务 job 记 Completed。run 终态前完成的写工具结果可能已经持久化，恢复时依幂等键读取。

bridge 不再断言“所有任务恰好八个工具”；握手校验 supported_policies 的版本，创建 session 时只提供该 policy 工具。原有“精确八工具”测试改为各 policy 的精确白名单测试；禁止把断言直接删掉后允许任意工具。

错误 envelope 固定 `code,message,retryable,details?`；C# 映射用户文案，details 允许键白名单。Unknown command／missing id 作为协议错误计数并在阈值后断开，错误响应本身不得回显 secret。

## 4. 任务实例、幂等与重放

增加 `JobAttempt(Id,RunId,StageKey,AttemptNumber,OwnerSession,State,StartedAt,FinishedAt?,LastSequence)`，stage 重试生成新 AttemptId。`AgentRunEvent` 增加 AttemptId、AttemptSequence，设置 `(AttemptId,AttemptSequence)` 唯一；现有业务 Sequence 仍在 Run 内递增。

只读请求可重试；业务写幂等键为 `runId:stageKey:logicalOperation:targetId`，并保存 InputHash。不能把 AttemptId 或模型随机 tool_call_id 当稳定业务键；否则恢复会重复创建草稿／分析。tool_call_id 只承担某次工具往返关联。相同稳定键不同输入返回 IDEMPOTENCY_CONFLICT，不覆盖人工编辑。

提交阶段采用一次事务：校验当前 owner／attempt→写结果与关联→更新 JobStage 完成和 Checkpoint→添加业务事件→提交。失去 owner 的迟到响应拒绝，即使输出合法。C# 确认提交后释放任务槽，队列继续下一项。

`max_attempts` 表示总尝试次数，默认 3（首次＋最多 2 次重试），不使用“重试三次”同时表示总共三次或四次。公开 HTTP 和阶段模型修正分别记 budget，子任务共享父预算。登录需要用户操作时 WaitingForAuth，不自动反复打开浏览器。

## 5. Python 机器产物

Collector 一次进程处理一个 command，参数使用 ArgumentList：`python -m tools_touch_collector export-baoyan --request <request.json> --output <empty-staging-dir>`。request 内含 source、scope、limits，来源 token 不放命令行。成功输出 manifest.json 为最后一步，退出 0；进度在 stderr 以脱敏结构记录，stdout 只输出一个 manifest 路径确认 JSON。

manifest 使用 snake_case：schema_version、source、adapter_version、scope、fetched_at、complete、counts、files。files 中每项 path、sha256、byte_length、kind；记录放 records.jsonl，原始响应放 raw/ 下。`counts.scanned` 是实际扫描全部来源记录，`expected` 是来源报告的总数或 null，`selected` 必须等于 records.jsonl 行数；complete 只表示声明范围，不能代表全国／全校官网全部信息。

每行包含 external_id、school、department?、source_year?、kind、title、registration_start_raw?／end_raw?、event_start_raw?／end_raw?、official_url?、application_url?、source_url?、raw_ref。raw_ref 是包内文件及可选 JSON Pointer，不是假正式 Artifact ID。

校验相对路径、禁止 `..`／绝对路径／符号链接／重复规范化路径，文件 hash／字节数／行数／来源身份一致；原始捕获时刻不靠文件 mtime 推断。单包和解压大小使用应用配置限额，错误结果留诊断，旧已发布批次不改。人用 baoyan 中文导出仅作为旧格式导入，不冒充这个新协议。

同一 Python 包提供 `export-xlsx --request <request.json> --output <staging-dir>`，request 仅包含 C# 导出的行文件、字段类型／sheet 映射和格式参数；此命令禁止来源网络访问。Python 用 openpyxl 的 write_only 流式写入，返回文件 manifest；C# 核对每 sheet 行数后发布。它不接收正式 SQLite 路径、凭据或任意 SQL。baoyan、XLSX 命令的依赖在 collector 包显式声明，避免仅在开发聚合环境能运行。

## 6. 推荐输入和评分契约

Ranker 接收 fixed features、Eligibility、Components、WeightProfile 和 AlgorithmVersion。每个分项字段：key、weight、score?、evidence_ids、unknown_reason?、evaluation_level。权重非负且总和为 1（十进制误差容限 1e-9）；score 必须在 [0,1]；已知分数必须有对应来源或可追溯的用户事实／规则依据。

输出 known_score?、coverage、lower、upper、evidence_bucket、rank?、display_order。三态资格顺序 Eligible→NeedsVerification；Ineligible 单列不参与推荐 rank。每个资格组再按 Sufficient（coverage≥0.6）→Insufficient 分组，再按 lower DESC、known_score DESC（null 最后）、source_quality DESC、target_id ASC。UI 标明分组；display_order 跨组，rank 是组内顺位，不能把不同研究层级混排行。

模型输出分数只是 rubric 观察，C# 重算最终值；没有分项时返回 score=null、coverage=0、lower=0、upper=100、Insufficient。缺失维度的区间不是“真实值零”。具体黄金值见[评分样例](fixtures/ranking-cases.json)。

## 7. 表格写入与系统实体

`UpdateCellCommand` 必需 record_id、field_id、expected_record_revision、expected_field_definition_revision、value、command_id；系统领域映射还带 expected_entity_revision。一次事务验证字段所属集合／类型／权限→CAS 更新领域或自定义值→RecordRef.Revision+1→RecordChange→DataRevision+1。禁止发生“领域值改了、记录器还显示旧 revision”。

FieldValue.Type 从 FieldDefinition 读取；同一次写入仅允许一个对应 typed slot。单选存 Choice ID，多选存去重且稳定排序的 Choice ID JSON 数组，关系只存 RecordRelation，不重复写 JsonValue。自定义数字以规范化十进制文本保真＋数值排序投影；筛选和汇总对精度敏感时按 decimal 规则重核，不能只依赖浮点相等。

单用户也可能同时有导入／爬取／UI，因此 CAS 必须在数据库事务内生效。批量编辑先固定 RecordIds 与 revisions；每批默认 100、上限 1,000 行，批内全校验通过才提交，跨批可部分完成并报告。稳定 CommandId＋batchIndex 使重试不重复添加事件。

系统来源字段更正走 ObservationOverride；系统状态 Editable=false。任意关系不能表达不合法的内置领域事实，例如自定义“相关导师”关系不会自动把该导师任职学院改掉。

Filter AST 最多 5 层、100 个节点，白名单字段和类型运算；关系过滤深度首版 1 层。查询缓存键包含 CollectionId／ViewRevision／DataRevision／Sort／Filter／AsOf；日期状态分页绑定同一个 AsOf，跨午夜刷新时启动新查询。

## 8. 导出与恢复契约

`ExportRequest` 固定 scope、format、view_id?、collection_id?、school_ids?、include_history、include_attachments、output_path。scope=WorkspaceAll 不读取当前 UI 过滤／勾选；include_history 默认 true。格式为 csv／xlsx／business_bundle；完整备份模式强制 history／引用文件完整，并在 manifest 中标 FullBackup=true。

JSONL业务包按完整实体／关系／修订导出，不导出原始 SQLite SQL 语句；manifest 用 snake_case，字段为 export_version、schema_version、workspace_id、data_revision、created_at、full_backup、tables、files、excluded、missing_files。tables 项为 name、path、row_count；files 为 path、sha256、byte_length、kind。可读 XLSX 超长正文使用伴随目录或统一 ZIP（workbook.xlsx＋texts/），保存对用户可见的路径索引。

Restore v1 先支持“恢复到新隔离工作区”和“关闭任务、备份后完整替换”，二者保留 ID；合并导入先仅支持自定义集合／显式系统字段映射，完整业务包跨工作区自动合并后续实现。这样首版拥有完整备份能力，又不会把未实现的全库冲突合并假装支持。

恢复顺序依据 FK 依赖拓扑，合法自引用先插基础行后补关系；所有导入数据验证后事务提交。恢复出的未决发送必须为 Unknown，需要用户核对；恢复任务默认暂停，用户选择继续后才调用外部服务。引用的凭据不恢复，历史 SenderAccount 原样保留，重新连接不能改写历史归属。

## 9. 发送快照最后约束

发送准备阶段生成含固定 RFC Message-ID 的不可变 MIME artifact 和完整预览。SnapshotHash 包含账号、draft revision、内容与附件 hash、MIME hash；传输发送这份已确认 MIME 字节，不在确认后重新打开可变 CV 路径并重新拼正文。

读取 MIME 时验证 hash，并在受应用大小限制的稳定字节缓冲／拒绝共享写的流中传输，封闭“检查完文件又被替换”的窗口。初版应用总 MIME 上限暂设 20 MiB（应用选择，非 Gmail 官方上限），超限在预览前报错。

本地确认消费与 Sending 创建提交后再请求 Gmail；崩溃在提交和 HTTP 之间也按 Unknown 恢复，不因“似乎还没发送”自动重试。成功响应无法落库时同样保留核对路径。SendAttempt 的 immutable artifact、RfcMessageId、原账号不可变。
