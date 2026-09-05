using ToolsTouch.Core;

namespace ToolsTouch.Application;

public interface ISourceRepository
{
    SourceSnapshot SaveSnapshot(SourceSnapshot snapshot, ReadOnlyMemory<byte> content, string artifactKind, string mimeType, string extension = ".bin");
    EvidenceClaim AddClaim(EvidenceClaim claim, IReadOnlyList<ClaimSupport>? supports = null);
    SourceCheck AddSourceCheck(SourceCheck check);
    EvidenceConflict AddConflict(EvidenceConflict conflict);
    WindowObservation AddObservation(WindowObservation observation);
    HistoricalProjection AddProjection(HistoricalProjection projection);
    ObservationOverride AddOverride(ObservationOverride observationOverride);
    void RevokeOverride(string id, long revision);
    PreferenceRevision AddPreferenceRevision(PreferenceRevision preference);
}
