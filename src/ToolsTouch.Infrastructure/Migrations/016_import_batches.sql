ALTER TABLE ImportBatch ADD COLUMN CollectionId TEXT;
ALTER TABLE ImportBatch ADD COLUMN SourcePath TEXT;
ALTER TABLE ImportBatch ADD COLUMN SourceHash TEXT;
ALTER TABLE ImportBatch ADD COLUMN Format TEXT;
ALTER TABLE ImportBatch ADD COLUMN DuplicateStrategy TEXT;
ALTER TABLE ImportBatch ADD COLUMN SheetName TEXT;
ALTER TABLE ImportBatch ADD COLUMN MappingJson TEXT;
ALTER TABLE ImportBatch ADD COLUMN ErrorsJson TEXT;

CREATE INDEX IX_ImportBatch_Collection ON ImportBatch(CollectionId,CreatedAt DESC);
