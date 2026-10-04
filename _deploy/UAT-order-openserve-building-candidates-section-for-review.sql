-- =====================================================================
-- UAT review section: 20261004093118_AddOrderOpenserveBuildingCandidates
-- Adds two NULLABLE columns to [Orders]:
--   OpenserveBuildingCandidateCount int — how many buildingInfo rows
--     Openserve Product Qualification returned for the order's AMID;
--   OpenserveBuildingCandidatesJson nvarchar(max) — those rows verbatim,
--     for Admin to choose the building/unit (BLD_NUM_ID) from.
-- Additive only: no data change, no backfill, no index, no constraint.
-- Existing rows stay NULL ("not recorded"); the API reads the old
-- qualification note for those and Admin can reload the rows by AMID.
-- Idempotent: every statement is guarded by __EFMigrationsHistory.
-- Already appended to UAT-idempotent-migrations.sql. NOT yet applied.
-- Apply AFTER 20261003154757_AddOpenserveSubmissionRecovery.
-- =====================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004093118_AddOrderOpenserveBuildingCandidates'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveBuildingCandidateCount] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004093118_AddOrderOpenserveBuildingCandidates'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveBuildingCandidatesJson] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004093118_AddOrderOpenserveBuildingCandidates'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261004093118_AddOrderOpenserveBuildingCandidates', N'8.0.11');
END;
GO

COMMIT;
GO

