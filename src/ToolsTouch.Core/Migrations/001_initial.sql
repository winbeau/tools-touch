CREATE TABLE Professor (
 Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Institution TEXT NOT NULL,
 Homepage TEXT NOT NULL UNIQUE, Email TEXT, EvidenceJson TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL
);
CREATE TABLE Paper (
 Id TEXT PRIMARY KEY, ExternalId TEXT UNIQUE, Title TEXT NOT NULL,
 AuthorsJson TEXT NOT NULL, Year INTEGER, Abstract TEXT, SourceUrl TEXT NOT NULL,
 LocalPath TEXT, ContentHash TEXT, ParseStatus TEXT NOT NULL DEFAULT 'Pending'
);
CREATE TABLE ProfessorPaper (
 ProfessorId TEXT NOT NULL REFERENCES Professor(Id),
 PaperId TEXT NOT NULL REFERENCES Paper(Id), PRIMARY KEY (ProfessorId, PaperId)
);
CREATE TABLE UserProfile (
 Id TEXT PRIMARY KEY, Version INTEGER NOT NULL UNIQUE, CvPath TEXT NOT NULL,
 CvHash TEXT NOT NULL, ExperiencesJson TEXT NOT NULL, Confirmed INTEGER NOT NULL DEFAULT 0,
 CreatedAt TEXT NOT NULL
);
CREATE TABLE ProfessorAnalysis (
 Id TEXT PRIMARY KEY, ProfessorId TEXT NOT NULL REFERENCES Professor(Id),
 ProfileId TEXT REFERENCES UserProfile(Id), ContentJson TEXT NOT NULL,
 Model TEXT NOT NULL, CreatedAt TEXT NOT NULL
);
CREATE TABLE Outreach (
 Id TEXT PRIMARY KEY, ProfessorId TEXT NOT NULL REFERENCES Professor(Id),
 Version INTEGER NOT NULL, Recipient TEXT NOT NULL, Subject TEXT NOT NULL, Body TEXT NOT NULL,
 CvPath TEXT, CvHash TEXT, State TEXT NOT NULL CHECK(State IN ('Draft','Sending','Sent','Failed','Unknown')),
 SnapshotJson TEXT, MessageId TEXT, ThreadId TEXT, Error TEXT,
 IdempotencyKey TEXT NOT NULL UNIQUE, CreatedAt TEXT NOT NULL,
 UNIQUE(ProfessorId, Version)
);
CREATE TABLE EmailThread (
 Account TEXT NOT NULL, ThreadId TEXT NOT NULL, OutreachId TEXT NOT NULL REFERENCES Outreach(Id),
 ReplyState TEXT NOT NULL DEFAULT 'NoReply', SyncedAt TEXT,
 PRIMARY KEY(Account, ThreadId)
);
CREATE TABLE AgentRun (
 Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, InputJson TEXT NOT NULL,
 State TEXT NOT NULL, Stage TEXT NOT NULL, CheckpointJson TEXT,
 BudgetJson TEXT NOT NULL, Error TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL
);
CREATE TABLE AgentRunEvent (
 RunId TEXT NOT NULL REFERENCES AgentRun(Id), Sequence INTEGER NOT NULL,
 Type TEXT NOT NULL, PayloadJson TEXT NOT NULL, CreatedAt TEXT NOT NULL,
 PRIMARY KEY(RunId, Sequence)
);
CREATE TABLE ToolWrite (
 IdempotencyKey TEXT PRIMARY KEY, Tool TEXT NOT NULL, InputHash TEXT NOT NULL, ResultJson TEXT NOT NULL
);
