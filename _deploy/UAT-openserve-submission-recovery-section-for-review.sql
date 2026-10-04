-- =====================================================================
-- UAT review section: 20261003154757_AddOpenserveSubmissionRecovery
-- Openserve submission recovery + per-order automation pause.
--   [Orders]: OpenserveAutomationPaused (bit NOT NULL DEFAULT 0),
--     OpenserveAutomationPausedAtUtc, OpenserveAutomationPausedByUserId,
--     OpenserveAutomationPauseReason nvarchar(500) — all NULLABLE.
--   [OpenserveOrders]: LastFailureClass (int NOT NULL DEFAULT 0),
--     AutomaticRetryCount (int NOT NULL DEFAULT 0), LastSubmissionAttemptAtUtc,
--     LastSubmissionTrigger, NextAutomaticRetryAtUtc — NULLABLE;
--     index IX_OpenserveOrders_NextAutomaticRetryAtUtc.
--   [OpenserveIntegrationLogs]: SubmissionTrigger int NULL.
-- Data change (Failed rows only, NormalizedStatus = 7): sets
-- LastFailureClass from each row's latest Create Order log, and a
-- BLOCKED_* LastFailureCode on never-sent rows. NextAutomaticRetryAtUtc
-- is left NULL, so no existing row is resent automatically after deploy.
-- Idempotent: every statement is guarded by __EFMigrationsHistory.
-- Already appended to UAT-idempotent-migrations.sql. NOT yet applied.
-- Apply AFTER 20261001103523_AddOrderOpenserveMduPlaceFields.
-- =====================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveAutomationPauseReason] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveAutomationPaused] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveAutomationPausedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveAutomationPausedByUserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [OpenserveOrders] ADD [AutomaticRetryCount] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [OpenserveOrders] ADD [LastFailureClass] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [OpenserveOrders] ADD [LastSubmissionAttemptAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [OpenserveOrders] ADD [LastSubmissionTrigger] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [OpenserveOrders] ADD [NextAutomaticRetryAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    ALTER TABLE [OpenserveIntegrationLogs] ADD [SubmissionTrigger] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    EXEC(N'
    UPDATE o SET o.LastFailureClass = CASE
        WHEN lastLog.HttpMethod IS NULL THEN 3
        WHEN o.LastFailureCode IN (N''TIMEOUT'', N''TRANSPORT_ERROR'', N''PARSE_ERROR'') THEN 4
        WHEN lastLog.ResponseStatusCode IN (408, 429, 503) THEN 1
        WHEN lastLog.ResponseStatusCode IS NULL OR lastLog.ResponseStatusCode >= 500 THEN 4
        ELSE 2 END
    FROM OpenserveOrders o
    OUTER APPLY (SELECT TOP 1 l.HttpMethod, l.ResponseStatusCode FROM OpenserveIntegrationLogs l
                 WHERE l.OpenserveOrderId = o.Id AND l.OperationType = 1 ORDER BY l.OccurredAtUtc DESC) lastLog
    WHERE o.NormalizedStatus = 7 AND o.LastFailureClass = 0;

    UPDATE OpenserveOrders SET LastFailureCode = CASE
        WHEN LastFailureMessage LIKE N''%mapping%'' THEN N''BLOCKED_MAPPING''
        WHEN LastFailureMessage LIKE N''%AMID%'' THEN N''BLOCKED_AMID''
        WHEN LastFailureMessage LIKE N''%street address%'' THEN N''BLOCKED_ADDRESS''
        ELSE N''BLOCKED_CONTACT'' END
    WHERE NormalizedStatus = 7 AND LastFailureClass = 3 AND LastFailureCode IS NULL;
    ')
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrders_NextAutomaticRetryAtUtc] ON [OpenserveOrders] ([NextAutomaticRetryAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003154757_AddOpenserveSubmissionRecovery'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261003154757_AddOpenserveSubmissionRecovery', N'8.0.11');
END;
GO

COMMIT;
GO

