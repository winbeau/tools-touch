CREATE TABLE RecommendationRun (
 Id TEXT PRIMARY KEY,
 Level TEXT NOT NULL,
 ProfileId TEXT NOT NULL REFERENCES UserProfile(Id),
 PreferenceId TEXT REFERENCES PreferenceRevision(Id),
 TargetCycleYear INTEGER NOT NULL CHECK(TargetCycleYear BETWEEN 1900 AND 2200),
 AlgorithmVersion TEXT NOT NULL,
 WeightProfileJson TEXT NOT NULL,
 CandidateSnapshotArtifactId TEXT NOT NULL REFERENCES Artifact(Id),
 DatasetRevision INTEGER NOT NULL CHECK(DatasetRevision >= 0),
 AsOf TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Queued','Running','Completed','Partial','Failed','Cancelled')),
 BudgetJson TEXT NOT NULL,
 CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_RecommendationRun_Profile ON RecommendationRun(ProfileId,CreatedAt DESC);

CREATE TABLE RecommendationItem (
 RunId TEXT NOT NULL REFERENCES RecommendationRun(Id) ON DELETE CASCADE,
 TargetKind TEXT NOT NULL CHECK(TargetKind IN ('DepartmentProgram','ProfessorAppointment')),
 TargetId TEXT NOT NULL,
 Eligibility TEXT NOT NULL CHECK(Eligibility IN ('Eligible','Ineligible','NeedsVerification')),
 Rank INTEGER,
 Score REAL,
 ScoreLower REAL NOT NULL,
 ScoreUpper REAL NOT NULL,
 ConfidenceLabel TEXT NOT NULL CHECK(ConfidenceLabel IN ('Sufficient','Insufficient')),
 ComponentsJson TEXT NOT NULL,
 ReasonsJson TEXT NOT NULL,
 MissingFactsJson TEXT NOT NULL,
 EvidenceIdsJson TEXT NOT NULL,
 SourceQuality INTEGER NOT NULL CHECK(SourceQuality >= 0),
 DisplayOrder INTEGER,
 PRIMARY KEY(RunId,TargetKind,TargetId)
);
CREATE INDEX IX_RecommendationItem_Order ON RecommendationItem(RunId,Eligibility,DisplayOrder,TargetId);
