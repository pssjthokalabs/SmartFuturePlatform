BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915185520_AddOrderPropertyType'
)
BEGIN
    ALTER TABLE [Orders] ADD [BuildingComplexName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915185520_AddOrderPropertyType'
)
BEGIN
    ALTER TABLE [Orders] ADD [PropertyType] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915185520_AddOrderPropertyType'
)
BEGIN
    ALTER TABLE [Orders] ADD [UnitNumber] nvarchar(50) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915185520_AddOrderPropertyType'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915185520_AddOrderPropertyType', N'8.0.11');
END;
GO

COMMIT;
GO
