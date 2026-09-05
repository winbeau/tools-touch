CREATE TABLE ApplicationCase (
 Id TEXT PRIMARY KEY,
 DepartmentId TEXT NOT NULL REFERENCES Department(Id),
 ProgramId TEXT REFERENCES AdmissionProgram(Id),
 RoundId TEXT REFERENCES AdmissionRound(Id),
 ProfessorId TEXT REFERENCES Professor(Id),
 CycleYear INTEGER NOT NULL CHECK(CycleYear BETWEEN 1900 AND 2200),
 DegreeType TEXT NOT NULL,
 Stage TEXT NOT NULL CHECK(Stage IN ('Interested','Preparing','Submitted','Interview','Waitlisted','Offer','Rejected','Withdrawn','Archived')),
 Priority INTEGER NOT NULL DEFAULT 0 CHECK(Priority BETWEEN 0 AND 5),
 ApplicationUrl TEXT,
 SubmittedAt TEXT,
 SubmissionReference TEXT,
 ReceiptArtifactId TEXT REFERENCES Artifact(Id),
 NextStep TEXT,
 NextStepDueAt TEXT,
 OwnerNote TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX UX_ApplicationCase_Identity ON ApplicationCase(
 DepartmentId,CycleYear,COALESCE(ProgramId,''),COALESCE(RoundId,''),COALESCE(ProfessorId,'')
);
CREATE INDEX IX_ApplicationCase_List ON ApplicationCase(CycleYear DESC,Stage,Priority DESC,DepartmentId,Id);

CREATE TABLE ApplicationEvent (
 Id TEXT PRIMARY KEY,
 CaseId TEXT NOT NULL REFERENCES ApplicationCase(Id) ON DELETE CASCADE,
 Type TEXT NOT NULL,
 PreviousStage TEXT,
 NextStage TEXT,
 RelatedOutreachId TEXT REFERENCES Outreach(Id),
 EvidenceArtifactId TEXT REFERENCES Artifact(Id),
 OccurredAt TEXT NOT NULL,
 RecordedAt TEXT NOT NULL,
 Note TEXT
);
CREATE INDEX IX_ApplicationEvent_Case ON ApplicationEvent(CaseId,RecordedAt DESC,Id DESC);

CREATE TABLE CaseOutreach (
 CaseId TEXT NOT NULL REFERENCES ApplicationCase(Id) ON DELETE CASCADE,
 OutreachId TEXT NOT NULL REFERENCES Outreach(Id) ON DELETE CASCADE,
 CreatedAt TEXT NOT NULL,
 PRIMARY KEY(CaseId,OutreachId)
);
CREATE INDEX IX_CaseOutreach_Outreach ON CaseOutreach(OutreachId,CaseId);

CREATE TABLE ApplicationMaterial (
 Id TEXT PRIMARY KEY,
 CaseId TEXT NOT NULL REFERENCES ApplicationCase(Id) ON DELETE CASCADE,
 Name TEXT NOT NULL,
 IsRequired INTEGER NOT NULL DEFAULT 1 CHECK(IsRequired IN (0,1)),
 State TEXT NOT NULL CHECK(State IN ('Missing','Ready','Submitted','NotApplicable')),
 ArtifactId TEXT REFERENCES Artifact(Id),
 Note TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 UNIQUE(CaseId,Name COLLATE NOCASE)
);
CREATE INDEX IX_ApplicationMaterial_Case ON ApplicationMaterial(CaseId,State,Name COLLATE NOCASE);

CREATE TABLE Reminder (
 Id TEXT PRIMARY KEY,
 CaseId TEXT REFERENCES ApplicationCase(Id) ON DELETE CASCADE,
 RoundId TEXT REFERENCES AdmissionRound(Id) ON DELETE CASCADE,
 Kind TEXT NOT NULL,
 DueAt TEXT NOT NULL,
 Basis TEXT NOT NULL,
 State TEXT NOT NULL DEFAULT 'Pending' CHECK(State IN ('Pending','Completed','Dismissed')),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 CHECK(CaseId IS NOT NULL OR RoundId IS NOT NULL)
);
CREATE INDEX IX_Reminder_Due ON Reminder(State,DueAt,Id);
CREATE INDEX IX_Reminder_Case ON Reminder(CaseId,State,DueAt);
