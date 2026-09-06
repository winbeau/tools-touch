# WPF 工作台界面改造与验证

日期：2026-09-05。范围仅为界面与交互，不提交、推送、安装或发布版本。

## 实现

- 保留 C# WPF + MVVM。`MainWindow.xaml` 负责窗口外壳、登录门禁、分组导航和全局状态；`Pages/` 中的 11 个 UserControl 保持原来的 DataContext、命令与页面实例。
- `WorkspacePage` 固定原来 0—10 的页面映射。左侧 7 个分组包含 10 个常驻入口，导师详情由列表进入；返回按钮和 Alt+Left 回到来源列表。F5 刷新入口保留。
- `Resources/Theme.xaml` 合并 `Workbench.xaml`，集中管理浅色表面、青绿色强调色、文字层级、间距、圆角、输入、按钮层级、选中、焦点、禁用、校验和状态样式。导航采用矢量 Path 图标，无新增 UI 框架或 NuGet 依赖。
- 邮件工作区分为草稿列表、收件人与主题、正文、附件、操作区。发送确认仍由原来的不可变快照流程完成；发送与同步期间显示进度并临时只读；Sent／Unknown 保持锁定，Unknown 不允许重发。
- 表格记录器将编辑区与筛选、保存视图、导入、新建表和字段分开；账户页分为模型与邮箱、我的资料、设置与诊断；导师目录的论文、评价、覆盖改为局部标签，增加每张表的可用高度。
- 表格保留原生虚拟化模板。空状态使用独立 Adorner；主表不放进无限高度的外层滚动容器。表单区域独立滚动，横向工具条必要时换行。
- 刷新申请表单和记录器时保留选择、筛选、保存视图及未保存内容，并保留编辑时加载的修订号，避免绕过已有并发修改检查。页面切换保留实例和滚动位置。
- 增加 PerMonitorV2 清单。生产程序和原生测试使用同一清单。

数据库迁移仍为 016，协议仍为 v2；无业务数据迁移，无真实账号操作。现有未跟踪 `pi/auth.json` 保留且未读取。命令入口对照结果为原有 54 个全部保留，改造后 57 个（增加明确返回、记录刷新、申请阶段确认入口）。

## 已执行检查

| 检查 | 命令 / 证据 | 结果 |
| --- | --- | --- |
| WSL Windows 互操作 | `wsl -d Ubuntu-22.04 --exec /mnt/c/Windows/System32/cmd.exe /c ver` | 退出 0；Windows 11，10.0.26200.9168 |
| 改造前原生基线 | 下述 Windows 命令，旧测试程序 `artifacts/desktop-tests-v0.3.1-final/ToolsTouch.Desktop.Tests.exe`，输出 `artifacts/ui-redesign/before` | 退出 0，14 组检查及 11 页、详情截图 |
| 核心可执行回归 | `dotnet run --project tests/ToolsTouch.Core.Tests -c Release` | 退出 0；实际执行 SQLite、草稿、确认发送、Unknown、申请、表格、导入导出等测试，使用合成数据 |
| WPF 交叉构建 | `dotnet publish tests/ToolsTouch.Desktop.Tests -c Release -r win-x64 --self-contained true -o artifacts/ui-redesign/build` | 退出 0 |
| 第二轮原生检查 | 输出 `artifacts/ui-redesign/after-r2` | 退出 0，20 组检查 |
| 第三轮原生检查 | 输出 `artifacts/ui-redesign/after-r3` | 退出 0，22 组检查，包括所有折叠区、设置与详情子页、滚动保留、Unknown 只读 |
| 最终原生检查 | 输出 `artifacts/ui-redesign/final` | 退出 0，23 / 23 组通过，零 WPF 绑定错误；包括邮件加载与编辑保护检查 |
| 原入口完整性 | `artifacts/ui-redesign/command-coverage.json` | 原有 54 / 54 保留，无缺失 |
| 修改格式 | `git diff --check` | 退出 0 |

Linux .NET 实际路径为 `/home/winbeau/.local/share/tools-touch-dotnet/dotnet`。核心回归使用 Linux Node 24.16.0 的 PATH。Windows 原生执行命令：

```powershell
uv run --isolated --no-project --no-config `
  --python C:/Users/genev/AppData/Local/Programs/Python/Python312/python.exe `
  python scripts/test-windows.py `
  --package artifacts/final-v0.3.1/ToolsTouch-win-x64-v0.3.1 `
  --test-exe artifacts/ui-redesign/build/ToolsTouch.Desktop.Tests.exe `
  --output artifacts/ui-redesign/final
```

脚本把自包含测试程序和已有 Node/Pi 运行时复制到 Windows 独立临时目录，创建独立 SQLite 和模拟凭据；不会启动生产 App.OnStartup、打开正式工作区或发送真实邮件。已有程序包仅提供测试所需的运行时，不是本轮发布产物。

## 原生验证覆盖与限制

- 11 个主页面及导师详情、导师目录、资料与设置的局部标签均渲染。数据绑定错误监听覆盖页面切换和交互。
- 窗口逻辑尺寸 1040×720、1360×900、1600×1000，共 33 个页面布局；检查按钮文字宽度、页面边界及表格虚拟化配置。折叠区全部展开后逐个把按钮滚动到可见区域，检查完整可达性。
- 通过真实 WPF 焦点系统检查收件人→主题→正文的 Tab 顺序，检查下拉弹出、数字输入错误提示及修正、返回命令、刷新和未保存内容保留。
- 主机原生窗口初始 DPI 为 144（150%）。对隔离 HWND 发送 `WM_DPICHANGED`，WPF 实际报告 96→120→144→96，即 100%→125%→150%→100%；每种 DPI 都遍历 11 页并保存截图。实际报告值记录于 `dpi-results.json`，不是仅放大已有图片。
- **未验证**：人工拖动窗口跨越真实不同 DPI 显示器、修改操作系统缩放设置后的重新登录、长时间真人使用与屏幕阅读器体验。DPI 消息注入验证不等同于真实显示器切换验收。没有使用真实 Gmail、模型账号或真实收发邮件；这些仍按原验收矩阵标记未验证。

## 截图与问题修正

- 改造前：`artifacts/ui-redesign/before/page-1.png` 至 `page-11.png`。
- 改造后：`artifacts/ui-redesign/final/dpi-96-page-1.png` 至 `dpi-96-page-11.png`；同目录还有 120／144 DPI、三种窗口大小和所有展开区截图。
- 代表页面：`before/page-5.png` 与 `final/dpi-96-page-5.png`（邮件）；`before/page-7.png` 与 `final/records-populated.png`（记录）；`before/page-8.png` 与 `final/dpi-96-page-8.png`（账户）。
- 状态证据：`records-no-results.png`、`validation-error.png`、`outreach-unknown.png`、`outreach-loading.png`、`applications-populated.png`。
- 第一轮截图发现下拉框显示对象描述、输入内边距叠加、空状态定位错误；均已修正并回归。第二轮截图暴露测试截取内容树未包含窗口背景及外层 Adorner 的问题，已使用显式背景与根 AdornerDecorator 修正。中间截图保留用于追溯，不作为最终效果。
