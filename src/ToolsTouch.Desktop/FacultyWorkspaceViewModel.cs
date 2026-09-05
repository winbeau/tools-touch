using System.Collections.ObjectModel;
using System.Windows.Input;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed class FacultyWorkspaceViewModel : Observable
{
    private readonly IFacultyQueryService queries;
    private readonly Action<Exception> reportError;
    private FacultyListRow? selectedFaculty;
    private string filter = "";
    private string status = "尚未加载导师目录。";

    public ObservableCollection<FacultyListRow> Faculties { get; } = [];
    public ObservableCollection<FacultyAppointmentRow> Appointments { get; } = [];
    public ObservableCollection<FacultyPaperRow> Papers { get; } = [];
    public ObservableCollection<FacultyEvidenceRow> Evidence { get; } = [];
    public ObservableCollection<FacultyEvaluationRow> Evaluations { get; } = [];
    public ObservableCollection<FacultyCoverageRow> Coverage { get; } = [];

    public string Filter { get => filter; set => Set(ref filter, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public FacultyListRow? SelectedFaculty
    {
        get => selectedFaculty;
        set
        {
            if (!Set(ref selectedFaculty, value)) return;
            LoadDetail();
            Raise(nameof(HasSelectedFaculty));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool HasSelectedFaculty => SelectedFaculty is not null;
    public ICommand RefreshCommand { get; }

    public FacultyWorkspaceViewModel(IFacultyQueryService queries, Action<Exception> reportError)
    {
        this.queries = queries;
        this.reportError = reportError;
        RefreshCommand = new UiCommand(() => { Refresh(); return Task.CompletedTask; }, reportError);
    }

    public void Refresh()
    {
        var selectedId = SelectedFaculty?.ProfessorId;
        var result = queries.ListFaculty(Filter, new PageRequest(200));
        Replace(Faculties, result.Items);
        Replace(Coverage, queries.ListCoverage());
        SelectedFaculty = Faculties.FirstOrDefault(item => item.ProfessorId == selectedId) ?? Faculties.FirstOrDefault();
        if (SelectedFaculty is null) ClearDetail();
        Status = $"已加载 {Faculties.Count} 位导师，覆盖批次 {Coverage.Count} 条。" +
            (result.NextCursor is null ? "" : " 结果超过当前页，请缩小筛选范围。") ;
    }

    private void LoadDetail()
    {
        ClearDetail();
        if (SelectedFaculty is null) return;
        var detail = queries.GetDetail(SelectedFaculty.ProfessorId);
        Replace(Appointments, detail.Appointments);
        Replace(Papers, detail.Papers);
        Replace(Evidence, detail.Evidence);
        Replace(Evaluations, detail.Evaluations);
    }

    private void ClearDetail()
    {
        Appointments.Clear();
        Papers.Clear();
        Evidence.Clear();
        Evaluations.Clear();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}
