-- ========================================================================
-- SECURITY BATCH 03 — MIGRATION 03: SECURE DOCUMENTS & METADATA (F15 / F03)
-- Additive, safe migration for SQL Server.
-- ========================================================================

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Quotes') AND name = 'PdfSha256')
BEGIN
    ALTER TABLE Quotes ADD PdfSha256 NVARCHAR(64) NULL;
END;

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Quotes') AND name = 'PdfSizeBytes')
BEGIN
    ALTER TABLE Quotes ADD PdfSizeBytes BIGINT NULL;
END;

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Quotes') AND name = 'StorageFileName')
BEGIN
    ALTER TABLE Quotes ADD StorageFileName NVARCHAR(260) NULL;
END;

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Quotes') AND name = 'DocxStorageFileName')
BEGIN
    ALTER TABLE Quotes ADD DocxStorageFileName NVARCHAR(260) NULL;
END;
