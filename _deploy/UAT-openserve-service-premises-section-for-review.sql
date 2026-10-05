-- =====================================================================
-- UAT review section: 20261005152317_AddOpenserveServicePremisesSelection
-- Openserve SERVICE PREMISES vs the customer's INSTALLATION ADDRESS.
-- When no Openserve AddressVerify record matches the customer's address
-- automatically, the customer may choose (and must confirm) the record that
-- corresponds to their property. The installation address (AddressLine1…)
-- is never overwritten; the chosen record is kept here as the order's
-- Openserve service premises snapshot, with how it was established.
--
-- Adds 6 columns to [Orders] (nothing else):
--   OpenservePremisesAddress nvarchar(300) NULL      (Openserve's record text)
--   OpenservePremisesSelection int NOT NULL DEFAULT 0 (0 NotEvaluated/legacy,
--       1 AutoMatched, 2 AdminSelected, 5 CustomerSelected)
--   OpenservePremisesSelectedAtUtc datetime2 NULL
--   OpenservePremisesSelectedByUserId uniqueidentifier NULL (no FK)
--   OpenservePremisesDistanceMeters decimal(10,2) NULL (AddressVerify DIST)
--   OpenservePremisesCustomerConfirmedAtUtc datetime2 NULL
--
-- Additive only: no data changed, no backfill, no index, no FK. Existing
-- orders read as NotEvaluated; the Admin view falls back to the linked
-- evidence row. Idempotent: every statement is guarded by
-- __EFMigrationsHistory. Already appended to UAT-idempotent-migrations.sql.
-- NOT yet applied. Apply AFTER 20261005103227_AddOpenserveAddressVerification.
-- =====================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenservePremisesAddress] nvarchar(300) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenservePremisesCustomerConfirmedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenservePremisesDistanceMeters] decimal(10,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenservePremisesSelectedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenservePremisesSelectedByUserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenservePremisesSelection] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005152317_AddOpenserveServicePremisesSelection'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005152317_AddOpenserveServicePremisesSelection', N'8.0.11');
END;
GO

COMMIT;
GO
