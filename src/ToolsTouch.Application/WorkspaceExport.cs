namespace ToolsTouch.Application;

public sealed record WorkspaceExportRequest(
    string Scope,
    string Format,
    string OutputPath,
    string? ViewId = null,
    string? CollectionId = null,
    IReadOnlyList<string>? SchoolIds = null,
    bool IncludeHistory = true,
    bool IncludeAttachments = false,
    int MaxRowsPerSheet = 1_000_000);

public sealed record WorkspaceExportResult(
    string OutputPath,
    string Format,
    string WorkspaceId,
    long DataRevision,
    int TableCount,
    long RowCount);

public sealed record WorkspaceRestoreResult(
    string DirectoryPath,
    string DatabasePath,
    string WorkspaceId,
    long DataRevision,
    long RowCount);

public interface IWorkspaceExportService
{
    WorkspaceExportResult Export(WorkspaceExportRequest request, CancellationToken cancellationToken = default);
    WorkspaceRestoreResult RestoreToNewWorkspace(string bundlePath, string destinationDirectory, CancellationToken cancellationToken = default);
}
