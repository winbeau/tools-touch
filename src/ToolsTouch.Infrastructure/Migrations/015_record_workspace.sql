CREATE TABLE Collection (
 Id TEXT PRIMARY KEY,
 Name TEXT NOT NULL,
 Kind TEXT NOT NULL CHECK(Kind IN ('System','Custom')),
 SystemEntityKind TEXT,
 Description TEXT,
 ArchivedAt TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 CHECK((Kind='System' AND SystemEntityKind IS NOT NULL) OR (Kind='Custom' AND SystemEntityKind IS NULL))
);
CREATE UNIQUE INDEX UX_Collection_Name ON Collection(Name COLLATE NOCASE) WHERE ArchivedAt IS NULL;

CREATE TABLE RecordRef (
 Id TEXT PRIMARY KEY,
 CollectionId TEXT NOT NULL REFERENCES Collection(Id) ON DELETE CASCADE,
 EntityKind TEXT,
 EntityId TEXT,
 CreatedAt TEXT NOT NULL,
 ArchivedAt TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CHECK((EntityKind IS NULL AND EntityId IS NULL) OR (EntityKind IS NOT NULL AND EntityId IS NOT NULL))
);
CREATE UNIQUE INDEX UX_RecordRef_SystemEntity ON RecordRef(CollectionId,EntityKind,EntityId)
 WHERE EntityKind IS NOT NULL AND EntityId IS NOT NULL AND ArchivedAt IS NULL;
CREATE INDEX IX_RecordRef_Collection ON RecordRef(CollectionId,ArchivedAt,Id);

CREATE TABLE FieldDefinition (
 Id TEXT PRIMARY KEY,
 CollectionId TEXT NOT NULL REFERENCES Collection(Id) ON DELETE CASCADE,
 Key TEXT NOT NULL,
 DisplayName TEXT NOT NULL,
 Type TEXT NOT NULL CHECK(Type IN ('Text','Number','DateTime','Boolean','Url','Choice','MultiChoice','Relation')),
 StorageKind TEXT NOT NULL CHECK(StorageKind IN ('System','Custom')),
 SystemBinding TEXT,
 OptionsJson TEXT,
 Required INTEGER NOT NULL DEFAULT 0 CHECK(Required IN (0,1)),
 EditPolicy TEXT NOT NULL CHECK(EditPolicy IN ('Editable','OverrideWithReason','SystemManaged')),
 ArchivedAt TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 CHECK((StorageKind='System' AND SystemBinding IS NOT NULL) OR (StorageKind='Custom' AND SystemBinding IS NULL))
);
CREATE UNIQUE INDEX UX_FieldDefinition_Key ON FieldDefinition(CollectionId,Key COLLATE NOCASE);
CREATE INDEX IX_FieldDefinition_Collection ON FieldDefinition(CollectionId,ArchivedAt,Key COLLATE NOCASE);

CREATE TABLE FieldChoice (
 Id TEXT PRIMARY KEY,
 FieldId TEXT NOT NULL REFERENCES FieldDefinition(Id) ON DELETE CASCADE,
 Label TEXT NOT NULL,
 ColorToken TEXT,
 SortOrder INTEGER NOT NULL DEFAULT 0,
 ArchivedAt TEXT
);
CREATE UNIQUE INDEX UX_FieldChoice_Label ON FieldChoice(FieldId,Label COLLATE NOCASE) WHERE ArchivedAt IS NULL;

CREATE TABLE FieldValue (
 RecordId TEXT NOT NULL REFERENCES RecordRef(Id) ON DELETE CASCADE,
 FieldId TEXT NOT NULL REFERENCES FieldDefinition(Id) ON DELETE CASCADE,
 TextValue TEXT,
 NumberValue REAL,
 DateValue TEXT,
 BoolValue INTEGER CHECK(BoolValue IS NULL OR BoolValue IN (0,1)),
 JsonValue TEXT,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 PRIMARY KEY(RecordId,FieldId),
 CHECK((CASE WHEN TextValue IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN NumberValue IS NOT NULL THEN 1 ELSE 0 END +
        CASE WHEN DateValue IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN BoolValue IS NOT NULL THEN 1 ELSE 0 END +
        CASE WHEN JsonValue IS NOT NULL THEN 1 ELSE 0 END) = 1)
);
CREATE INDEX IX_FieldValue_Text ON FieldValue(FieldId,TextValue,RecordId);
CREATE INDEX IX_FieldValue_Number ON FieldValue(FieldId,NumberValue,RecordId);
CREATE INDEX IX_FieldValue_Date ON FieldValue(FieldId,DateValue,RecordId);

CREATE TABLE RecordRelation (
 FieldId TEXT NOT NULL REFERENCES FieldDefinition(Id) ON DELETE CASCADE,
 FromRecordId TEXT NOT NULL REFERENCES RecordRef(Id) ON DELETE CASCADE,
 ToRecordId TEXT NOT NULL REFERENCES RecordRef(Id) ON DELETE CASCADE,
 SortOrder INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(FieldId,FromRecordId,ToRecordId)
);
CREATE INDEX IX_RecordRelation_To ON RecordRelation(FieldId,ToRecordId,FromRecordId);

CREATE TABLE ViewDefinition (
 Id TEXT PRIMARY KEY,
 CollectionId TEXT NOT NULL REFERENCES Collection(Id) ON DELETE CASCADE,
 Name TEXT NOT NULL,
 ViewType TEXT NOT NULL,
 FilterAstJson TEXT NOT NULL DEFAULT '{}',
 SortJson TEXT NOT NULL DEFAULT '[]',
 GroupJson TEXT NOT NULL DEFAULT '{}',
 ColumnsJson TEXT NOT NULL DEFAULT '[]',
 Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision > 0),
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NOT NULL,
 UNIQUE(CollectionId,Name COLLATE NOCASE)
);

CREATE TABLE RecordChange (
 Id TEXT PRIMARY KEY,
 RecordId TEXT NOT NULL REFERENCES RecordRef(Id) ON DELETE CASCADE,
 FieldId TEXT REFERENCES FieldDefinition(Id),
 CommandId TEXT NOT NULL,
 ActorKind TEXT NOT NULL,
 BeforeJson TEXT,
 AfterJson TEXT,
 OccurredAt TEXT NOT NULL,
 UndoOf TEXT REFERENCES RecordChange(Id)
);
CREATE INDEX IX_RecordChange_Record ON RecordChange(RecordId,OccurredAt DESC,Id DESC);
CREATE UNIQUE INDEX UX_RecordChange_Command ON RecordChange(CommandId,RecordId,FieldId);

CREATE TABLE ImportBatch (
 Id TEXT PRIMARY KEY,
 SourceKind TEXT NOT NULL,
 State TEXT NOT NULL CHECK(State IN ('Preview','Committed','Failed','Cancelled')),
 CommandId TEXT NOT NULL UNIQUE,
 ScannedCount INTEGER NOT NULL DEFAULT 0,
 AcceptedCount INTEGER NOT NULL DEFAULT 0,
 RejectedCount INTEGER NOT NULL DEFAULT 0,
 CreatedAt TEXT NOT NULL,
 FinishedAt TEXT,
 Error TEXT
);
