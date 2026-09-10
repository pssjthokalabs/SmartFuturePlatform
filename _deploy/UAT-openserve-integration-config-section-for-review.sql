BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910082250_AddOpenserveIntegrationConfig'
)
BEGIN
    CREATE TABLE [OpenserveIntegrationConfigs] (
        [Id] uniqueidentifier NOT NULL,
        [Enabled] bit NULL,
        [BaseUrl] nvarchar(300) NULL,
        [WsIspCode] nvarchar(60) NULL,
        [IspIdentifier] nvarchar(60) NULL,
        [SenderId] nvarchar(60) NULL,
        [ReplyToAddress] nvarchar(300) NULL,
        [EventNotificationUrl] nvarchar(300) NULL,
        [HttpTimeoutSeconds] int NULL,
        [PollingFallbackIntervalMinutes] int NULL,
        [CallbackAuthMode] int NULL,
        [AllowedIpRangesCsv] nvarchar(1000) NULL,
        [ApiKeyProtected] nvarchar(max) NULL,
        [SharedSecretProtected] nvarchar(max) NULL,
        [UpdatedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OpenserveIntegrationConfigs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OpenserveIntegrationConfigs_AspNetUsers_UpdatedByUserId] FOREIGN KEY ([UpdatedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910082250_AddOpenserveIntegrationConfig'
)
BEGIN
    CREATE INDEX [IX_OpenserveIntegrationConfigs_UpdatedByUserId] ON [OpenserveIntegrationConfigs] ([UpdatedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910082250_AddOpenserveIntegrationConfig'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260910082250_AddOpenserveIntegrationConfig', N'8.0.11');
END;
GO

COMMIT;
GO
