CREATE TABLE PaperReadSegment (
 Id TEXT PRIMARY KEY,
 PaperId TEXT NOT NULL REFERENCES Paper(Id),
 ContentHash TEXT,
 StartPage INTEGER,
 EndPage INTEGER,
 ReadScope TEXT NOT NULL CHECK(ReadScope IN ('Abstract','Pages','FullText','Unreadable')),
 ExtractorVersion TEXT NOT NULL,
 TextArtifactId TEXT REFERENCES Artifact(Id),
 CreatedAt TEXT NOT NULL,
 CHECK(StartPage IS NULL OR StartPage >= 0),
 CHECK(EndPage IS NULL OR EndPage >= 0),
 CHECK(StartPage IS NULL OR EndPage IS NULL OR EndPage >= StartPage)
);
CREATE UNIQUE INDEX UX_PaperReadSegment_Identity
ON PaperReadSegment(PaperId,COALESCE(ContentHash,''),COALESCE(StartPage,-1),COALESCE(EndPage,-1),ReadScope,ExtractorVersion);

CREATE TABLE PaperAttributionResolution (
 Id TEXT PRIMARY KEY,
 PaperId TEXT NOT NULL REFERENCES Paper(Id),
 ProfessorId TEXT NOT NULL REFERENCES Professor(Id),
 AuthorMatchesJson TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Pending','Resolved','Rejected')),
 Reason TEXT NOT NULL,
 CreatedAt TEXT NOT NULL,
 ResolvedAt TEXT
);
CREATE INDEX IX_PaperAttributionResolution_State
ON PaperAttributionResolution(State,CreatedAt);

CREATE TABLE ProfessorEvaluation (
 Id TEXT PRIMARY KEY,
 ProfessorId TEXT NOT NULL REFERENCES Professor(Id),
 ClaimId TEXT NOT NULL REFERENCES EvidenceClaim(Id),
 SourceType TEXT NOT NULL,
 PostedAt TEXT,
 Summary TEXT NOT NULL,
 TopicsJson TEXT NOT NULL,
 VerificationState TEXT NOT NULL CHECK(VerificationState IN ('Unverified','Conflicting','UserVerified')),
 CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_ProfessorEvaluation_Professor
ON ProfessorEvaluation(ProfessorId,CreatedAt DESC);
