CREATE TABLE DeliveryCheck (
 Id TEXT PRIMARY KEY,
 DraftId TEXT NOT NULL REFERENCES Outreach(Id) ON DELETE CASCADE,
 AttemptId TEXT REFERENCES SendAttempt(Id),
 SenderAccount TEXT NOT NULL,
 RfcMessageId TEXT NOT NULL,
 Outcome TEXT NOT NULL CHECK(Outcome IN ('Found','NotFound','Ambiguous','Failed')),
 ProviderMessageId TEXT,
 ThreadId TEXT,
 Error TEXT,
 CheckedAt TEXT NOT NULL
);
CREATE INDEX IX_DeliveryCheck_Draft ON DeliveryCheck(DraftId,CheckedAt DESC);

CREATE TABLE EmailMessage (
 Id TEXT PRIMARY KEY,
 Account TEXT NOT NULL,
 ThreadId TEXT NOT NULL,
 ProviderMessageId TEXT NOT NULL,
 OutreachId TEXT REFERENCES Outreach(Id) ON DELETE SET NULL,
 FromAddress TEXT NOT NULL,
 ToAddress TEXT,
 InReplyTo TEXT,
 ReferencesJson TEXT NOT NULL DEFAULT '[]',
 IsSelf INTEGER NOT NULL CHECK(IsSelf IN (0,1)),
 RelationState TEXT NOT NULL CHECK(RelationState IN ('Sent','Reply','ThreadMember','Ambiguous')),
 InternalDate TEXT,
 RecordedAt TEXT NOT NULL,
 UNIQUE(Account,ProviderMessageId)
);
CREATE INDEX IX_EmailMessage_Thread ON EmailMessage(Account,ThreadId,InternalDate,ProviderMessageId);
CREATE INDEX IX_EmailMessage_Outreach ON EmailMessage(OutreachId,RelationState);
