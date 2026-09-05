# P03 招生数据、年度窗口与学校表

状态：计划。前置：P01、P02。设计依据：[窗口规则](../details/03-admission-windows.md)。

## 编码范围

baoyan-cli 机器导出；collector-host 来源适配；Core Admissions 的 CycleResolver／WindowEvaluator；Application AdmissionService；Infrastructure BaoyanAdapter；Desktop SchoolOverview／SchoolDetail 与初版共用只读 RecordGrid。

## 子步骤

1. 与用户登记 3—5 所学校的完整名称、计算机相关学院、官方目录及申请年度种子；明确学硕／专硕／直博范围。
2. 兼容当前中文字段 JSON，并新增版本化机器导出；保留 raw record，读取当前及历史记录，不只过滤当前网站 year。
3. 实现导入暂存、跨源 ID、学校／学院别名、歧义队列；校级公告不能假设覆盖所有学院。
4. 实现 CycleResolver，分离 SourceYear／CycleYear／EntryYear；年份冲突留待核实。
5. 实现纯 WindowEvaluator、本年事实优先、历史投影和跨年／精度规则；UI 与导出共用。
6. 学校总览每校一行，学校详情按学院／项目／批次分组，展示历史／预计／实际及报名链接；保存基础视图，C# 提供同一查询快照的基础 CSV 导出。Excel 和全量数据包在 P07 完成。
7. 提供按校刷新、部分失败清单、人工日期更正与应用内提醒；原始来源不被更正覆盖。

## 验收

执行窗口文档全部日期矩阵，特别是 2025-09-12—18 在 2026-09-05 的预测；空年份、2027 级标题、本年更早结束、不同批次、洛杉矶机器时区、过了预计截止仍不实际 Closed。验证分页重复／总数变化拒绝伪完整导入。

首轮真实数据人工抽查每校至少一个历史与本年有可用来源的轮次；若本年未公布如实记录，不能补造测试事实。GUI 验证学校汇总不因一个学院结束而标整校结束。

## 退出条件与回退

首个可用招生工作台可浏览、过滤、保存来源和展示本年／历史状态。坏批次隔离，保留上次有效结果；不删除全部旧数据后再同步。
