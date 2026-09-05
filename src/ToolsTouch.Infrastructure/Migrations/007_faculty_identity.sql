PRAGMA foreign_keys=OFF;
CREATE TABLE Professor_v7 (
 Id TEXT PRIMARY KEY,
 Name TEXT NOT NULL,
 Institution TEXT NOT NULL,
 Homepage TEXT UNIQUE,
 Email TEXT,
 EvidenceJson TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL
);
INSERT INTO Professor_v7(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt)
SELECT Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt FROM Professor;
DROP TABLE Professor;
ALTER TABLE Professor_v7 RENAME TO Professor;
PRAGMA foreign_keys=ON;

CREATE TABLE FacultyIdentityResolution (
 Id TEXT PRIMARY KEY,
 CrawlBatchId TEXT REFERENCES CrawlBatch(Id) ON DELETE SET NULL,
 SourceSnapshotId TEXT REFERENCES SourceSnapshot(Id),
 SourceKey TEXT NOT NULL,
 ExternalId TEXT,
 RawName TEXT NOT NULL,
 RawHomepage TEXT,
 CandidateIdsJson TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Pending','Resolved','Rejected')),
 Reason TEXT NOT NULL,
 ResolvedProfessorId TEXT REFERENCES Professor(Id),
 CreatedAt TEXT NOT NULL,
 ResolvedAt TEXT
);
CREATE INDEX IX_FacultyIdentityResolution_State
ON FacultyIdentityResolution(State,CreatedAt);
CREATE INDEX IX_FacultyIdentityResolution_External
ON FacultyIdentityResolution(SourceKey,ExternalId);
