CREATE TABLE Artifact (
 Id TEXT PRIMARY KEY,
 Kind TEXT NOT NULL,
 RelativePath TEXT NOT NULL UNIQUE,
 ContentHash TEXT NOT NULL UNIQUE,
 ByteLength INTEGER NOT NULL CHECK(ByteLength >= 0),
 MimeType TEXT NOT NULL,
 CreatedAt TEXT NOT NULL
);

CREATE TABLE SourceSnapshot (
 Id TEXT PRIMARY KEY,
 SourceKey TEXT NOT NULL,
 OriginalUrl TEXT,
 CanonicalUrl TEXT,
 ExternalRecordId TEXT,
 ContentHash TEXT NOT NULL,
 FetchedAt TEXT NOT NULL,
 PublishedAt TEXT,
 SourceYear INTEGER CHECK(SourceYear IS NULL OR (SourceYear BETWEEN 1900 AND 2200)),
 Transport TEXT NOT NULL,
 ArtifactId TEXT NOT NULL REFERENCES Artifact(Id),
 ParseVersion TEXT NOT NULL,
 Title TEXT
);
CREATE INDEX IX_SourceSnapshot_Identity ON SourceSnapshot(SourceKey,ExternalRecordId,FetchedAt);
CREATE INDEX IX_SourceSnapshot_Hash ON SourceSnapshot(ContentHash);

CREATE TABLE EvidenceClaim (
 Id TEXT PRIMARY KEY,
 SnapshotId TEXT NOT NULL REFERENCES SourceSnapshot(Id),
 SubjectKind TEXT NOT NULL,
 SubjectId TEXT NOT NULL,
 ClaimType TEXT NOT NULL,
 ValueJson TEXT NOT NULL,
 QuotedText TEXT,
 LocatorJson TEXT,
 OriginKind TEXT NOT NULL CHECK(OriginKind IN ('OfficialFact','ThirdPartyReport','UserNote','ModelInference')),
 ConfidenceLabel TEXT NOT NULL,
 ValidFrom TEXT,
 ValidUntil TEXT,
 SupersedesId TEXT REFERENCES EvidenceClaim(Id),
 CHECK(ValidFrom IS NULL OR ValidUntil IS NULL OR ValidUntil >= ValidFrom)
);
CREATE INDEX IX_EvidenceClaim_Subject ON EvidenceClaim(SubjectKind,SubjectId,ClaimType,Id);
CREATE INDEX IX_EvidenceClaim_Snapshot ON EvidenceClaim(SnapshotId);

CREATE TABLE ClaimSupport (
 ClaimId TEXT NOT NULL REFERENCES EvidenceClaim(Id),
 SnapshotId TEXT NOT NULL REFERENCES SourceSnapshot(Id),
 LocatorJson TEXT,
 PRIMARY KEY(ClaimId,SnapshotId)
);

CREATE TABLE SourceCheck (
 Id TEXT PRIMARY KEY,
 SnapshotId TEXT NOT NULL REFERENCES SourceSnapshot(Id),
 CheckedAt TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Fetched','Unchanged','Changed','Failed')),
 ETag TEXT,
 LastModified TEXT,
 Error TEXT
);
CREATE INDEX IX_SourceCheck_Snapshot ON SourceCheck(SnapshotId,CheckedAt DESC);

CREATE TABLE EvidenceConflict (
 Id TEXT PRIMARY KEY,
 SubjectKind TEXT NOT NULL,
 SubjectId TEXT NOT NULL,
 ClaimType TEXT NOT NULL,
 ClaimIdsJson TEXT NOT NULL,
 Resolution TEXT,
 ResolvedBy TEXT,
 ResolvedAt TEXT
);
CREATE INDEX IX_EvidenceConflict_Subject ON EvidenceConflict(SubjectKind,SubjectId,ClaimType);

CREATE TABLE WindowObservation (
 Id TEXT PRIMARY KEY,
 RoundId TEXT NOT NULL REFERENCES AdmissionRound(Id),
 SourceSnapshotId TEXT NOT NULL REFERENCES SourceSnapshot(Id),
 SourceYear INTEGER CHECK(SourceYear IS NULL OR (SourceYear BETWEEN 1900 AND 2200)),
 CycleYear INTEGER CHECK(CycleYear IS NULL OR (CycleYear BETWEEN 1900 AND 2200)),
 EntryYear INTEGER CHECK(EntryYear IS NULL OR (EntryYear BETWEEN 1900 AND 2200)),
 YearBasis TEXT NOT NULL,
 DateBasis TEXT NOT NULL,
 RegistrationStartRaw TEXT,
 RegistrationEndRaw TEXT,
 EventStartRaw TEXT,
 EventEndRaw TEXT,
 RegistrationStartLocalDate TEXT,
 RegistrationEndLocalDate TEXT,
 EventStartLocalDate TEXT,
 EventEndLocalDate TEXT,
 RegistrationStartInstant TEXT,
 RegistrationEndInstant TEXT,
 EventStartInstant TEXT,
 EventEndInstant TEXT,
 Precision TEXT NOT NULL CHECK(Precision IN ('Date','Instant','Approximate','Unknown')),
 TimeZone TEXT,
 ObservedAt TEXT NOT NULL,
 SupersedesId TEXT REFERENCES WindowObservation(Id)
);
CREATE INDEX IX_WindowObservation_Round ON WindowObservation(RoundId,ObservedAt DESC);

CREATE TABLE HistoricalProjection (
 Id TEXT PRIMARY KEY,
 ProgramId TEXT NOT NULL REFERENCES AdmissionProgram(Id),
 TargetCycleYear INTEGER NOT NULL CHECK(TargetCycleYear BETWEEN 1900 AND 2200),
 Kind TEXT NOT NULL,
 RoundKey TEXT NOT NULL,
 SourceObservationId TEXT NOT NULL REFERENCES WindowObservation(Id),
 ProjectedStart TEXT,
 ProjectedEndExclusive TEXT,
 MethodVersion TEXT NOT NULL,
 GeneratedAt TEXT NOT NULL,
 ProjectionIssue TEXT
);
CREATE UNIQUE INDEX UX_HistoricalProjection_Source ON HistoricalProjection(SourceObservationId,TargetCycleYear,MethodVersion);

CREATE TABLE ObservationOverride (
 Id TEXT PRIMARY KEY,
 SubjectId TEXT NOT NULL,
 FieldKey TEXT NOT NULL,
 ValueJson TEXT NOT NULL,
 BasedOnClaimId TEXT REFERENCES EvidenceClaim(Id),
 Revision INTEGER NOT NULL CHECK(Revision > 0),
 Reason TEXT NOT NULL,
 CreatedAt TEXT NOT NULL,
 RevokedAt TEXT
);
CREATE INDEX IX_ObservationOverride_Subject ON ObservationOverride(SubjectId,FieldKey,RevokedAt,Revision DESC);

ALTER TABLE UserProfile ADD COLUMN StructuredFactsJson TEXT NOT NULL DEFAULT '{}';
ALTER TABLE UserProfile ADD COLUMN ExtractionVersion TEXT;
ALTER TABLE UserProfile ADD COLUMN ConfirmedAt TEXT;

CREATE TABLE PreferenceRevision (
 Id TEXT PRIMARY KEY,
 Version INTEGER NOT NULL UNIQUE,
 PromptText TEXT,
 ParsedConstraintsJson TEXT NOT NULL,
 UserConfirmed INTEGER NOT NULL DEFAULT 0 CHECK(UserConfirmed IN (0,1)),
 CreatedAt TEXT NOT NULL
);
