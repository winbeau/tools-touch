CREATE TABLE AdmissionCaseSample (
 Id TEXT PRIMARY KEY,
 DepartmentId TEXT NOT NULL REFERENCES Department(Id),
 ProgramId TEXT REFERENCES AdmissionProgram(Id),
 CycleYear INTEGER CHECK(CycleYear IS NULL OR (CycleYear BETWEEN 1900 AND 2200)),
 RoundKind TEXT NOT NULL,
 OutcomeStage TEXT NOT NULL,
 BackgroundJson TEXT NOT NULL,
 ClaimId TEXT NOT NULL REFERENCES EvidenceClaim(Id),
 DedupGroup TEXT NOT NULL,
 ConsentOrigin TEXT NOT NULL,
 CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_AdmissionCaseSample_Group
ON AdmissionCaseSample(DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage);
CREATE INDEX IX_AdmissionCaseSample_Dedup
ON AdmissionCaseSample(DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage,DedupGroup);

CREATE TABLE CohortStatistic (
 Id TEXT PRIMARY KEY,
 DepartmentId TEXT NOT NULL REFERENCES Department(Id),
 ProgramId TEXT REFERENCES AdmissionProgram(Id),
 CycleYear INTEGER NOT NULL CHECK(CycleYear BETWEEN 1900 AND 2200),
 RoundKind TEXT NOT NULL,
 OutcomeStage TEXT NOT NULL,
 Metric TEXT NOT NULL,
 DefinitionVersion TEXT NOT NULL,
 Numerator INTEGER,
 Denominator INTEGER NOT NULL CHECK(Denominator >= 0),
 UnknownCount INTEGER NOT NULL CHECK(UnknownCount >= 0),
 DistributionJson TEXT NOT NULL,
 SampleIdsJson TEXT NOT NULL,
 BuiltAt TEXT NOT NULL
);
CREATE INDEX IX_CohortStatistic_Group
ON CohortStatistic(DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage,Metric,BuiltAt DESC);
