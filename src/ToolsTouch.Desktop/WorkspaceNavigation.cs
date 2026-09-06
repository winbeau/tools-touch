using System.Windows.Input;

namespace ToolsTouch.Desktop;

// Values are stable: existing task/history navigation uses the same page IDs.
public enum WorkspacePage { Dashboard, Discover, Professors, ProfessorDetail, Outreach, Applications, Records, Settings, Admissions, Faculty, Recommendations }
public sealed record NavigationEntry(int Id, string Group, string Title, string Description, string Icon);

public sealed partial class MainViewModel
{
    public IReadOnlyList<NavigationEntry> Navigation { get; } = [
        new(0, "工作台", "工作概览", "查看研究任务进度，继续未完成的工作。", "M2,2 H8 V8 H2 Z M12,2 H18 V8 H12 Z M2,12 H8 V18 H2 Z M12,12 H18 V18 H12 Z"),
        new(8, "学校机会", "招生窗口", "查看学校与学院的招生批次，区分实际状态与历史预测。", "M2,5 H18 V18 H2 Z M2,9 H18 M6,2 V7 M14,2 V7"),
        new(1, "导师研究", "发现导师", "从研究方向出发，检索并核实公开资料。", "M13,13 L19,19 M15,8 A7,7 0 1 1 1,8 A7,7 0 1 1 15,8"),
        new(2, "导师研究", "本地导师库", "筛选已保存的导师，进入详情开展研究或准备邮件。", "M2,3 H8 V17 H2 Z M11,3 H17 V17 H11 Z"),
        new(9, "导师研究", "导师目录与覆盖", "查看学院任职、论文证据与采集覆盖情况。", "M7,6 A3,3 0 1 1 13,6 A3,3 0 1 1 7,6 M3,18 C3,10 17,10 17,18"),
        new(10, "推荐", "推荐中心", "对照候选范围、证据覆盖与历史运行，审阅推荐结果。", "M3,17 V11 M9,17 V3 M15,17 V7 M1,19 H19"),
        new(5, "申请与投递", "申请记录", "维护申请阶段、材料和提醒；官网提交与邮件联系分别记录。", "M5,3 H17 V18 H3 V3 H5 M7,2 H13 V5 H7 Z M7,9 H13 M7,13 H13"),
        new(4, "申请与投递", "邮件工作区", "审阅草稿与附件，确认后发送，并跟踪发送结果和回复。", "M2,4 H18 V16 H2 Z M2,4 L10,10 L18,4"),
        new(6, "数据记录", "表格记录", "使用自定义表、字段与保存视图整理研究和申请信息。", "M2,3 H18 V17 H2 Z M2,8 H18 M2,12 H18 M8,3 V17"),
        new(7, "资料与设置", "资料与账户", "维护已确认经历、模型服务、Gmail 与本机设置。", "M2,5 H18 M2,15 H18 M6,2 V8 M14,12 V18")
    ];
    private int professorReturnPage = (int)WorkspacePage.Professors;
    public int SelectedNavigationPage { get => SelectedPage == (int)WorkspacePage.ProfessorDetail ? professorReturnPage : SelectedPage; set { if (value >= 0) SelectedPage = value; } }
    public string PageTitle => SelectedPage == (int)WorkspacePage.ProfessorDetail ? "导师详情" : Navigation.First(item => item.Id == SelectedPage).Title;
    public string PageDescription => SelectedPage == (int)WorkspacePage.ProfessorDetail ? "阅读研究证据与论文，准备联系内容。返回列表会保留筛选和选择。" : Navigation.First(item => item.Id == SelectedPage).Description;
    public ICommand BackToProfessorsCommand => new UiCommand(() => { SelectedPage = professorReturnPage; return Task.CompletedTask; }, ReportError,
        () => SelectedPage == (int)WorkspacePage.ProfessorDetail);
    public string DraftEditorStatus => MailBusy ? "正在处理邮件，请稍候。完成后可继续编辑或核对结果。" : SelectedDraft?.State switch
    {
        null => "从左侧选择草稿；尚无草稿时，可在导师详情中生成。",
        "Unknown" => "发送结果待核实。请核对发送结果；此邮件已锁定，不会自动重试。",
        "Sent" => "此邮件已发送，正文和附件只读。可同步回复查看后续进展。",
        "Sending" => "正在发送，请稍候。正文和附件暂时只读。",
        "Failed" => "上次发送失败。请检查内容与账户状态，再确认发送。",
        _ => "修改后请保存。发送前会再次显示内容与附件，等待你确认。"
    };
}
