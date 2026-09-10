GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [OpenserveSubscriberReferenceNumber] nvarchar(120) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE TABLE [PackageOpenserveMappings] (
        [Id] uniqueidentifier NOT NULL,
        [ServicePackageId] uniqueidentifier NOT NULL,
        [OpenserveProductName] nvarchar(200) NOT NULL,
        [Sku] nvarchar(20) NOT NULL,
        [Capacity] nvarchar(20) NOT NULL,
        [CapacityUom] nvarchar(20) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [OpenserveProductOfferingId] nvarchar(60) NULL,
        [OpenserveProductSpecificationId] nvarchar(60) NULL,
        [Notes] nvarchar(2000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PackageOpenserveMappings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PackageOpenserveMappings_ServicePackages_ServicePackageId] FOREIGN KEY ([ServicePackageId]) REFERENCES [ServicePackages] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE TABLE [OpenserveOrders] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [ExternalReferenceNumber] nvarchar(120) NOT NULL,
        [LastMessageId] nvarchar(64) NULL,
        [CorrelationId] nvarchar(120) NULL,
        [OpenserveOrderId] nvarchar(60) NULL,
        [OpenserveOrderName] nvarchar(60) NULL,
        [SubscriberReferenceNumber] nvarchar(120) NULL,
        [OrderType] nvarchar(60) NOT NULL,
        [Reason] nvarchar(60) NULL,
        [RawState] nvarchar(60) NULL,
        [NormalizedStatus] int NOT NULL,
        [SubmittedAtUtc] datetime2 NULL,
        [LastOpenserveUpdateAtUtc] datetime2 NULL,
        [LastSuccessfulSyncAtUtc] datetime2 NULL,
        [RetryCount] int NOT NULL,
        [LastFailureCode] nvarchar(120) NULL,
        [LastFailureMessage] nvarchar(2000) NULL,
        [PackageOpenserveMappingId] uniqueidentifier NULL,
        [IsTerminal] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OpenserveOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OpenserveOrders_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OpenserveOrders_PackageOpenserveMappings_PackageOpenserveMappingId] FOREIGN KEY ([PackageOpenserveMappingId]) REFERENCES [PackageOpenserveMappings] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE TABLE [OpenserveIntegrationLogs] (
        [Id] uniqueidentifier NOT NULL,
        [OpenserveOrderId] uniqueidentifier NULL,
        [Direction] int NOT NULL,
        [OperationType] int NOT NULL,
        [MessageId] nvarchar(64) NULL,
        [CorrelationId] nvarchar(120) NULL,
        [HttpMethod] nvarchar(10) NULL,
        [Endpoint] nvarchar(500) NULL,
        [RequestHeadersJson] nvarchar(4000) NULL,
        [RequestBodyJson] nvarchar(max) NULL,
        [ResponseStatusCode] int NULL,
        [ResponseBodyJson] nvarchar(max) NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [IsSuccess] bit NOT NULL,
        [ErrorSummary] nvarchar(2000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OpenserveIntegrationLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OpenserveIntegrationLogs_OpenserveOrders_OpenserveOrderId] FOREIGN KEY ([OpenserveOrderId]) REFERENCES [OpenserveOrders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE TABLE [OpenserveOrderStatusHistories] (
        [Id] uniqueidentifier NOT NULL,
        [OpenserveOrderId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [OpenserveEventId] nvarchar(60) NULL,
        [CorrelationId] nvarchar(120) NULL,
        [EventType] int NOT NULL,
        [PreviousRawState] nvarchar(60) NULL,
        [NewRawState] nvarchar(60) NULL,
        [NormalizedStatus] int NOT NULL,
        [Description] nvarchar(2000) NULL,
        [InstallationStatus] nvarchar(200) NULL,
        [AppointmentAtUtc] datetime2 NULL,
        [ReceivedAtUtc] datetime2 NOT NULL,
        [EventOccurredAtUtc] datetime2 NULL,
        [IntegrationLogId] uniqueidentifier NULL,
        [ProcessingResult] nvarchar(60) NULL,
        [NotificationTriggered] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OpenserveOrderStatusHistories] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OpenserveOrderStatusHistories_OpenserveIntegrationLogs_IntegrationLogId] FOREIGN KEY ([IntegrationLogId]) REFERENCES [OpenserveIntegrationLogs] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OpenserveOrderStatusHistories_OpenserveOrders_OpenserveOrderId] FOREIGN KEY ([OpenserveOrderId]) REFERENCES [OpenserveOrders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_NetworkAccounts_OpenserveSubscriberReferenceNumber] ON [NetworkAccounts] ([OpenserveSubscriberReferenceNumber]) WHERE [OpenserveSubscriberReferenceNumber] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveIntegrationLogs_MessageId] ON [OpenserveIntegrationLogs] ([MessageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveIntegrationLogs_OccurredAtUtc] ON [OpenserveIntegrationLogs] ([OccurredAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveIntegrationLogs_OpenserveOrderId] ON [OpenserveIntegrationLogs] ([OpenserveOrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveIntegrationLogs_OperationType] ON [OpenserveIntegrationLogs] ([OperationType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OpenserveOrders_ExternalReferenceNumber] ON [OpenserveOrders] ([ExternalReferenceNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrders_IsTerminal] ON [OpenserveOrders] ([IsTerminal]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrders_NormalizedStatus] ON [OpenserveOrders] ([NormalizedStatus]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrders_OpenserveOrderId] ON [OpenserveOrders] ([OpenserveOrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrders_OrderId] ON [OpenserveOrders] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrders_PackageOpenserveMappingId] ON [OpenserveOrders] ([PackageOpenserveMappingId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrderStatusHistories_IntegrationLogId] ON [OpenserveOrderStatusHistories] ([IntegrationLogId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrderStatusHistories_OpenserveEventId] ON [OpenserveOrderStatusHistories] ([OpenserveEventId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrderStatusHistories_OpenserveOrderId] ON [OpenserveOrderStatusHistories] ([OpenserveOrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrderStatusHistories_OrderId] ON [OpenserveOrderStatusHistories] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_OpenserveOrderStatusHistories_ReceivedAtUtc] ON [OpenserveOrderStatusHistories] ([ReceivedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE INDEX [IX_PackageOpenserveMappings_IsEnabled] ON [PackageOpenserveMappings] ([IsEnabled]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PackageOpenserveMappings_ServicePackageId] ON [PackageOpenserveMappings] ([ServicePackageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909160112_AddOpenserveFulfilmentIntegration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909160112_AddOpenserveFulfilmentIntegration', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909171455_AddOpenserveAmIdToOrders'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveAmId] nvarchar(60) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909171455_AddOpenserveAmIdToOrders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909171455_AddOpenserveAmIdToOrders', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909171535_SeedDailyCrawlFrequencyForActiveJobSources'
)
BEGIN
    UPDATE [JobSources] SET [CrawlFrequencyMinutes] = 1440 WHERE [CrawlFrequencyMinutes] IS NULL AND [IsActive] = 1 AND [SourceType] <> 3;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909171535_SeedDailyCrawlFrequencyForActiveJobSources'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909171535_SeedDailyCrawlFrequencyForActiveJobSources', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909204918_AddOpenserveQualificationFieldsToOrders'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveBuildingNumId] nvarchar(60) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909204918_AddOpenserveQualificationFieldsToOrders'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveQualificationFailureReason] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909204918_AddOpenserveQualificationFieldsToOrders'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveQualifiedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909204918_AddOpenserveQualificationFieldsToOrders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909204918_AddOpenserveQualificationFieldsToOrders', N'8.0.11');
END;
GO

COMMIT;
GO

