using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using Microsoft.Win32;
using ToolsTouch.Application;

namespace ToolsTouch.Desktop;

public sealed class AdmissionWorkspaceViewModel : Observable
{
    private readonly IAdmissionQueryService queries;
    private readonly Action<Exception> reportError;
    private SchoolOverviewRow? selectedSchool;
    private string status = "尚未加载招生学校。";

    public ObservableCollection<SchoolOverviewRow> Schools { get; } = [];
    public ObservableCollection<AdmissionRoundRow> Rounds { get; } = [];
    public int TargetCycleYear { get; set; } = DateTimeOffset.Now.Year;
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public string Status { get => status; private set => Set(ref status, value); }
    public SchoolOverviewRow? SelectedSchool
    {
        get => selectedSchool;
        set
        {
            if (!Set(ref selectedSchool, value)) return;
            LoadRounds();
            Raise(nameof(HasSelectedSchool));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool HasSelectedSchool => SelectedSchool is not null;
    public ICommand RefreshCommand { get; }
    public ICommand ExportCsvCommand { get; }

    public AdmissionWorkspaceViewModel(IAdmissionQueryService queries, Action<Exception> reportError)
    {
        this.queries = queries;
        this.reportError = reportError;
        RefreshCommand = new UiCommand(() => { Refresh(); return Task.CompletedTask; }, reportError);
        ExportCsvCommand = new UiCommand(() => { ExportCsv(); return Task.CompletedTask; }, reportError, () => HasSelectedSchool);
    }

    public void Refresh()
    {
        var selectedId = SelectedSchool?.SchoolId;
        var page = queries.ListSchools(TargetCycleYear, TimeZoneId, DateTimeOffset.UtcNow, new PageRequest(200));
        Replace(Schools, page.Items);
        SelectedSchool = Schools.FirstOrDefault(row => row.SchoolId == selectedId) ?? Schools.FirstOrDefault();
        if (SelectedSchool is null) Rounds.Clear();
        Status = $"已加载 {Schools.Count} 所学校，年度 {TargetCycleYear}，业务时区 {TimeZoneId}。";
    }

    private void LoadRounds()
    {
        Rounds.Clear();
        if (SelectedSchool is null) return;
        var page = queries.ListRounds(SelectedSchool.SchoolId, TargetCycleYear, TimeZoneId, DateTimeOffset.UtcNow, new PageRequest(200));
        foreach (var row in page.Items) Rounds.Add(row);
    }

    private void ExportCsv()
    {
        if (SelectedSchool is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"{SelectedSchool.SchoolName}-{TargetCycleYear}-招生窗口.csv",
            Title = "导出招生窗口 CSV"
        };
        if (dialog.ShowDialog() != true) return;
        var csv = queries.ExportRoundsCsv(SelectedSchool.SchoolId, TargetCycleYear, TimeZoneId, DateTimeOffset.UtcNow);
        File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Status = $"已导出 {Rounds.Count} 条招生轮次：{dialog.FileName}";
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}
