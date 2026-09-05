# 表格数据记录器详细设计

返回[详细设计](../02-detailed-design.md)。本模块源于用户补充：项目同时作为类似飞书／Notion 数据库的表格记录器，支持学校分表与全量导出。首版目标是结构化记录、字段扩展、关联视图和可靠导出。

## 1. 首版能力与边界

| 能力 | 首版设计 |
| --- | --- |
| 内置数据表 | 学校、学院／项目／批次、导师、分析、推荐、申请、邮件、任务 |
| 自定义表 | 用户创建“材料清单”“面试安排”“备选计划”等表，可关联内置记录 |
| 自定义字段 | 文本、数字、日期／时间、复选框、URL、单选、多选、关联 |
| 表格操作 | 单元格编辑、新增记录、粘贴、批量修改、归档、撤销可逆编辑 |
| 保存视图 | 筛选、排序、分组、列顺序／宽度／显隐、冻结列；表格为首要视图 |
| 记录详情 | 侧边详情、相关学校／导师／申请、附件、来源、更改历史 |
| 导入 | CSV／XLSX／应用数据包，字段映射、预览、重复处理与校验报告 |
| 导出 | 当前视图、当前表全部、学校分工作表、整个工作区业务数据包 |
| 后续扩展 | 看板／日历、有限公式与汇总、模板库；不作为首版表格闭环前置条件 |

不引入任意脚本公式或在线多人文档协作。它们需要独立权限／执行与同步设计，可在确认需求后增加。

## 2. “表”和“视图”的区别

Collection 是数据集合；ViewDefinition 是对同一集合的查询与布局。例如“今年预推免”“本周截止”“等待导师回复”只是保存的视图，不复制数据。学校详情页的学院表也是全局招生集合中 `SchoolId=当前学校` 的固定过滤视图。

内置集合由已有领域实体提供记录；自定义集合由通用用户记录提供。数据库不为每个学校、每个视图或每个用户自定义表动态建物理 SQL 表。

## 3. 元数据与字段值模型

| 表 | 关键字段与约束 |
| --- | --- |
| Collection | Id、Name、Kind=System／Custom、SystemEntityKind?、Description?、ArchivedAt?、Revision |
| RecordRef | Id、CollectionId FK、EntityKind?、EntityId?、CreatedAt、ArchivedAt?、Revision；系统实体引用唯一；自定义记录无 EntityId |
| FieldDefinition | Id、CollectionId FK、Key、DisplayName、Type、StorageKind=System／Custom、SystemBinding?、OptionsJson、Required、ArchivedAt?、Revision；同 collection＋key 唯一 |
| FieldChoice | Id、FieldId FK、Label、ColorToken、SortOrder、ArchivedAt?；选项重命名不改变 ID |
| FieldValue | RecordId FK、FieldId FK、TextValue?、NumberValue?、DateValue?、BoolValue?、JsonValue?、Revision；联合主键；按类型只使用对应值槽，缺失不建行 |
| RecordRelation | FieldId FK、FromRecordId FK、ToRecordId FK、SortOrder；联合唯一；目标 collection、单双向与数量限制来自字段定义 |
| ViewDefinition | Id、CollectionId FK、Name、ViewType、FilterAstJson、SortJson、GroupJson、ColumnsJson、Revision |
| RecordChange | Id、RecordId FK、FieldId?、CommandId、ActorKind、BeforeJson、AfterJson、OccurredAt、UndoOf?；可追踪批量命令 |

跨表关联使用 RecordRef，领域实体的实体引用仍由领域表 FK 管理；两者生命周期在同一事务维护，启动一致性巡检可发现缺失映射。外部导入 ID 不直接当 RecordRef ID。

系统字段通过 `SystemBinding` 映射到类型化 Query／Command，不在 FieldValue 重复存一份。例如 School.Name 取 School 表；用户自定义“优先级”在 FieldValue；若某字段已是领域字段，就绑定原字段。应用验证字段与记录同属 collection，关联目标合法，JSON 多选只含有效 Choice ID。

数字初版提供小数位显示与范围约束；金额、精确 GPA 原值保留可使用规范化十进制文本并配排序值，不能让浮点展示改变正式成绩。日期字段明确 DateOnly 或含时区 DateTimeOffset。大文件存 Artifact，关系／附件由专用类型后续扩展或系统附件字段管理。

## 4. 编辑规则与来源更正

字段有三种编辑权限：Editable、OverrideWithReason、SystemManaged。普通备注／优先级／自定义字段 Editable；官网抽取的学院名称／报名日期通过 OverrideWithReason 建立用户更正，保留原来源和理由；Sent、Gmail 回执、任务状态和证据抓取时间 SystemManaged。

系统字段编辑路由到对应 Application Command。例如修改报名日期调用 CorrectWindowObservation，写入 UserVerified 观察；不会直接 UPDATE 派生状态。用户可撤销自己的更正，恢复使用来源事实。自定义表可自由增改记录，归档有关联的行先展示引用影响。

`UpdateCellCommand` 包含 recordId、fieldId、expectedRevision、typedValue、commandId、reason?；`BulkEditCommand` 包含固定 RecordIds 与最大 1,000 行批次。确认批量范围后按批事务校验；失败报告到行与字段，不能显示“全部完成”却丢部分错误。通用表格层不能调用发送、授权或学校网站提交操作。

粘贴先解析矩形区域并展示新增／修改预览；枚举未匹配、日期歧义、重复收件人等给错误定位。导入 CSV 中的“Sent”只能作为来源文本或通过专用历史导入模式生成 ImportedHistory；不能伪造新的实际 Gmail 回执。

撤销通过有条件的反向业务命令执行：要求当前 Revision 仍对应刚才的操作；后续有变更则提示冲突。已经发送邮件、外部申请等事实不能用 Ctrl+Z 撤销。

## 5. 查询与布局

FilterAst 是受限结构，支持 and／or、eq／contains／range／isEmpty 等按字段类型开放的运算。禁止保存任意 SQL 文本。`RecordQueryCompiler` 将白名单字段映射成参数化 SQL，系统字段查询和自定义 FieldValue 索引查询组合，关系通过 EXISTS／分页关联查询实现。

自定义字段建立 `(FieldId, NumberValue, RecordId)`、`(FieldId, DateValue, RecordId)`、`(FieldId, TextValue, RecordId)` 等可选索引；仅为有需要的字段类型启用。FTS 文本索引异步重建且有版本，源数据优先于索引。所有排序用 RecordId 兜底；虚拟化与分页见[桌面设计](06-desktop-delivery.md)。

默认视图提供：学校总览、按学校学院分组、今年实际开放、历史预测待确认、导师方向匹配、待投递、已联系待回复、面试安排。用户可复制视图并调整列；预设不阻止全量导出。

## 6. 导出范围与格式

ExportRequest 明确 Scope=CurrentView／CollectionAll／AllSchools／WorkspaceAll、Format、IncludeHistory、IncludeAttachments；默认不根据“当前是否勾选全部”猜范围。用户补充的全量导出以 WorkspaceAll 支持，不受隐藏行／筛选／分页影响。

| 格式／模式 | 内容 |
| --- | --- |
| 当前视图 CSV／XLSX | 同一快照下全部匹配行，按视图排序和显示列；不是只导出当前 50 行 |
| 学校分表 XLSX | 总览一张表；每校一张学院／项目／批次表，保留本年事实与历史参考列 |
| 全工作区 XLSX | 学校分表＋导师／申请／邮件／分析／推荐＋自定义集合表，关联使用稳定 ID 与显示名；超长正文另附文件并标路径 |
| 全量业务 ZIP | manifest.json、各实体／关系／修订／自定义字段／视图的 JSONL、分析正文，及可选附件／来源原文；用于完整保真备份和迁移 |

全量业务 ZIP 的 schemaVersion、exportVersion、datasetRevision、导出时刻、记录数、所有文件哈希和已排除项写入 manifest。凭据／授权码／Pi 认证目录不属于业务数据导出；如果用户选择不含附件，manifest 明确这些引用的文件未打包。可恢复完整备份需要含被业务引用的文件。

XLSX 是便于阅读的投影，JSONL 数据包是保真格式：空值、数组、关联、多版本与长文本不能仅凭 Excel 展示恢复。Excel 工作表名清理非法字符、截断到限制并追加稳定短 ID；重名、行数和单元格长度超限自动拆分并在索引页列映射，禁止静默截断。CSV／XLSX 文本字段处理公式前缀；正常数字保持数值类型。

全量导出先创建一致性 SQLite 快照，依据快照流式查询写临时文件，核对行数／哈希后原子重命名。导出过程用户可继续工作，输出仍绑定同一 DatasetRevision。取消导出删除临时产物，不出现貌似成功的半文件。

## 7. 导入、恢复与扩展

表格导入采用选择文件→识别编码／sheet→字段映射→类型校验→重复策略→预览→提交报告。支持追加、按明确稳定键更新、只预览；系统实体姓名不作为默认覆盖键。关系先暂存再解析，缺失引用进入错误报告；默认不静默建同名关系。

应用 ZIP 恢复先验证版本、路径安全、哈希和 FK，解压到隔离暂存目录；首版固定提供完整恢复到新工作区、或在关闭任务并备份原库后替换。首版表格合并限自定义集合／显式系统字段映射；全业务包跨工作区自动合并后续实现，不能把全量覆盖叫作合并。重发邮件不是任何导入／恢复的附带效果。正式 manifest、恢复顺序及暂停任务约定见[核心契约](../04-contracts.md)。

自定义字段类型变更先试转换，显示不兼容行；不能直接清空旧值。归档字段隐藏但保留值和历史，可恢复。未来公式／汇总以受限表达式和依赖图实现，单独定义循环检测和重算预算。

## 8. 编码入口与验收

Application：`RecordWorkspaceService`、`FieldSchemaService`、`RecordQueryService`、`BulkEditService`、`ViewService`、`WorkspaceExportService`、`ImportPreviewService`。

Infrastructure：`RecordRepository`、`RecordQueryCompiler`、`SystemCollectionAdapter`、`XlsxWorkspaceWriter`、`WorkspaceBundleWriter`、`BundleValidator`。

WPF：`RecordGridViewModel`、`FieldEditorViewModel`、`FilterBuilderViewModel`、`RecordDetailViewModel`、`ExportDialogViewModel`。内置不同集合共用表格控件，但保留各业务命令与详情组件。

验收包括：新建自定义表／字段、类型校验、关联查找、保存视图重启恢复、批量编辑冲突及撤销、系统状态只读、来源更正保留原文、全量导出不受当前分页／筛选影响、长文本不丢失、工作表重名拆分、公式前缀、取消导出、导入预览／关系恢复、自定义字段与历史版本的完整往返。
