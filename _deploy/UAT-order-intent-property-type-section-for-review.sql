BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915191341_AddOrderIntentPropertyType'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [BuildingComplexName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915191341_AddOrderIntentPropertyType'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [PropertyType] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915191341_AddOrderIntentPropertyType'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [UnitNumber] nvarchar(50) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915191341_AddOrderIntentPropertyType'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915191341_AddOrderIntentPropertyType', N'8.0.11');
END;
GO

COMMIT;
GO


