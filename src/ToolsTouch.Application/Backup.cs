namespace ToolsTouch.Application;

public sealed record BackupReport(string DirectoryPath, string WorkspaceId, long DataRevision, int ArtifactCount);
public sealed record BackupVerification(string DirectoryPath, string WorkspaceId, long DataRevision, int ArtifactCount);

public interface IWorkspaceBackup
{
    BackupReport Create(string destinationDirectory);
    BackupVerification Verify(string backupDirectory);
}
