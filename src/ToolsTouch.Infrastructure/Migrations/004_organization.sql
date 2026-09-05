CREATE TABLE WorkspaceMeta (
 Id INTEGER PRIMARY KEY CHECK(Id = 1),
 DataRevision INTEGER NOT NULL DEFAULT 0 CHECK(DataRevision >= 0),
 SchemaVersion INTEGER NOT NULL,
 WorkspaceId TEXT NOT NULL UNIQUE
);
INSERT INTO WorkspaceMeta(Id,DataRevision,SchemaVersion,WorkspaceId)
VALUES(1,0,4,lower(hex(randomblob(16))));

CREATE TABLE School (
 Id TEXT PRIMARY KEY,
 CanonicalName TEXT NOT NULL,
 ShortName TEXT NOT NULL,
 OfficialCode TEXT,
 Campus TEXT,
 City TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 ArchivedAt TEXT
);
CREATE UNIQUE INDEX IX_School_OfficialCode ON School(OfficialCode) WHERE OfficialCode IS NOT NULL;
CREATE INDEX IX_School_Name ON School(CanonicalName COLLATE NOCASE, Id);

CREATE TABLE Department (
 Id TEXT PRIMARY KEY,
 SchoolId TEXT NOT NULL REFERENCES School(Id),
 CanonicalName TEXT NOT NULL,
 OfficialCode TEXT,
 ParentDepartmentId TEXT REFERENCES Department(Id),
 Kind TEXT NOT NULL,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 ArchivedAt TEXT
);
CREATE UNIQUE INDEX UX_Department_School_Name ON Department(SchoolId,CanonicalName COLLATE NOCASE) WHERE ArchivedAt IS NULL;
CREATE INDEX IX_Department_School_Name ON Department(SchoolId,CanonicalName COLLATE NOCASE,Id);

CREATE TABLE AdmissionProgram (
 Id TEXT PRIMARY KEY,
 DepartmentId TEXT NOT NULL REFERENCES Department(Id),
 Name TEXT NOT NULL,
 DegreeType TEXT NOT NULL,
 DisciplineCode TEXT,
 Track TEXT,
 Campus TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 ArchivedAt TEXT
);
CREATE UNIQUE INDEX UX_AdmissionProgram_Identity ON AdmissionProgram(DepartmentId,Name COLLATE NOCASE,DegreeType,COALESCE(Track,'')) WHERE ArchivedAt IS NULL;
CREATE INDEX IX_AdmissionProgram_Department_Name ON AdmissionProgram(DepartmentId,Name COLLATE NOCASE,Id);

CREATE TABLE AdmissionRound (
 Id TEXT PRIMARY KEY,
 ProgramId TEXT NOT NULL REFERENCES AdmissionProgram(Id),
 CycleYear INTEGER CHECK(CycleYear IS NULL OR (CycleYear BETWEEN 1900 AND 2200)),
 EntryYear INTEGER CHECK(EntryYear IS NULL OR (EntryYear BETWEEN 1900 AND 2200)),
 Kind TEXT NOT NULL CHECK(Kind IN ('SummerCamp','PreRecommendation','Other')),
 RoundKey TEXT NOT NULL,
 Title TEXT NOT NULL,
 OfficialUrl TEXT,
 ApplicationUrl TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 ArchivedAt TEXT
);
CREATE UNIQUE INDEX UX_AdmissionRound_KnownCycle ON AdmissionRound(ProgramId,CycleYear,Kind,RoundKey) WHERE CycleYear IS NOT NULL AND ArchivedAt IS NULL;
CREATE INDEX IX_AdmissionRound_Program_Order ON AdmissionRound(ProgramId,CycleYear DESC,EntryYear DESC,Kind,RoundKey,Id);

CREATE TABLE EntityAlias (
 Id TEXT PRIMARY KEY,
 EntityKind TEXT NOT NULL,
 EntityId TEXT NOT NULL,
 Alias TEXT NOT NULL,
 SourceKey TEXT,
 ValidFrom TEXT,
 ValidTo TEXT
);
CREATE UNIQUE INDEX UX_EntityAlias_Identity ON EntityAlias(EntityKind,EntityId,Alias,COALESCE(SourceKey,''));
CREATE INDEX IX_EntityAlias_Lookup ON EntityAlias(EntityKind,Alias COLLATE NOCASE,SourceKey);

CREATE TABLE ExternalIdentity (
 SourceKey TEXT NOT NULL,
 EntityKind TEXT NOT NULL,
 ExternalId TEXT NOT NULL,
 EntityId TEXT NOT NULL,
 CreatedAt TEXT NOT NULL,
 PRIMARY KEY(SourceKey,EntityKind,ExternalId)
);
CREATE INDEX IX_ExternalIdentity_Entity ON ExternalIdentity(EntityKind,EntityId);

CREATE TABLE EntityResolution (
 Id TEXT PRIMARY KEY,
 SourceSnapshotId TEXT,
 RawSchool TEXT,
 RawDepartment TEXT,
 CandidateIdsJson TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Pending','Resolved','Rejected')),
 ResolvedBy TEXT,
 ResolvedEntityId TEXT,
 CreatedAt TEXT NOT NULL,
 ResolvedAt TEXT
);
CREATE INDEX IX_EntityResolution_State ON EntityResolution(State,CreatedAt);

CREATE TABLE Appointment (
 Id TEXT PRIMARY KEY,
 ProfessorId TEXT NOT NULL REFERENCES Professor(Id),
 DepartmentId TEXT NOT NULL REFERENCES Department(Id),
 Title TEXT,
 Role TEXT,
 StartDate TEXT,
 EndDate TEXT,
 IsPrimary INTEGER NOT NULL DEFAULT 0 CHECK(IsPrimary IN (0,1)),
 EvidenceClaimId TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 ArchivedAt TEXT,
 CHECK(StartDate IS NULL OR EndDate IS NULL OR EndDate >= StartDate)
);
CREATE INDEX IX_Appointment_Department_Professor ON Appointment(DepartmentId,ProfessorId,Id);
CREATE INDEX IX_Appointment_Professor ON Appointment(ProfessorId,Id);
