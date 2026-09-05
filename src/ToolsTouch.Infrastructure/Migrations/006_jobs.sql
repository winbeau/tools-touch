ALTER TABLE AgentRun ADD COLUMN ParentRunId TEXT REFERENCES AgentRun(Id);
ALTER TABLE AgentRun ADD COLUMN Provider TEXT;
ALTER TABLE AgentRun ADD COLUMN Model TEXT;
ALTER TABLE AgentRun ADD COLUMN MaxAttempts INTEGER NOT NULL DEFAULT 3 CHECK(MaxAttempts BETWEEN 1 AND 10);
ALTER TABLE AgentRun ADD COLUMN LeaseOwner TEXT;
ALTER TABLE AgentRun ADD COLUMN LeaseExpiresAt TEXT;
ALTER TABLE AgentRun ADD COLUMN WaitingReason TEXT;

ALTER TABLE AgentRunEvent ADD COLUMN AttemptId TEXT REFERENCES JobAttempt(Id);
ALTER TABLE AgentRunEvent ADD COLUMN AttemptSequence INTEGER;

CREATE TABLE JobStage (
 Id TEXT PRIMARY KEY,
 RunId TEXT NOT NULL REFERENCES AgentRun(Id) ON DELETE CASCADE,
 StageKey TEXT NOT NULL,
 Ordinal INTEGER NOT NULL CHECK(Ordinal >= 0),
 State TEXT NOT NULL CHECK(State IN ('Queued','Running','WaitingForAuth','RetryScheduled','Partial','Completed','Failed','Cancelled','Interrupted')),
 AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount >= 0),
 CheckpointJson TEXT,
 OutputJson TEXT,
 Error TEXT,
 NextAttemptAt TEXT,
 LeaseOwner TEXT,
 LeaseExpiresAt TEXT,
 StableKey TEXT NOT NULL,
 InputHash TEXT NOT NULL,
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 UNIQUE(RunId,StageKey),
 UNIQUE(RunId,StableKey)
);
CREATE INDEX IX_JobStage_Queue ON JobStage(RunId,State,Ordinal,NextAttemptAt);

CREATE TABLE JobAttempt (
 Id TEXT PRIMARY KEY,
 RunId TEXT NOT NULL REFERENCES AgentRun(Id) ON DELETE CASCADE,
 StageId TEXT NOT NULL REFERENCES JobStage(Id) ON DELETE CASCADE,
 StageKey TEXT NOT NULL,
 AttemptNumber INTEGER NOT NULL CHECK(AttemptNumber >= 1),
 OwnerSession TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Running','Completed','Failed','Cancelled','Interrupted')),
 StartedAt TEXT NOT NULL,
 FinishedAt TEXT,
 LastSequence INTEGER NOT NULL DEFAULT 0 CHECK(LastSequence >= 0),
 Error TEXT,
 InputHash TEXT NOT NULL,
 UNIQUE(StageId,AttemptNumber)
);
CREATE INDEX IX_JobAttempt_Active ON JobAttempt(RunId,State,OwnerSession);

CREATE TABLE JobBudget (
 RunId TEXT PRIMARY KEY REFERENCES AgentRun(Id) ON DELETE CASCADE,
 RootRunId TEXT NOT NULL REFERENCES AgentRun(Id) ON DELETE CASCADE,
 MaxToolCalls INTEGER NOT NULL CHECK(MaxToolCalls >= 1),
 UsedToolCalls INTEGER NOT NULL DEFAULT 0 CHECK(UsedToolCalls >= 0),
 MaxModelRequests INTEGER NOT NULL CHECK(MaxModelRequests >= 1),
 UsedModelRequests INTEGER NOT NULL DEFAULT 0 CHECK(UsedModelRequests >= 0),
 MaxRuntimeMilliseconds INTEGER NOT NULL CHECK(MaxRuntimeMilliseconds >= 1000),
 UsedRuntimeMilliseconds INTEGER NOT NULL DEFAULT 0 CHECK(UsedRuntimeMilliseconds >= 0),
 CHECK(UsedToolCalls <= MaxToolCalls),
 CHECK(UsedModelRequests <= MaxModelRequests),
 CHECK(UsedRuntimeMilliseconds <= MaxRuntimeMilliseconds)
);
CREATE INDEX IX_JobBudget_Root ON JobBudget(RootRunId);

CREATE TABLE CrawlBatch (
 Id TEXT PRIMARY KEY,
 RunId TEXT REFERENCES AgentRun(Id) ON DELETE SET NULL,
 StageId TEXT REFERENCES JobStage(Id) ON DELETE SET NULL,
 SourceKey TEXT NOT NULL,
 ScopeJson TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Queued','Running','Completed','Failed','Cancelled')),
 ExpectedCount INTEGER,
 ScannedCount INTEGER NOT NULL DEFAULT 0,
 SelectedCount INTEGER NOT NULL DEFAULT 0,
 Complete INTEGER NOT NULL DEFAULT 0 CHECK(Complete IN (0,1)),
 ManifestArtifactId TEXT,
 Error TEXT,
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL
);
CREATE INDEX IX_CrawlBatch_Run ON CrawlBatch(RunId,CreatedAt);

CREATE TABLE CrawlItem (
 Id TEXT PRIMARY KEY,
 BatchId TEXT NOT NULL REFERENCES CrawlBatch(Id) ON DELETE CASCADE,
 ExternalId TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Discovered','Fetched','Selected','Failed','Skipped')),
 AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK(AttemptCount >= 0),
 PayloadJson TEXT,
 Error TEXT,
 UpdatedAt TEXT NOT NULL,
 UNIQUE(BatchId,ExternalId)
);
CREATE INDEX IX_CrawlItem_Batch ON CrawlItem(BatchId,State,ExternalId);

CREATE UNIQUE INDEX UX_AgentRunEvent_AttemptSequence
ON AgentRunEvent(AttemptId,AttemptSequence)
WHERE AttemptId IS NOT NULL AND AttemptSequence IS NOT NULL;
