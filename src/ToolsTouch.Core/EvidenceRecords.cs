namespace ToolsTouch.Core;

public sealed record Artifact
{
    public Artifact(string id, string kind, string relativePath, string contentHash, long byteLength,
        string mimeType, DateTimeOffset createdAt)
    {
        Id = School.Required(id, nameof(id));
        Kind = School.Required(kind, nameof(kind));
        RelativePath = SafeRelativePath(relativePath);
        ContentHash = School.Required(contentHash, nameof(contentHash));
        ByteLength = byteLength >= 0 ? byteLength : throw new ArgumentOutOfRangeException(nameof(byteLength));
        MimeType = School.Required(mimeType, nameof(mimeType));
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string Kind { get; }
    public string RelativePath { get; }
    public string ContentHash { get; }
    public long ByteLength { get; }
    public string MimeType { get; }
    public DateTimeOffset CreatedAt { get; }

    private static string SafeRelativePath(string value)
    {
        value = School.Required(value, nameof(value)).Replace('\\', '/');
        if (value.StartsWith('/') || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Artifact path must be relative and normalized.", nameof(value));
        return value;
    }
}

public sealed record SourceSnapshot
{
    public SourceSnapshot(string id, string sourceKey, string? originalUrl, string? canonicalUrl,
        string? externalRecordId, string contentHash, DateTimeOffset fetchedAt, DateTimeOffset? publishedAt,
        int? sourceYear, string transport, string parseVersion, string? artifactId = null, string? title = null)
    {
        Id = School.Required(id, nameof(id));
        SourceKey = School.Required(sourceKey, nameof(sourceKey));
        OriginalUrl = OptionalUrl(originalUrl, nameof(originalUrl));
        CanonicalUrl = OptionalUrl(canonicalUrl, nameof(canonicalUrl));
        ExternalRecordId = School.Optional(externalRecordId);
        ContentHash = School.Required(contentHash, nameof(contentHash));
        FetchedAt = fetchedAt;
        PublishedAt = publishedAt;
        SourceYear = sourceYear is null or >= 1900 and <= 2200 ? sourceYear : throw new ArgumentOutOfRangeException(nameof(sourceYear));
        Transport = School.Required(transport, nameof(transport));
        ParseVersion = School.Required(parseVersion, nameof(parseVersion));
        ArtifactId = School.Optional(artifactId);
        Title = School.Optional(title);
    }

    public string Id { get; }
    public string SourceKey { get; }
    public string? OriginalUrl { get; }
    public string? CanonicalUrl { get; }
    public string? ExternalRecordId { get; }
    public string ContentHash { get; }
    public DateTimeOffset FetchedAt { get; }
    public DateTimeOffset? PublishedAt { get; }
    public int? SourceYear { get; }
    public string Transport { get; }
    public string ParseVersion { get; }
    public string? ArtifactId { get; }
    public string? Title { get; }

    private static string? OptionalUrl(string? value, string name)
    {
        value = School.Optional(value);
        if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Only an HTTP(S) URL is allowed.", name);
        return value;
    }
}

public sealed record EvidenceClaim
{
    public EvidenceClaim(string id, string snapshotId, string subjectKind, string subjectId, string claimType,
        string valueJson, string? quotedText, string? locatorJson, string originKind, string confidenceLabel,
        DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null, string? supersedesId = null)
    {
        Id = School.Required(id, nameof(id));
        SnapshotId = School.Required(snapshotId, nameof(snapshotId));
        SubjectKind = School.Required(subjectKind, nameof(subjectKind));
        SubjectId = School.Required(subjectId, nameof(subjectId));
        ClaimType = School.Required(claimType, nameof(claimType));
        ValueJson = School.Required(valueJson, nameof(valueJson));
        QuotedText = School.Optional(quotedText);
        LocatorJson = School.Optional(locatorJson);
        OriginKind = originKind is "OfficialFact" or "ThirdPartyReport" or "UserNote" or "ModelInference"
            ? originKind : throw new ArgumentException("Unknown evidence origin.", nameof(originKind));
        ConfidenceLabel = School.Required(confidenceLabel, nameof(confidenceLabel));
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        SupersedesId = School.Optional(supersedesId);
        if (validFrom is not null && validUntil is not null && validUntil < validFrom)
            throw new ArgumentException("Validity end must not precede validity start.", nameof(validUntil));
    }

    public string Id { get; }
    public string SnapshotId { get; }
    public string SubjectKind { get; }
    public string SubjectId { get; }
    public string ClaimType { get; }
    public string ValueJson { get; }
    public string? QuotedText { get; }
    public string? LocatorJson { get; }
    public string OriginKind { get; }
    public string ConfidenceLabel { get; }
    public DateTimeOffset? ValidFrom { get; }
    public DateTimeOffset? ValidUntil { get; }
    public string? SupersedesId { get; }
}

public sealed record ClaimSupport(string ClaimId, string SnapshotId, string? LocatorJson);

public sealed record SourceCheck(string Id, string SnapshotId, DateTimeOffset CheckedAt, string State,
    string? ETag = null, string? LastModified = null, string? Error = null);

public sealed record EvidenceConflict(string Id, string SubjectKind, string SubjectId, string ClaimType,
    string ClaimIdsJson, string? Resolution = null, string? ResolvedBy = null,
    DateTimeOffset? ResolvedAt = null);

public sealed record ObservationOverride(string Id, string SubjectId, string FieldKey, string ValueJson,
    string? BasedOnClaimId, long Revision, string Reason, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt = null);

public enum DatePrecision
{
    Date,
    Instant,
    Approximate,
    Unknown
}

public sealed record WindowObservation
{
    public WindowObservation(string id, string roundId, string sourceSnapshotId, int? sourceYear, int? cycleYear, int? entryYear,
        string yearBasis, string dateBasis, string? registrationStartRaw, string? registrationEndRaw, string? eventStartRaw,
        string? eventEndRaw, string? registrationStartLocalDate, string? registrationEndLocalDate, string? eventStartLocalDate,
        string? eventEndLocalDate, string? registrationStartInstant, string? registrationEndInstant, string? eventStartInstant,
        string? eventEndInstant, DatePrecision precision, string? timeZone, DateTimeOffset observedAt, string? supersedesId = null)
    {
        Id = School.Required(id, nameof(id));
        RoundId = School.Required(roundId, nameof(roundId));
        SourceSnapshotId = School.Required(sourceSnapshotId, nameof(sourceSnapshotId));
        SourceYear = Year(sourceYear, nameof(sourceYear));
        CycleYear = Year(cycleYear, nameof(cycleYear));
        EntryYear = Year(entryYear, nameof(entryYear));
        YearBasis = School.Required(yearBasis, nameof(yearBasis));
        DateBasis = School.Required(dateBasis, nameof(dateBasis));
        RegistrationStartRaw = School.Optional(registrationStartRaw);
        RegistrationEndRaw = School.Optional(registrationEndRaw);
        EventStartRaw = School.Optional(eventStartRaw);
        EventEndRaw = School.Optional(eventEndRaw);
        RegistrationStartLocalDate = School.Optional(registrationStartLocalDate);
        RegistrationEndLocalDate = School.Optional(registrationEndLocalDate);
        EventStartLocalDate = School.Optional(eventStartLocalDate);
        EventEndLocalDate = School.Optional(eventEndLocalDate);
        RegistrationStartInstant = School.Optional(registrationStartInstant);
        RegistrationEndInstant = School.Optional(registrationEndInstant);
        EventStartInstant = School.Optional(eventStartInstant);
        EventEndInstant = School.Optional(eventEndInstant);
        Precision = precision;
        TimeZone = School.Optional(timeZone);
        ObservedAt = observedAt;
        SupersedesId = School.Optional(supersedesId);
    }

    public string Id { get; }
    public string RoundId { get; }
    public string SourceSnapshotId { get; }
    public int? SourceYear { get; }
    public int? CycleYear { get; }
    public int? EntryYear { get; }
    public string YearBasis { get; }
    public string DateBasis { get; }
    public string? RegistrationStartRaw { get; }
    public string? RegistrationEndRaw { get; }
    public string? EventStartRaw { get; }
    public string? EventEndRaw { get; }
    public string? RegistrationStartLocalDate { get; }
    public string? RegistrationEndLocalDate { get; }
    public string? EventStartLocalDate { get; }
    public string? EventEndLocalDate { get; }
    public string? RegistrationStartInstant { get; }
    public string? RegistrationEndInstant { get; }
    public string? EventStartInstant { get; }
    public string? EventEndInstant { get; }
    public DatePrecision Precision { get; }
    public string? TimeZone { get; }
    public DateTimeOffset ObservedAt { get; }
    public string? SupersedesId { get; }

    private static int? Year(int? value, string name) => value is null or >= 1900 and <= 2200
        ? value : throw new ArgumentOutOfRangeException(name);
}

public sealed record HistoricalProjection(string Id, string ProgramId, int TargetCycleYear, string Kind, string RoundKey,
    string SourceObservationId, string? ProjectedStart, string? ProjectedEndExclusive, string MethodVersion,
    DateTimeOffset GeneratedAt, string? ProjectionIssue);

public sealed record PreferenceRevision(string Id, int Version, string? PromptText, string ParsedConstraintsJson,
    bool UserConfirmed, DateTimeOffset CreatedAt);
