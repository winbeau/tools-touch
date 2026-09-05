using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using ToolsTouch.Application;

namespace ToolsTouch.Desktop;

public sealed record RecommendationItemRow(
    string TargetKind,
    string TargetId,
    string Eligibility,
    int? Rank,
    string Score,
    string ScoreRange,
    string Coverage,
    string Components,
    string Evidence,
    string MissingFacts,
    string Reasons,
    int SourceQuality,
    int? DisplayOrder);

public sealed record RecommendationComparisonRow(
    string TargetKind,
    string TargetId,
    string CurrentEligibility,
    string CurrentRank,
    string CurrentScore,
    string ComparedEligibility,
    string ComparedRank,
    string ComparedScore,
    string RankChange,
    string ScoreChange);

public sealed class RecommendationCenterViewModel : Observable
{
    private readonly IRecommendationRepository repository;
    private readonly Action<Exception> reportError;
    private RecommendationRunRecord? selectedRun;
    private RecommendationRunRecord? compareRun;
    private string status = "尚无推荐运行。先完成候选资料和评分，再从这里查看可追溯结果。";
    private string runSummary = "选择一个推荐运行查看候选范围。";
    private string coverageSummary = "";
    private string scopeSummary = "";

    public ObservableCollection<RecommendationRunRecord> Runs { get; } = [];
    public ObservableCollection<RecommendationItemRow> Items { get; } = [];
    public ObservableCollection<RecommendationComparisonRow> Comparison { get; } = [];

    public RecommendationRunRecord? SelectedRun
    {
        get => selectedRun;
        set
        {
            if (!Set(ref selectedRun, value)) return;
            LoadSelectedRun();
            Raise(nameof(HasSelectedRun));
            Raise(nameof(HasComparison));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public RecommendationRunRecord? CompareRun
    {
        get => compareRun;
        set
        {
            if (!Set(ref compareRun, value)) return;
            LoadComparison();
            Raise(nameof(HasComparison));
        }
    }

    public bool HasSelectedRun => SelectedRun is not null;
    public bool HasComparison => SelectedRun is not null && CompareRun is not null && SelectedRun.Id != CompareRun.Id;
    public string Status { get => status; private set => Set(ref status, value); }
    public string RunSummary { get => runSummary; private set => Set(ref runSummary, value); }
    public string ScopeSummary { get => scopeSummary; private set => Set(ref scopeSummary, value); }
    public string CoverageSummary { get => coverageSummary; private set => Set(ref coverageSummary, value); }
    public ICommand RefreshCommand { get; }

    public RecommendationCenterViewModel(IRecommendationRepository repository, Action<Exception> reportError)
    {
        this.repository = repository;
        this.reportError = reportError;
        RefreshCommand = new UiCommand(() => { Refresh(); return Task.CompletedTask; }, reportError);
    }

    public void Refresh()
    {
        var selectedId = SelectedRun?.Id;
        var compareId = CompareRun?.Id;
        Replace(Runs, repository.ListRuns());
        SelectedRun = Runs.FirstOrDefault(run => run.Id == selectedId) ?? Runs.FirstOrDefault();
        CompareRun = compareId is null ? null : Runs.FirstOrDefault(run => run.Id == compareId && run.Id != SelectedRun?.Id);
        if (SelectedRun is null)
        {
            Items.Clear();
            Comparison.Clear();
            RunSummary = "选择一个推荐运行查看候选范围。";
            ScopeSummary = "";
            CoverageSummary = "";
            Status = "尚无推荐运行。先完成候选资料和评分，再从这里查看可追溯结果。";
        }
        else
        {
            Status = $"已加载 {Runs.Count} 个推荐运行；当前结果保留候选范围和资料版本。";
        }
    }

    private void LoadSelectedRun()
    {
        Items.Clear();
        Comparison.Clear();
        if (SelectedRun is null)
        {
            RunSummary = "选择一个推荐运行查看候选范围。";
            ScopeSummary = "";
            CoverageSummary = "";
            return;
        }

        var sourceItems = repository.ListItems(SelectedRun.Id);
        foreach (var item in sourceItems) Items.Add(ToRow(item));
        RunSummary = $"{SelectedRun.Level} · {SelectedRun.State} · {SelectedRun.TargetCycleYear} · 算法 {SelectedRun.AlgorithmVersion} · 资料修订 {SelectedRun.DatasetRevision} · {SelectedRun.AsOf:yyyy-MM-dd HH:mm:ss zzz}";
        ScopeSummary = $"候选快照 {SelectedRun.CandidateSnapshotArtifactId} · {SnapshotCount(SelectedRun.Id)} 个输入对象 · 同一 scope 内比较";
        var groups = sourceItems.GroupBy(item => item.Eligibility + "/" + item.ConfidenceLabel, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + " " + group.Count());
        CoverageSummary = groups.Any() ? "可见分组：" + string.Join(" · ", groups) : "当前运行没有候选分项。";
        LoadComparison();
    }

    private void LoadComparison()
    {
        Comparison.Clear();
        if (!HasComparison) return;
        var current = repository.ListItems(SelectedRun!.Id).ToDictionary(item => TargetKey(item.TargetKind, item.TargetId), StringComparer.Ordinal);
        var compared = repository.ListItems(CompareRun!.Id).ToDictionary(item => TargetKey(item.TargetKind, item.TargetId), StringComparer.Ordinal);
        foreach (var key in current.Keys.Concat(compared.Keys).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            current.TryGetValue(key, out var currentItem);
            compared.TryGetValue(key, out var comparedItem);
            Comparison.Add(new(
                currentItem?.TargetKind ?? comparedItem!.TargetKind,
                currentItem?.TargetId ?? comparedItem!.TargetId,
                currentItem?.Eligibility.ToString() ?? "未出现在当前运行",
                Rank(currentItem?.Rank), Score(currentItem?.Score),
                comparedItem?.Eligibility.ToString() ?? "未出现在对比运行",
                Rank(comparedItem?.Rank), Score(comparedItem?.Score),
                RankChange(currentItem?.Rank, comparedItem?.Rank), ScoreChange(currentItem?.Score, comparedItem?.Score)));
        }
    }

    private int SnapshotCount(string runId)
    {
        try
        {
            using var document = JsonDocument.Parse(repository.GetCandidateSnapshot(runId));
            var root = document.RootElement;
            if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array) return targets.GetArrayLength();
            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array) return candidates.GetArrayLength();
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        return repository.ListItems(runId).Count;
    }

    private static RecommendationItemRow ToRow(RecommendationItemRecord item)
        => new(item.TargetKind, item.TargetId, item.Eligibility.ToString(), item.Rank, Score(item.Score),
            $"{item.ScoreLower:0.##}—{item.ScoreUpper:0.##}", item.ConfidenceLabel.ToString(), FormatComponents(item.ComponentsJson),
            FormatStringArray(item.EvidenceIdsJson, "无"), FormatStringArray(item.MissingFactsJson, "无"), FormatStringArray(item.ReasonsJson, "无"),
            item.SourceQuality, item.DisplayOrder);

    private static string FormatComponents(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return "未知";
            var values = document.RootElement.EnumerateArray().Select(component =>
            {
                var key = component.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : "?";
                var score = component.TryGetProperty("score", out var scoreValue) && scoreValue.ValueKind != JsonValueKind.Null ? scoreValue.ToString() : "未知";
                var level = component.TryGetProperty("evaluationLevel", out var levelValue) ? levelValue.GetString() : null;
                return key + "=" + score + (string.IsNullOrWhiteSpace(level) ? "" : " (" + level + ")");
            });
            return string.Join(" · ", values);
        }
        catch (JsonException) { return "无法解析分项"; }
    }

    private static string FormatStringArray(string json, string empty)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return values.Length == 0 ? empty : string.Join(" · ", values);
        }
        catch (JsonException) { return "无法解析"; }
    }

    private static string Score(decimal? value) => value is null ? "未知" : value.Value.ToString("0.##");
    private static string Rank(int? value) => value?.ToString() ?? "—";
    private static string RankChange(int? current, int? compared) => current is null || compared is null ? "—" : (compared.Value - current.Value).ToString("+0;-0;0");
    private static string ScoreChange(decimal? current, decimal? compared) => current is null || compared is null ? "—" : (current.Value - compared.Value).ToString("+0.##;-0.##;0");
    private static string TargetKey(string kind, string id) => kind + ":" + id;

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}
