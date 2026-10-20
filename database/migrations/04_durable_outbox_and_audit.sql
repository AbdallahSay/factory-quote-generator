-- Migration: 04_durable_outbox_and_audit.sql
-- Description: Additive transactional outbox columns and indexes for durable Telegram delivery (F10)
-- Safe, idempotent, non-destructive migration.

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'ClaimToken')
BEGIN
    ALTER TABLE QuoteTelegramDeliveries ADD ClaimToken NVARCHAR(100) NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'ClaimedAt')
BEGIN
    ALTER TABLE QuoteTelegramDeliveries ADD ClaimedAt DATETIME2 NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'ClaimExpiresAt')
BEGIN
    ALTER TABLE QuoteTelegramDeliveries ADD ClaimExpiresAt DATETIME2 NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'NextRetryAt')
BEGIN
    ALTER TABLE QuoteTelegramDeliveries ADD NextRetryAt DATETIME2 NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'LastAttemptAt')
BEGIN
    ALTER TABLE QuoteTelegramDeliveries ADD LastAttemptAt DATETIME2 NULL;
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'FileSha256')
BEGIN
    ALTER TABLE QuoteTelegramDeliveries ADD FileSha256 NVARCHAR(64) NULL;
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_QuoteTelegramDeliveries_Status_NextRetryAt')
BEGIN
    CREATE NONCLUSTERED INDEX IX_QuoteTelegramDeliveries_Status_NextRetryAt
    ON QuoteTelegramDeliveries (Status, NextRetryAt);
END
