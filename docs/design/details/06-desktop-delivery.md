# WPF 交互、工程环境与交付

返回[详细设计](../02-detailed-design.md)。表格字段、视图、导入导出细节见[表格数据记录器](07-record-workspace.md)。

## 1. 工作台信息架构

WPF 继续采用 MVVM。主窗口只负责导航、全局申请年度、搜索入口、运行状态和用户工作区；每个页面有独立 ViewModel，避免继续扩大 MainViewModel／MainWindow.xaml。

| 页面 | 核心区域与主要操作 |
| --- | --- |
| 工作台 | 即将截止、预计待开放、待核实、待回复、任务进展；点击进入对应保存视图 |
| 学校机会 | 学校总览→校内学院／项目／批次；同步、筛选、查看来源、加入申请 |
| 导师库 | 学校／学院过滤、方向、职称、本年招生、论文、评价、采集覆盖；研究／联系 |
| 推荐中心 | 选择资料与需求版本→学院／导师推荐→分项与引用→对比历史结果 |
| 投递工作台 | 申请阶段、材料、邮件草稿、发送确认、回执、回复与提醒 |
| 数据记录 | 内置表、自定义表、字段配置、保存视图、导入与全量导出 |
| 任务中心 | 父子任务、阶段、来源、完成／失败项、预算、取消、继续与重试 |
| 我的资料 | CV／背景事实、人工确认、需求提示与版本历史 |
| 账户与设置 | Gmail、Pi provider／模型、搜索来源配置、存储、备份与诊断 |

页面可合并视觉分组，但业务入口全部可达。学校／导师详情在右侧抽屉或独立详情页打开，返回时保留过滤、滚动位置、选择和未保存编辑。

## 2. 表格布局示意

```text
┌ 导航 ─────┬ 学校机会   申请年度 2026   搜索…       刷新  导出 ┐
│ 工作台    │ 视图：全部学校 ▾   筛选   分组   列设置   保存视图 │
│ 学校机会  ├ 学校 ─ 开放学院 ─ 待开放 ─ 历史预测 ─ 最近截止 ─┤
│ 导师库    │ 示例大学 A     2        1        3       09-18   │
│ 推荐中心  │ 示例大学 B     0        2        4       待核实  │
│ 投递      ├────────────────────────┬─────────────────────┤
│ 数据记录  │ 分页／载入更多／结果总数 │ 选中记录详情、来源、关联 │
│ 任务中心  │                        │ 自定义字段、变更记录    │
└───────────┴────────────────────────┴─────────────────────┘
```

学校行默认列：学校、地区、本年实际开放数、待开放数、历史预测数、待核实数、最近实际截止、推荐摘要、申请进展、更新来源。详情默认分为“学院与窗口／导师／画像与推荐／我的申请”，避免一行同时塞入几十个多值字段。

导师表默认列：姓名、任职学院、职称、方向、本年目标学位招生、证据更新时间、匹配分／覆盖度、联系状态。展开后展示摘要、论文阅读范围、招生证据和评价来源。

## 3. 视觉与可编辑行为

采用统一 ResourceDictionary 的字号、间距、边框、强调色和状态色。表头清晰、行高适中、长文本省略后可展开，链接有可点击样式。未知显示“待核实”；预测使用单独标记，绝不只靠颜色区分状态。

DataGrid 支持键盘移动、Tab、复制、编辑确认／取消；单元格显示验证错误和保存状态。普通字段失焦可提交，批量修改先预览范围；受保护字段显示来源或只读原因。Ctrl+Z 只作用于可逆本地编辑。

提供列排序、显隐、宽度与冻结、组合过滤和保存视图。实体关联字段用搜索选择器，不要求手输 ID；点击关联可导航。长篇分析与邮件正文在详情编辑器中显示，不塞入默认表格行。

空数据、过滤无结果、尚未同步、部分失败、来源过期、登录失效分别给出正确状态。刷新时保护用户尚未提交的内容；切换导师后清除旧人的草稿／历史上下文。

## 4. 性能设计与验收目标

DataGrid 开启行／列虚拟化及 recycling，避免在无限高度 StackPanel 或外部 ScrollViewer 中破坏虚拟化。选择、展开和编辑状态绑定到记录 ID；UI 容器回收不能让另一行继承上一行状态。WPF UI 虚拟化不会自动提供数据分页，因此数据库查询与分页仍需单独实现。[Microsoft WPF 控件性能文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls)

查询默认 50 行，最大 200；输入搜索 250—350ms 去抖并取消旧查询，以 query generation 丢弃迟到结果。排序／筛选下推 SQL，详情关联按需加载；不要一次把 10 万位导师及全部论文／评价加载到 ObservableCollection。

初始性能目标：在约定的 Windows x64、4 核、16GB 内存、SSD 基准机上，100,000 导师规模的常见索引筛选首屏 p95 < 500ms；复杂自定义字段查询 p95 < 1s；无网络等待时已建立工作区首屏可用 < 3s。没有基准测试前这些只是验收目标。批量导出流式执行、可取消、常驻内存不随全量行数线性增长。

WPF STA 线程只做 UI 更新；HTTP、SQLite 批量查询、PDF 解析和导出在后台调度。任务进度节流为每秒数次，不为每个 token 重绘整表。

## 5. pnpm 与 uv 管理

这是目标开发规范；当前仓库 npm／Python 手工环境尚未在文档阶段迁移。

| 环境 | 目标 |
| --- | --- |
| C# | 沿用 net10.0／net10.0-windows 和现有 global.json；.NET SDK／NuGet 锁文件固定，升级单独验证 |
| Node | 保留已使用的 Node 24 基线，Pi 0.85.0 先不升级；根 packageManager 固定经 P00 验证的 pnpm 精确版本 |
| Node 包 | pnpm workspace 仅纳入 agent-host 等应用包；一个根 pnpm-lock.yaml；不纳入 pi 上游工作区 |
| Python | 根 pyproject.toml＋uv.lock、固定 `.python-version`；baoyan-cli／collector-host 为 workspace 成员 |
| 开发脚本 | scripts/ 中 Python 通过 uv run --all-packages --locked 调用；开发机装 pnpm／uv，用户安装包无需安装 |

迁移时导入现有 npm 解析结果、对比直接与传递依赖，再运行冻结安装；Pi 包可能存在隐式依赖，按实际发布包补齐应用依赖，不用全局提升掩盖缺包。迁移完成后应用开发／CI／打包指南统一使用 pnpm，不同时维护两份活跃应用锁文件。`pi/` 的上游文件不在这次范围。

冻结安装通过 `pnpm install --frozen-lockfile --ignore-scripts` 保证锁文件与声明一致；必要原生构建脚本逐项记录并显式允许。[pnpm install 文档](https://pnpm.io/cli/install)

Python 使用 `uv sync --all-packages --locked`、`uv run --all-packages --locked ...`，显式包含 workspace 成员；`--locked` 在配置与锁不一致时失败，`--frozen` 跳过一致性检查，不能把两者当成等价的验证。[uv 锁定与同步文档](https://docs.astral.sh/uv/concepts/projects/sync/)

P00 后统一命令示意：

```sh
pnpm install --frozen-lockfile --ignore-scripts
pnpm --filter tools-touch-agent-host test
dotnet run --project tests/ToolsTouch.Core.Tests
uv sync --all-packages --locked
uv run --all-packages --locked python -m unittest discover -s baoyan-cli -p 'test_*.py'
uv run --all-packages --locked python scripts/package.py
```

新增 Python 模块的 pytest／类型检查入口在其 pyproject 注册后再写入 CI；不提前宣称当前已有这些命令。不要在文档阶段触发 dependency migration 或真实模型请求。

## 6. Windows 打包与启动

目标安装包包含 self-contained .NET、Windows Node、AgentHost 生产依赖、Windows Python 运行时及采集生产依赖。Python 浏览器能力如需 Chromium，可独立可选组件，先静态采集可用；开发安装浏览器和最终用户使用外部 Chrome 是不同部署选择，应由实际采集需要决定。

优先在原生 Windows runner 安装目标平台依赖并打包。pnpm 产物需要包含完整可移植依赖树，不能依赖开发机全局 store 或离包符号链接；可评估 pnpm deploy，但以固定版本实际隔离运行验证为准。[pnpm deploy](https://pnpm.io/cli/deploy)

安装到当前用户目录，数据位于 `%LOCALAPPDATA%\ToolsTouch` 下的工作区。沿用当前数据路径，通过显式迁移调整子目录；示意子目录为 database／artifacts／exports／logs／pi／credentials，各自职责清晰。卸载默认保留用户工作区，升级先备份再迁移。

启动顺序：加载配置与单实例→校验数据库兼容→迁移／恢复→渲染本地首屏→后台组件握手→账号状态→恢复可恢复任务。Google 配置缺失、Pi 缺依赖、Python 不可用分别显示组件原因，不能卡在无说明的进度条。

## 7. 验证层次与发布记录

| 层次 | 验证内容 |
| --- | --- |
| 规则 | 年度窗口、资格、分母、推荐稳定性、字段类型、发送状态 |
| 真实 SQLite | 旧库迁移、外键、并发冲突、任务恢复、导出一致性 |
| 子进程 | C#↔Pi／Python 握手、消息限额、取消、重启、生产依赖 |
| 固定来源样本 | 公告抽取、目录分页、同名消歧、证据定位、部分失败 |
| Windows 原生 | 页面／详情渲染、键盘、编辑、DPI、DPAPI、长路径、休眠恢复 |
| 安装包 | 干净用户无需开发环境启动、更新、卸载保留数据、恢复备份 |
| 真实业务 | 用户指定 provider 登录／模型调用、实际学校样本、Gmail 登录、具体邮件确认发送与回复 |

日志保存 request／job／stage ID、耗时、数量与错误码，不含密钥、授权输入、完整 CV 或邮件正文。发送正文属于受控业务快照，不能混进诊断压缩包。保留现有诊断保留期与导出入口，并让用户看到诊断包的范围。

本轮仅编写设计，不执行上述真实流程。每次实现交付分别报告自动测试、真实来源抽查、原生 Windows 与真实账号验收情况。
