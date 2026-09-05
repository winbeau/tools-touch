# P01 领域模型、查询契约与数据迁移

状态：计划。前置：P00。设计依据：[数据模型](../details/01-data-model.md)、[记录器](../details/07-record-workspace.md)。

## 编码范围

Core 各领域实体／值对象；Application Abstractions 与 DTO；Infrastructure Persistence／Migrations／Repositories；现有 LocalDatabase 与 ResearchStore 兼容调用；Core.Tests 迁移 fixture。

## 子步骤

1. 先执行[文件迁移表](../03-implementation-map.md) P01-A 的机械搬迁与旧行为回归；再固定 School、Department、Program、Round、Appointment 语义及去重键，日期／年度模型独立实现。
2. 为旧库 001—003 制作合成 fixture，包含资料、导师、已发邮件、Unknown、未完成任务和历史来源。
3. 按当前最高迁移号新增院校及证据基础 schema；旧 Institution 转待映射，保留全部旧实体 ID。
4. 增加不可变 SourceSnapshot／Artifact／EvidenceClaim，区分抓取时间、发布年份、推断来源与人工更正。
5. 定义资料、需求、分析、推荐输入快照与申请模型；发送、记录器扩展表可随业务步骤落地，但本步骤固定 FK 和契约。
6. 定义 RecordRef／系统集合映射和字段权限；提供学校／学院／导师分页查询及系统字段只读表格接口，为 P03 的表格基础铺路。
7. 实现备份、迁移互斥、完整性检查、schema 兼容判断及派生索引重建。

## 验收

旧库升级／二次启动、异常回滚、FK、同人多任职、跨源 ID 冲突、source 多版本、null 日期／排名、历史已发送与 Unknown 保留；模拟迁移失败后可恢复旧库。SQL 页排序稳定且不会重复或遗漏并列项。

## 退出条件与回退

基础模型及查询可编译调用，旧数据保持，迁移和备份往返通过。复杂业务表随对应计划实现时继续追加迁移，不修改已经执行的迁移文件。故障时恢复一致性备份，不执行破坏性清库。
