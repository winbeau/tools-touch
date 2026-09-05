ALTER TABLE Outreach ADD COLUMN AppointmentId TEXT REFERENCES Appointment(Id);
ALTER TABLE Outreach ADD COLUMN PreferenceId TEXT REFERENCES PreferenceRevision(Id);
ALTER TABLE Outreach ADD COLUMN AnalysisArtifactId TEXT REFERENCES Artifact(Id);
ALTER TABLE Outreach ADD COLUMN Language TEXT NOT NULL DEFAULT 'zh-CN';
ALTER TABLE Outreach ADD COLUMN PromptVersion TEXT NOT NULL DEFAULT 'legacy';
ALTER TABLE Outreach ADD COLUMN Model TEXT;
ALTER TABLE Outreach ADD COLUMN DraftRequestJson TEXT NOT NULL DEFAULT '{}';
CREATE INDEX IX_Outreach_AnalysisArtifact ON Outreach(AnalysisArtifactId);
CREATE INDEX IX_Outreach_Appointment ON Outreach(AppointmentId);

CREATE TABLE DraftChange (
 Id TEXT PRIMARY KEY,
 DraftId TEXT NOT NULL REFERENCES Outreach(Id) ON DELETE CASCADE,
 Revision INTEGER NOT NULL CHECK(Revision >= 1),
 ChangeKind TEXT NOT NULL CHECK(ChangeKind IN ('Generated','Edited')),
 SnapshotJson TEXT NOT NULL,
 CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_DraftChange_Draft ON DraftChange(DraftId,Revision);

-- Preserve the visible history of drafts that already existed before this migration.
INSERT INTO DraftChange(Id,DraftId,Revision,ChangeKind,SnapshotJson,CreatedAt)
SELECT 'legacy-generated-' || Id, Id, Revision, 'Generated', json_object(
    'professorId', ProfessorId, 'recipient', Recipient, 'subject', Subject, 'body', Body,
    'cvPath', CvPath, 'cvHash', CvHash, 'revision', Revision, 'evidenceJson', EvidenceJson,
    'profileId', ProfileId, 'appointmentId', AppointmentId, 'preferenceId', PreferenceId,
    'analysisArtifactId', AnalysisArtifactId, 'language', Language, 'promptVersion', PromptVersion,
    'model', Model, 'draftRequestJson', DraftRequestJson), CreatedAt
FROM Outreach;
