-- =====================================================================
-- UAT review section: 20261001103523_AddOrderOpenserveMduPlaceFields
-- Adds three NULLABLE columns to [Orders] holding the MDU place values
-- that Openserve's Create Order requires "exactly per product
-- qualification API" (Postman UC 1): BUILDING_NAME, FLOOR and NUM from
-- the same buildingInfo row as the existing OpenserveBuildingNumId.
-- Additive only: no data change, no backfill, no index, no constraint.
-- Idempotent: every statement is guarded by __EFMigrationsHistory.
-- Already appended to UAT-idempotent-migrations.sql. NOT yet applied.
-- =====================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001103523_AddOrderOpenserveMduPlaceFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveBuildingName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001103523_AddOrderOpenserveMduPlaceFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveFloor] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001103523_AddOrderOpenserveMduPlaceFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveUnit] nvarchar(60) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001103523_AddOrderOpenserveMduPlaceFields'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261001103523_AddOrderOpenserveMduPlaceFields', N'8.0.11');
END;
GO

COMMIT;
GO

