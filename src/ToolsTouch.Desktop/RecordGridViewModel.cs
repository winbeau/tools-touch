using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using ToolsTouch.Application;

namespace ToolsTouch.Desktop;

public sealed class RecordGridRow(RecordRefRecord reference, string displayValue)
{
    public string RecordId => reference.Id;
    public int Revision => reference.Revision;
    public string DisplayValue => displayValue;
    public RecordRefRecord Reference => reference;
}

public sealed class RecordGridViewModel : Observable
{
    private readonly IRecordWorkspaceCatalog catalog;
    private readonly IRecordWorkspaceService records;
    private readonly IFieldSchemaService fields;
    private readonly IRecordQueryService queries;
    private readonly IViewService views;
    private readonly IImportPreviewService imports;
    private readonly Action<Exception> reportError;
    private CollectionRecord? selectedCollection;
    private ViewDefinitionRecord? selectedView;
    private RecordGridRow? selectedRow;
    private FieldDefinitionRecord? selectedField;
    private FieldDefinitionRecord? selectedFilterField;
    private FieldDefinitionRecord? selectedSortField;
    private string editValue = "";
    private string newCollectionName = "";
    private string newFieldKey = "";
    private string newFieldName = "";
    private string newFieldType = "Text";
    private string newViewName = "";
    private string filterOperator = "contains";
    private string filterValue = "";
    private string sortDirection = "asc";
    private string status = "尚未加载自定义记录表。";
    private string importSourcePath = "";
    private string importBatchId = "";
    private string importStatus = "";

    public ObservableCollection<CollectionRecord> CustomCollections { get; } = [];
    public ObservableCollection<FieldDefinitionRecord> Fields { get; } = [];
    public ObservableCollection<ViewDefinitionRecord> Views { get; } = [];
    public ObservableCollection<RecordGridRow> Rows { get; } = [];
    public ObservableCollection<ImportRowError> ImportErrors { get; } = [];
    public IReadOnlyList<string> FieldTypes => RecordFieldTypes.All;
    public CollectionRecord? SelectedCollection
    {
        get => selectedCollection;
        set { if (!Set(ref selectedCollection, value)) return; LoadCollection(); Raise(nameof(HasSelectedCollection)); CommandManager.InvalidateRequerySuggested(); }
    }
    public ViewDefinitionRecord? SelectedView
    {
        get => selectedView;
        set { if (!Set(ref selectedView, value)) return; LoadCollection(); Raise(nameof(HasSelectedView)); CommandManager.InvalidateRequerySuggested(); }
    }
    public RecordGridRow? SelectedRow
    {
        get => selectedRow;
        set { if (!Set(ref selectedRow, value)) return; LoadEditValue(); Raise(nameof(HasSelectedRow)); CommandManager.InvalidateRequerySuggested(); }
    }
    public FieldDefinitionRecord? SelectedField
    {
        get => selectedField;
        set { if (!Set(ref selectedField, value)) return; LoadEditValue(); Raise(nameof(HasSelectedField)); CommandManager.InvalidateRequerySuggested(); }
    }
    public FieldDefinitionRecord? SelectedFilterField
    {
        get => selectedFilterField;
        set { if (!Set(ref selectedFilterField, value)) return; Raise(nameof(HasFilterField)); CommandManager.InvalidateRequerySuggested(); }
    }
    public FieldDefinitionRecord? SelectedSortField
    {
        get => selectedSortField;
        set { if (!Set(ref selectedSortField, value)) return; Raise(nameof(HasSortField)); CommandManager.InvalidateRequerySuggested(); }
    }
    public bool HasSelectedCollection => SelectedCollection is not null;
    public bool HasSelectedRow => SelectedRow is not null;
    public bool HasSelectedField => SelectedField is not null;
    public bool HasSelectedView => SelectedView is not null;
    public bool HasFilterField => SelectedFilterField is not null;
    public bool HasSortField => SelectedSortField is not null;
    public IReadOnlyList<string> FilterOperators => ["eq", "contains", "isEmpty", "gte", "lte", "range"];
    public IReadOnlyList<string> SortDirections => ["asc", "desc"];
    public string EditValue { get => editValue; set => Set(ref editValue, value); }
    public string NewCollectionName { get => newCollectionName; set => Set(ref newCollectionName, value); }
    public string NewFieldKey { get => newFieldKey; set => Set(ref newFieldKey, value); }
    public string NewFieldName { get => newFieldName; set => Set(ref newFieldName, value); }
    public string NewFieldType { get => newFieldType; set => Set(ref newFieldType, value); }
    public string NewViewName { get => newViewName; set => Set(ref newViewName, value); }
    public string FilterOperator { get => filterOperator; set => Set(ref filterOperator, value); }
    public string FilterValue { get => filterValue; set => Set(ref filterValue, value); }
    public string SortDirection { get => sortDirection; set => Set(ref sortDirection, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string ImportSourcePath { get => importSourcePath; private set => Set(ref importSourcePath, value); }
    public string ImportStatus { get => importStatus; private set => Set(ref importStatus, value); }
    public bool HasImportBatch => !string.IsNullOrWhiteSpace(importBatchId);
    public bool HasImportErrors => ImportErrors.Count != 0;
    public ICommand RefreshCommand { get; }
    public ICommand CreateCollectionCommand { get; }
    public ICommand CreateFieldCommand { get; }
    public ICommand CreateRecordCommand { get; }
    public ICommand SaveCellCommand { get; }
    public ICommand ApplyFilterCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand SaveViewCommand { get; }
    public ICommand PreviewImportCommand { get; }
    public ICommand CommitImportCommand { get; }
    public ICommand CancelImportCommand { get; }

    public RecordGridViewModel(IRecordWorkspaceCatalog catalog, IRecordWorkspaceService records, IFieldSchemaService fields,
        IRecordQueryService queries, IViewService views, IImportPreviewService imports, Action<Exception> reportError)
    {
        this.catalog = catalog; this.records = records; this.fields = fields; this.queries = queries; this.views = views; this.imports = imports; this.reportError = reportError;
        RefreshCommand = new UiCommand(() => { Refresh(); return Task.CompletedTask; }, reportError);
        CreateCollectionCommand = new UiCommand(() => { CreateCollection(); return Task.CompletedTask; }, reportError, () => !string.IsNullOrWhiteSpace(NewCollectionName));
        CreateFieldCommand = new UiCommand(() => { CreateField(); return Task.CompletedTask; }, reportError, () => HasSelectedCollection && !string.IsNullOrWhiteSpace(NewFieldKey) && !string.IsNullOrWhiteSpace(NewFieldName));
        CreateRecordCommand = new UiCommand(() => { CreateRecord(); return Task.CompletedTask; }, reportError, () => HasSelectedCollection);
        SaveCellCommand = new UiCommand(() => { SaveCell(); return Task.CompletedTask; }, reportError, () => HasSelectedRow && HasSelectedField);
        ApplyFilterCommand = new UiCommand(() => { SelectedView = null; LoadCollection(); return Task.CompletedTask; }, reportError,
            () => HasSelectedCollection && HasFilterField);
        ClearFilterCommand = new UiCommand(() => { FilterValue = ""; SelectedView = null; LoadCollection(); return Task.CompletedTask; }, reportError,
            () => HasSelectedCollection);
        SaveViewCommand = new UiCommand(() => { SaveView(); return Task.CompletedTask; }, reportError,
            () => HasSelectedCollection && !string.IsNullOrWhiteSpace(NewViewName));
        PreviewImportCommand = new UiCommand(PreviewImportAsync, reportError, () => HasSelectedCollection);
        CommitImportCommand = new UiCommand(CommitImportAsync, reportError, () => HasImportBatch);
        CancelImportCommand = new UiCommand(CancelImportAsync, reportError, () => HasImportBatch);
    }

    public void Refresh()
    {
        var selectedId = SelectedCollection?.Id;
        var selectedViewId = SelectedView?.Id;
        var collections = catalog.Collections().Where(item => item.Kind == "Custom").ToArray();
        Replace(CustomCollections, collections);
        SelectedCollection = CustomCollections.FirstOrDefault(item => item.Id == selectedId) ?? CustomCollections.FirstOrDefault();
        if (SelectedCollection is null) ClearCollection();
        else if (selectedViewId is not null) SelectedView = Views.FirstOrDefault(item => item.Id == selectedViewId);
        Status = $"已加载 {CustomCollections.Count} 张自定义表；系统集合由领域服务提供。";
    }

    private void LoadCollection()
    {
        ClearCollection();
        if (SelectedCollection is null) return;
        foreach (var field in catalog.Fields(SelectedCollection.Id)) Fields.Add(field);
        Replace(Views, views.List(SelectedCollection.Id));
        if (SelectedView is not null && Views.All(view => view.Id != SelectedView.Id))
        {
            selectedView = null;
            Raise(nameof(SelectedView)); Raise(nameof(HasSelectedView));
        }
        SelectedFilterField = Fields.FirstOrDefault(field => field.Id == SelectedFilterField?.Id) ?? Fields.FirstOrDefault();
        SelectedSortField = Fields.FirstOrDefault(field => field.Id == SelectedSortField?.Id);
        var request = new RecordQueryRequest(SelectedCollection.Id, SelectedView?.Id,
            SelectedView is null ? CurrentFilterJson() : null, SelectedView is null ? CurrentSortJson() : null, Limit: 200);
        var page = queries.Query(request);
        foreach (var row in page.Items)
            Rows.Add(new RecordGridRow(row.Record, string.Join(" · ", row.Values.Values.Where(value => value is not null).Select(FormatValue))));
        SelectedRow = Rows.FirstOrDefault(); SelectedField = Fields.FirstOrDefault(field => field.Id == SelectedField?.Id) ?? Fields.FirstOrDefault();
        Status = $"已加载 {Rows.Count} 条记录；数据版本 {page.DataRevision}。";
    }

    private void ClearCollection()
    {
        Fields.Clear(); Views.Clear(); Rows.Clear(); SelectedRow = null; SelectedField = null; SelectedFilterField = null; SelectedSortField = null;
    }

    private void LoadEditValue()
    {
        if (SelectedRow is null || SelectedField is null) { EditValue = ""; return; }
        var value = records.Values(SelectedRow.RecordId).FirstOrDefault(item => item.FieldId == SelectedField.Id);
        EditValue = value is null ? "" : value.TextValue ?? value.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? value.DateValue?.ToString("O") ?? value.BoolValue?.ToString() ?? value.JsonValue ?? string.Join(",", records.Relations(SelectedRow.RecordId, SelectedField.Id).Select(item => item.ToRecordId));
    }

    private void CreateCollection()
    {
        var created = fields.CreateCustomCollection(new CustomCollectionCreateRequest(NewCollectionName));
        NewCollectionName = ""; Refresh(); SelectedCollection = CustomCollections.Single(item => item.Id == created.Id); Status = "自定义表已创建。";
    }

    private void CreateField()
    {
        var created = fields.CreateCustomField(new CustomFieldCreateRequest(SelectedCollection!.Id, NewFieldKey, NewFieldName, NewFieldType));
        NewFieldKey = ""; NewFieldName = ""; LoadCollection(); SelectedField = Fields.Single(item => item.Id == created.Id); Status = "字段已创建。";
    }

    private void CreateRecord()
    {
        records.CreateCustomRecord(new CustomRecordCreateRequest(SelectedCollection!.Id)); LoadCollection(); Status = "记录已创建。";
    }

    private void SaveCell()
    {
        var row = SelectedRow!; var field = SelectedField!;
        var value = ParseValue(field.Type, EditValue);
        records.UpdateCell(new UpdateCellCommand(row.RecordId, field.Id, row.Revision, field.Revision, value, Guid.NewGuid().ToString("N")));
        LoadCollection(); Status = "单元格已保存。";
    }

    private void SaveView()
    {
        var saved = views.Save(new ViewSaveRequest(SelectedCollection!.Id, NewViewName, FilterAstJson: CurrentFilterJson(), SortJson: CurrentSortJson()));
        NewViewName = ""; Replace(Views, views.List(SelectedCollection.Id)); SelectedView = Views.Single(view => view.Id == saved.Id);
        Status = "视图已保存。";
    }

    private async Task PreviewImportAsync()
    {
        if (SelectedCollection is null) return;
        var dialog = new OpenFileDialog { Filter = "表格文件|*.csv;*.xlsx|CSV|*.csv|Excel|*.xlsx", Title = "选择表格并预览导入" };
        if (dialog.ShowDialog() != true) return;
        var collection = SelectedCollection;
        var path = dialog.FileName;
        var format = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xlsx" : "csv";
        var mappings = Fields.Where(field => field.ArchivedAt is null).Select(field => new ImportFieldMapping(field.Key, field.Id)).ToArray();
        var request = new ImportRequest(collection.Id, path, "desktop-import-" + Guid.NewGuid().ToString("N"), mappings, format);
        var preview = await Task.Run(() => imports.Preview(request));
        importBatchId = preview.BatchId;
        Raise(nameof(HasImportBatch));
        ImportSourcePath = path;
        Replace(ImportErrors, preview.Errors);
        Raise(nameof(HasImportErrors));
        ImportStatus = $"预览 {preview.ScannedCount} 行：可提交 {preview.AcceptedCount}，错误 {preview.RejectedCount}。";
        Status = ImportStatus;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CommitImportAsync()
    {
        if (!HasImportBatch) return;
        var result = await Task.Run(() => imports.Commit(importBatchId));
        Replace(ImportErrors, result.Errors);
        Raise(nameof(HasImportErrors));
        ImportStatus = $"导入批次 {result.State}：新增 {result.CreatedCount}，更新 {result.UpdatedCount}，错误 {result.RejectedCount}。";
        Status = ImportStatus;
        if (result.State == "Committed") { importBatchId = ""; Raise(nameof(HasImportBatch)); LoadCollection(); }
        CommandManager.InvalidateRequerySuggested();
    }

    private Task CancelImportAsync()
    {
        if (!HasImportBatch) return Task.CompletedTask;
        var result = imports.Cancel(importBatchId);
        importBatchId = "";
        Raise(nameof(HasImportBatch));
        ImportStatus = $"导入批次 {result.State}。";
        Status = ImportStatus;
        CommandManager.InvalidateRequerySuggested();
        return Task.CompletedTask;
    }

    private string CurrentFilterJson() => SelectedFilterField is null || string.IsNullOrWhiteSpace(FilterValue) || FilterOperator == "isEmpty"
        ? (SelectedFilterField is null || FilterOperator != "isEmpty" ? FilterBuilder.Empty() : FilterBuilder.Condition(SelectedFilterField.Id, FilterOperator))
        : FilterBuilder.Condition(SelectedFilterField.Id, FilterOperator, FilterValue.Trim());

    private string CurrentSortJson() => SelectedSortField is null ? "[]" : JsonSerializer.Serialize(new[]
    {
        new { field_id = SelectedSortField.Id, direction = SortDirection, nulls = "last" }
    });

    private static TypedRecordValue ParseValue(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new TypedRecordValue(type);
        return type switch
        {
            "Text" or "Url" => new(type, TextValue: value),
            "Number" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => new(type, NumberValue: number),
            "DateTime" when DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date) => new(type, DateValue: date),
            "Boolean" when bool.TryParse(value, out var boolean) => new(type, BoolValue: boolean),
            "Choice" => new(type, ChoiceIds: [value.Trim()]),
            "MultiChoice" => new(type, ChoiceIds: value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            "Relation" => new(type, RelationRecordIds: value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            _ => throw new ArgumentException("字段值格式无效。")
        };
    }

    private static string FormatValue(TypedRecordValue? value) => value is null ? "" : value.TextValue ?? value.NumberValue?.ToString(CultureInfo.InvariantCulture) ??
        value.DateValue?.ToString("yyyy-MM-dd HH:mm") ?? value.BoolValue?.ToString() ??
        (value.ChoiceIds is not null ? string.Join(",", value.ChoiceIds) : value.RelationRecordIds is not null ? string.Join(",", value.RelationRecordIds) : "");
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
}
