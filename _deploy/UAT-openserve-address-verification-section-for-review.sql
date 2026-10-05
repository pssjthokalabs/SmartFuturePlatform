-- =====================================================================
-- UAT review section: 20261005103227_AddOpenserveAddressVerification
-- Openserve address verification (FORCEVERIFY=Y → AddressVerify[]).
-- A coordinate lookup only yields nearby Openserve Address Master
-- candidates; the premises (AMID) is the one candidate that matches the
-- customer's street number + street, or one an Admin explicitly chose.
--
-- Adds 9 columns to [OpenserveQualificationResults] (nothing else):
--   AddressResolution int NOT NULL DEFAULT 0   (0 NotEvaluated = legacy row,
--       1 AutoMatched, 2 AdminSelected, 3 Unresolved, 4 NoCandidates)
--   AddressVerifiedAtUtc datetime2 NULL        (when AddressVerify was read)
--   AddressVerifyIntegrationLogId uniqueidentifier NULL (the FORCEVERIFY call's
--       OpenserveIntegrationLogs row; plain column, no FK)
--   AddressCandidateCount int NOT NULL DEFAULT 0
--   AddressCandidatesJson nvarchar(max) NULL   (candidates: AMID, LR_Address,
--       LR_LAT/LR_LON, DIST, DIST_M + SmartFuture's match verdict — Openserve
--       data only, never headers/credentials)
--   AddressResolutionDetail nvarchar(1000) NULL
--   AddressResolvedByUserId uniqueidentifier NULL, AddressResolvedAtUtc
--       datetime2 NULL, AddressResolutionNote nvarchar(500) NULL (Admin choice)
--
-- Additive only: no data changed, no backfill, no index, no FK. Existing
-- rows read as NotEvaluated (their AMID came from the old nearest-address
-- lookup); the API re-verifies on Admin "Re-run address verification".
-- Idempotent: every statement is guarded by __EFMigrationsHistory.
-- Already appended to UAT-idempotent-migrations.sql. NOT yet applied.
-- Apply AFTER 20261004144143_AddOpenserveQualificationEvidence.
-- =====================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressCandidateCount] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressCandidatesJson] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressResolution] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressResolutionDetail] nvarchar(1000) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressResolutionNote] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressResolvedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressResolvedByUserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressVerifiedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    ALTER TABLE [OpenserveQualificationResults] ADD [AddressVerifyIntegrationLogId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005103227_AddOpenserveAddressVerification'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005103227_AddOpenserveAddressVerification', N'8.0.11');
END;
GO

COMMIT;
GO

