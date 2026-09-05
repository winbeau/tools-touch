CREATE TABLE SendConfirmation (
 Id TEXT PRIMARY KEY,
 DraftId TEXT NOT NULL REFERENCES Outreach(Id) ON DELETE CASCADE,
 DraftVersion INTEGER NOT NULL CHECK(DraftVersion >= 1),
 DraftRevision INTEGER NOT NULL CHECK(DraftRevision >= 1),
 SenderAccount TEXT NOT NULL,
 Recipient TEXT NOT NULL,
 CcJson TEXT NOT NULL DEFAULT '[]',
 BccJson TEXT NOT NULL DEFAULT '[]',
 Subject TEXT NOT NULL,
 Body TEXT NOT NULL,
 CvPath TEXT,
 CvHash TEXT,
 AttachmentName TEXT,
 AttachmentBytes INTEGER NOT NULL DEFAULT 0 CHECK(AttachmentBytes >= 0),
 ProfileId TEXT,
 SnapshotHash TEXT NOT NULL,
 MimeArtifactId TEXT NOT NULL REFERENCES Artifact(Id),
 ExpiresAt TEXT NOT NULL,
 CreatedAt TEXT NOT NULL,
 ConsumedAt TEXT
);
CREATE INDEX IX_SendConfirmation_Draft ON SendConfirmation(DraftId,CreatedAt DESC);
CREATE INDEX IX_SendConfirmation_Active ON SendConfirmation(ConsumedAt,ExpiresAt);

CREATE TABLE SendAttempt (
 Id TEXT PRIMARY KEY,
 DraftId TEXT NOT NULL REFERENCES Outreach(Id) ON DELETE CASCADE,
 ConfirmationId TEXT NOT NULL UNIQUE REFERENCES SendConfirmation(Id),
 DraftRevision INTEGER NOT NULL CHECK(DraftRevision >= 1),
 SenderAccount TEXT NOT NULL,
 RfcMessageId TEXT NOT NULL UNIQUE,
 MimeArtifactId TEXT NOT NULL REFERENCES Artifact(Id),
 MimeHash TEXT NOT NULL,
 SnapshotHash TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Sending','Transmitting','Sent','Failed','Unknown')),
 ProviderMessageId TEXT,
 ThreadId TEXT,
 Error TEXT,
 CreatedAt TEXT NOT NULL,
 StartedAt TEXT,
 FinishedAt TEXT
);
CREATE INDEX IX_SendAttempt_Draft ON SendAttempt(DraftId,CreatedAt DESC);
CREATE UNIQUE INDEX UX_SendAttempt_DraftPending
ON SendAttempt(DraftId) WHERE State IN ('Sending','Transmitting','Unknown');
