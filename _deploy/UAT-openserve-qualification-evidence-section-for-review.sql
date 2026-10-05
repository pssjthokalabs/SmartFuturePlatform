-- =====================================================================
-- UAT review section: 20261004144143_AddOpenserveQualificationEvidence
-- Openserve Product Qualification evidence (UAT SF-20261004-66E5A8CE:
-- an AMID was treated as Fibre coverage; Openserve answered "No Coverage").
--
-- NEW TABLE  [OpenserveQualificationResults] — one row per qualification
--   run: AMID + Openserve's canonical LR_* address, DIST_M, Fibre (FTTH)
--   availability/status, address match vs the customer's address, the
--   package mapping evaluated and the eligibility verdict/reason, Admin
--   address acceptance. Links to the raw call via IntegrationLogId (plain
--   column) and to its order via OrderId (plain indexed column, no FK —
--   a run can be recorded before the order row exists).
-- NEW TABLE  [OpenserveQualificationProducts] — every FTTH product row
--   (ProductCode, up/downstream speed, FTTH_Status/Type), FK → results
--   with ON DELETE CASCADE.
-- NEW COLUMN [Orders].[OpenserveQualificationResultId] uniqueidentifier
--   NULL, indexed, FK → [OpenserveQualificationResults] (NO ACTION).
-- NEW COLUMN [OrderIntents].[OpenserveQualificationResultId]
--   uniqueidentifier NULL, indexed, no FK.
--
-- Additive only: no existing data changed, no backfill. Existing orders
-- keep NULL (= "qualified before evidence was recorded"); the API
-- re-qualifies them before submission (self-heal) or Admin re-runs
-- Product Qualification.
-- Idempotent: every statement is guarded by __EFMigrationsHistory.
-- Already appended to UAT-idempotent-migrations.sql. NOT yet applied.
-- Apply AFTER 20261004093118_AddOrderOpenserveBuildingCandidates.
-- =====================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveQualificationResultId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [OpenserveQualificationResultId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE TABLE [OpenserveQualificationResults] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NULL,
        [Purpose] int NOT NULL,
        [IntegrationLogId] uniqueidentifier NULL,
        [QualifiedAtUtc] datetime2 NOT NULL,
        [QueryLatitude] decimal(9,6) NULL,
        [QueryLongitude] decimal(9,6) NULL,
        [QueryAmid] nvarchar(60) NULL,
        [CallSucceeded] bit NOT NULL,
        [HttpStatusCode] int NULL,
        [ErrorCode] nvarchar(120) NULL,
        [ErrorMessage] nvarchar(1000) NULL,
        [AddressIdentified] bit NOT NULL,
        [Amid] nvarchar(60) NULL,
        [CanonicalAddress] nvarchar(400) NULL,
        [StreetNumber] nvarchar(40) NULL,
        [StreetName] nvarchar(200) NULL,
        [StreetType] nvarchar(40) NULL,
        [Suburb] nvarchar(150) NULL,
        [Town] nvarchar(150) NULL,
        [Province] nvarchar(150) NULL,
        [Region] nvarchar(150) NULL,
        [Country] nvarchar(100) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [AddressStatus] nvarchar(100) NULL,
        [DistanceMeters] decimal(12,2) NULL,
        [DistanceText] nvarchar(60) NULL,
        [MduVerification] nvarchar(500) NULL,
        [AddressMessage] nvarchar(500) NULL,
        [BuildingCandidateCount] int NOT NULL,
        [BuildingCandidatesJson] nvarchar(max) NULL,
        [FibreAvailability] int NOT NULL,
        [FtthStatusSummary] nvarchar(300) NULL,
        [FtthInfrastructureCount] int NOT NULL,
        [FibreMaxSpeedMbps] decimal(12,2) NULL,
        [AvailableProductCodes] nvarchar(500) NULL,
        [EthernetProductCodes] nvarchar(300) NULL,
        [FwaStatus] nvarchar(100) NULL,
        [CustomerAddress] nvarchar(600) NULL,
        [AddressMatch] int NOT NULL,
        [AddressMatchDetail] nvarchar(1000) NULL,
        [AddressAcceptedAtUtc] datetime2 NULL,
        [AddressAcceptedByUserId] uniqueidentifier NULL,
        [AddressAcceptanceNote] nvarchar(500) NULL,
        [ServicePackageId] uniqueidentifier NULL,
        [MappingSku] nvarchar(20) NULL,
        [MappingCapacity] nvarchar(20) NULL,
        [MappingCapacityUom] nvarchar(20) NULL,
        [ProductEligibility] int NOT NULL,
        [EligibilityReason] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OpenserveQualificationResults] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE TABLE [OpenserveQualificationProducts] (
        [Id] uniqueidentifier NOT NULL,
        [QualificationResultId] uniqueidentifier NOT NULL,
        [InfrastructureIndex] int NOT NULL,
        [InfrastructureType] nvarchar(60) NULL,
        [FtthStatus] nvarchar(60) NULL,
        [ServiceProviderId] nvarchar(60) NULL,
        [IsImmediatelyAvailable] bit NOT NULL,
        [FibreMaxSpeedMbps] decimal(12,2) NULL,
        [ProductCode] nvarchar(20) NULL,
        [ProductName] nvarchar(150) NULL,
        [UpstreamSpeed] nvarchar(40) NULL,
        [DownstreamSpeed] nvarchar(40) NULL,
        [UpstreamMbps] decimal(12,2) NULL,
        [DownstreamMbps] decimal(12,2) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OpenserveQualificationProducts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OpenserveQualificationProducts_OpenserveQualificationResults_QualificationResultId] FOREIGN KEY ([QualificationResultId]) REFERENCES [OpenserveQualificationResults] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_Orders_OpenserveQualificationResultId] ON [Orders] ([OpenserveQualificationResultId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_OpenserveQualificationResultId] ON [OrderIntents] ([OpenserveQualificationResultId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OpenserveQualificationProducts_ProductCode] ON [OpenserveQualificationProducts] ([ProductCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OpenserveQualificationProducts_QualificationResultId] ON [OpenserveQualificationProducts] ([QualificationResultId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OpenserveQualificationResults_Amid] ON [OpenserveQualificationResults] ([Amid]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OpenserveQualificationResults_OrderId] ON [OpenserveQualificationResults] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OpenserveQualificationResults_QualifiedAtUtc] ON [OpenserveQualificationResults] ([QualifiedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    CREATE INDEX [IX_OpenserveQualificationResults_QueryLatitude_QueryLongitude_QualifiedAtUtc] ON [OpenserveQualificationResults] ([QueryLatitude], [QueryLongitude], [QualifiedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    ALTER TABLE [Orders] ADD CONSTRAINT [FK_Orders_OpenserveQualificationResults_OpenserveQualificationResultId] FOREIGN KEY ([OpenserveQualificationResultId]) REFERENCES [OpenserveQualificationResults] ([Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261004144143_AddOpenserveQualificationEvidence'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261004144143_AddOpenserveQualificationEvidence', N'8.0.11');
END;
GO

COMMIT;
GO

