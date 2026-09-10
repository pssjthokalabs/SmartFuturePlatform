IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] uniqueidentifier NOT NULL,
        [FirstName] nvarchar(100) NOT NULL,
        [LastName] nvarchar(100) NOT NULL,
        [AccountStatus] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] uniqueidentifier NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE TABLE [RefreshTokens] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Token] nvarchar(256) NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [RevokedAtUtc] datetime2 NULL,
        [ReplacedByToken] nvarchar(256) NULL,
        [CreatedByIp] nvarchar(64) NULL,
        [RevokedByIp] nvarchar(64) NULL,
        CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RefreshTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_AspNetUsers_AccountStatus] ON [AspNetUsers] ([AccountStatus]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_AspNetUsers_IsActive] ON [AspNetUsers] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_ExpiresAtUtc] ON [RefreshTokens] ([ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshTokens_Token] ON [RefreshTokens] ([Token]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_UserId] ON [RefreshTokens] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513120107_InitialIdentityBaseline'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513120107_InitialIdentityBaseline', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513121027_AddCustomerProfileAndRoles'
)
BEGIN
    CREATE TABLE [CustomerProfiles] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [IdNumber] nvarchar(32) NULL,
        [AddressLine1] nvarchar(200) NULL,
        [AddressLine2] nvarchar(200) NULL,
        [Suburb] nvarchar(100) NULL,
        [City] nvarchar(100) NULL,
        [Province] nvarchar(100) NULL,
        [PostalCode] nvarchar(20) NULL,
        [Country] nvarchar(100) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [Notes] nvarchar(2000) NULL,
        [PreferredContactMethod] nvarchar(32) NULL,
        [AcceptsMarketing] bit NOT NULL,
        [AcceptsPaymentReminders] bit NOT NULL,
        [AcceptsInstallationUpdates] bit NOT NULL,
        [AcceptsNetworkAlerts] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CustomerProfiles] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustomerProfiles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513121027_AddCustomerProfileAndRoles'
)
BEGIN
    CREATE INDEX [IX_CustomerProfiles_City] ON [CustomerProfiles] ([City]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513121027_AddCustomerProfileAndRoles'
)
BEGIN
    CREATE INDEX [IX_CustomerProfiles_Suburb] ON [CustomerProfiles] ([Suburb]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513121027_AddCustomerProfileAndRoles'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomerProfiles_UserId] ON [CustomerProfiles] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513121027_AddCustomerProfileAndRoles'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513121027_AddCustomerProfileAndRoles', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] uniqueidentifier NOT NULL,
        [ActorUserId] uniqueidentifier NULL,
        [ActorType] int NOT NULL,
        [ActionType] int NOT NULL,
        [EntityType] int NOT NULL,
        [EntityId] uniqueidentifier NULL,
        [EntityName] nvarchar(200) NULL,
        [Summary] nvarchar(1000) NULL,
        [MetadataJson] nvarchar(max) NULL,
        [IpAddress] nvarchar(100) NULL,
        [UserAgent] nvarchar(500) NULL,
        [IsSuccess] bit NOT NULL,
        [FailureReason] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AuditLogs_AspNetUsers_ActorUserId] FOREIGN KEY ([ActorUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_ActionType] ON [AuditLogs] ([ActionType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_ActorType] ON [AuditLogs] ([ActorType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_ActorUserId] ON [AuditLogs] ([ActorUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_CreatedAtUtc] ON [AuditLogs] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityId] ON [AuditLogs] ([EntityId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityType] ON [AuditLogs] ([EntityType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_IsSuccess] ON [AuditLogs] ([IsSuccess]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513130859_AddAuditLogs'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513130859_AddAuditLogs', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE TABLE [ServicePackages] (
        [Id] uniqueidentifier NOT NULL,
        [Type] int NOT NULL,
        [Status] int NOT NULL,
        [Name] nvarchar(150) NOT NULL,
        [Description] nvarchar(2000) NULL,
        [ShortDescription] nvarchar(500) NULL,
        [SpeedLabel] nvarchar(100) NULL,
        [DownloadSpeedMbps] int NULL,
        [UploadSpeedMbps] int NULL,
        [DataAllowanceLabel] nvarchar(100) NULL,
        [IsUncapped] bit NOT NULL,
        [Price] decimal(18,2) NOT NULL,
        [BillingCycle] int NOT NULL,
        [ContractMonths] int NULL,
        [HasFreeInstallation] bit NOT NULL,
        [InstallationFee] decimal(18,2) NULL,
        [IncludesRouter] bit NOT NULL,
        [RouterDescription] nvarchar(500) NULL,
        [IsFeatured] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [TermsSummary] nvarchar(1000) NULL,
        [CoverageNotes] nvarchar(1000) NULL,
        [ExternalReference] nvarchar(100) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ServicePackages] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_CreatedAtUtc] ON [ServicePackages] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_DisplayOrder] ON [ServicePackages] ([DisplayOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_ServicePackages_ExternalReference] ON [ServicePackages] ([ExternalReference]) WHERE [ExternalReference] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_IsFeatured] ON [ServicePackages] ([IsFeatured]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_Name] ON [ServicePackages] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_Price] ON [ServicePackages] ([Price]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_Status] ON [ServicePackages] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_Type] ON [ServicePackages] ([Type]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513132636_AddServicePackages'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513132636_AddServicePackages', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE TABLE [CoverageRequests] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CustomerProfileId] uniqueidentifier NULL,
        [ServicePackageId] uniqueidentifier NULL,
        [RequestedServiceType] int NULL,
        [Status] int NOT NULL,
        [Source] int NOT NULL,
        [FullName] nvarchar(200) NULL,
        [Email] nvarchar(256) NULL,
        [PhoneNumber] nvarchar(50) NULL,
        [AddressLine1] nvarchar(250) NOT NULL,
        [AddressLine2] nvarchar(250) NULL,
        [Suburb] nvarchar(150) NULL,
        [City] nvarchar(150) NULL,
        [Province] nvarchar(150) NULL,
        [PostalCode] nvarchar(30) NULL,
        [Country] nvarchar(100) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [GooglePlaceId] nvarchar(200) NULL,
        [MapProviderReference] nvarchar(300) NULL,
        [CustomerNotes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [CoverageResultSummary] nvarchar(1000) NULL,
        [ReviewedAtUtc] datetime2 NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CoverageRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CoverageRequests_AspNetUsers_ReviewedByUserId] FOREIGN KEY ([ReviewedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CoverageRequests_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CoverageRequests_CustomerProfiles_CustomerProfileId] FOREIGN KEY ([CustomerProfileId]) REFERENCES [CustomerProfiles] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_CoverageRequests_ServicePackages_ServicePackageId] FOREIGN KEY ([ServicePackageId]) REFERENCES [ServicePackages] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_City] ON [CoverageRequests] ([City]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_CreatedAtUtc] ON [CoverageRequests] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_CustomerProfileId] ON [CoverageRequests] ([CustomerProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_PostalCode] ON [CoverageRequests] ([PostalCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_Province] ON [CoverageRequests] ([Province]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_RequestedServiceType] ON [CoverageRequests] ([RequestedServiceType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_ReviewedAtUtc] ON [CoverageRequests] ([ReviewedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_ReviewedByUserId] ON [CoverageRequests] ([ReviewedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_ServicePackageId] ON [CoverageRequests] ([ServicePackageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_Source] ON [CoverageRequests] ([Source]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_Status] ON [CoverageRequests] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_Suburb] ON [CoverageRequests] ([Suburb]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    CREATE INDEX [IX_CoverageRequests_UserId] ON [CoverageRequests] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513140155_AddCoverageRequests'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513140155_AddCoverageRequests', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE TABLE [Orders] (
        [Id] uniqueidentifier NOT NULL,
        [OrderNumber] nvarchar(50) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CustomerProfileId] uniqueidentifier NULL,
        [ServicePackageId] uniqueidentifier NULL,
        [CoverageRequestId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [Source] int NOT NULL,
        [PackageName] nvarchar(150) NOT NULL,
        [PackageType] int NOT NULL,
        [PackageSpeedLabel] nvarchar(100) NULL,
        [PackageDataAllowanceLabel] nvarchar(100) NULL,
        [PackageIsUncapped] bit NOT NULL,
        [PackagePrice] decimal(18,2) NOT NULL,
        [PackageBillingCycle] int NOT NULL,
        [PackageContractMonths] int NULL,
        [PackageHasFreeInstallation] bit NOT NULL,
        [PackageInstallationFee] decimal(18,2) NULL,
        [PackageIncludesRouter] bit NOT NULL,
        [FullName] nvarchar(200) NULL,
        [Email] nvarchar(256) NULL,
        [PhoneNumber] nvarchar(50) NULL,
        [AddressLine1] nvarchar(250) NOT NULL,
        [AddressLine2] nvarchar(250) NULL,
        [Suburb] nvarchar(150) NULL,
        [City] nvarchar(150) NULL,
        [Province] nvarchar(150) NULL,
        [PostalCode] nvarchar(30) NULL,
        [Country] nvarchar(100) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [GooglePlaceId] nvarchar(200) NULL,
        [MapProviderReference] nvarchar(300) NULL,
        [CustomerNotes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [SubmittedAtUtc] datetime2 NULL,
        [ConfirmedAtUtc] datetime2 NULL,
        [CancelledAtUtc] datetime2 NULL,
        [ActivatedAtUtc] datetime2 NULL,
        [ExpectedInstallationDateUtc] datetime2 NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CancellationReason] nvarchar(1000) NULL,
        [FailureReason] nvarchar(1000) NULL,
        [RejectionReason] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Orders_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Orders_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Orders_CoverageRequests_CoverageRequestId] FOREIGN KEY ([CoverageRequestId]) REFERENCES [CoverageRequests] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Orders_CustomerProfiles_CustomerProfileId] FOREIGN KEY ([CustomerProfileId]) REFERENCES [CustomerProfiles] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Orders_ServicePackages_ServicePackageId] FOREIGN KEY ([ServicePackageId]) REFERENCES [ServicePackages] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_City] ON [Orders] ([City]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_ConfirmedAtUtc] ON [Orders] ([ConfirmedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_CoverageRequestId] ON [Orders] ([CoverageRequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_CreatedAtUtc] ON [Orders] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_CustomerProfileId] ON [Orders] ([CustomerProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_ExpectedInstallationDateUtc] ON [Orders] ([ExpectedInstallationDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_LastStatusChangedByUserId] ON [Orders] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Orders_OrderNumber] ON [Orders] ([OrderNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_PackageType] ON [Orders] ([PackageType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_PostalCode] ON [Orders] ([PostalCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_Province] ON [Orders] ([Province]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_ServicePackageId] ON [Orders] ([ServicePackageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_Source] ON [Orders] ([Source]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_Status] ON [Orders] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_SubmittedAtUtc] ON [Orders] ([SubmittedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_Suburb] ON [Orders] ([Suburb]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    CREATE INDEX [IX_Orders_UserId] ON [Orders] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513141142_AddOrders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513141142_AddOrders', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE TABLE [Installations] (
        [Id] uniqueidentifier NOT NULL,
        [InstallationNumber] nvarchar(50) NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [Source] int NOT NULL,
        [ScheduledForUtc] datetime2 NULL,
        [RescheduledFromUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CancelledAtUtc] datetime2 NULL,
        [FailedAtUtc] datetime2 NULL,
        [TechnicianName] nvarchar(200) NULL,
        [TechnicianPhone] nvarchar(50) NULL,
        [TechnicianEmail] nvarchar(256) NULL,
        [TechnicianUserId] uniqueidentifier NULL,
        [AddressLine1] nvarchar(250) NOT NULL,
        [AddressLine2] nvarchar(250) NULL,
        [Suburb] nvarchar(150) NULL,
        [City] nvarchar(150) NULL,
        [Province] nvarchar(150) NULL,
        [PostalCode] nvarchar(30) NULL,
        [Country] nvarchar(100) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [GooglePlaceId] nvarchar(200) NULL,
        [MapProviderReference] nvarchar(300) NULL,
        [CustomerNotes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [TechnicianNotes] nvarchar(3000) NULL,
        [CompletionNotes] nvarchar(3000) NULL,
        [FailureReason] nvarchar(1000) NULL,
        [CancellationReason] nvarchar(1000) NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Installations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Installations_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Installations_AspNetUsers_TechnicianUserId] FOREIGN KEY ([TechnicianUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Installations_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_CancelledAtUtc] ON [Installations] ([CancelledAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_City] ON [Installations] ([City]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_CompletedAtUtc] ON [Installations] ([CompletedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_CreatedAtUtc] ON [Installations] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_FailedAtUtc] ON [Installations] ([FailedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Installations_InstallationNumber] ON [Installations] ([InstallationNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_LastStatusChangedByUserId] ON [Installations] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_OrderId] ON [Installations] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_PostalCode] ON [Installations] ([PostalCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_Province] ON [Installations] ([Province]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_ScheduledForUtc] ON [Installations] ([ScheduledForUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_Source] ON [Installations] ([Source]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_Status] ON [Installations] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_Suburb] ON [Installations] ([Suburb]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    CREATE INDEX [IX_Installations_TechnicianUserId] ON [Installations] ([TechnicianUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513142859_AddInstallations'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513142859_AddInstallations', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE TABLE [DebitOrderMandates] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CustomerProfileId] uniqueidentifier NULL,
        [OrderId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [Frequency] int NOT NULL,
        [Amount] decimal(18,2) NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [PreferredDebitDay] int NULL,
        [StartDateUtc] datetime2 NULL,
        [EndDateUtc] datetime2 NULL,
        [ActivatedAtUtc] datetime2 NULL,
        [CancelledAtUtc] datetime2 NULL,
        [SuspendedAtUtc] datetime2 NULL,
        [AccountHolderName] nvarchar(200) NULL,
        [BankName] nvarchar(150) NULL,
        [BankAccountLast4] nvarchar(4) NULL,
        [BankAccountType] nvarchar(50) NULL,
        [MaskedAccountReference] nvarchar(100) NULL,
        [GatewayMandateReference] nvarchar(200) NULL,
        [ExternalReference] nvarchar(100) NULL,
        [Notes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_DebitOrderMandates] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DebitOrderMandates_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DebitOrderMandates_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DebitOrderMandates_CustomerProfiles_CustomerProfileId] FOREIGN KEY ([CustomerProfileId]) REFERENCES [CustomerProfiles] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DebitOrderMandates_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE TABLE [Invoices] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceNumber] nvarchar(50) NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [SubtotalAmount] decimal(18,2) NOT NULL,
        [TaxAmount] decimal(18,2) NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [AmountPaid] decimal(18,2) NOT NULL,
        [BalanceDue] decimal(18,2) NOT NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [IssuedAtUtc] datetime2 NULL,
        [DueAtUtc] datetime2 NULL,
        [PaidAtUtc] datetime2 NULL,
        [VoidedAtUtc] datetime2 NULL,
        [Notes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [ExternalReference] nvarchar(100) NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Invoices] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Invoices_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Invoices_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE TABLE [Payments] (
        [Id] uniqueidentifier NOT NULL,
        [PaymentNumber] nvarchar(50) NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [Method] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [PaidAtUtc] datetime2 NULL,
        [FailedAtUtc] datetime2 NULL,
        [RefundedAtUtc] datetime2 NULL,
        [GatewayName] nvarchar(100) NULL,
        [GatewayReference] nvarchar(200) NULL,
        [GatewayTransactionId] nvarchar(200) NULL,
        [ExternalReference] nvarchar(100) NULL,
        [FailureReason] nvarchar(1000) NULL,
        [Notes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Payments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Payments_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Payments_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_CreatedAtUtc] ON [DebitOrderMandates] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_CustomerProfileId] ON [DebitOrderMandates] ([CustomerProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_EndDateUtc] ON [DebitOrderMandates] ([EndDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_DebitOrderMandates_ExternalReference] ON [DebitOrderMandates] ([ExternalReference]) WHERE [ExternalReference] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_Frequency] ON [DebitOrderMandates] ([Frequency]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_GatewayMandateReference] ON [DebitOrderMandates] ([GatewayMandateReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_LastStatusChangedByUserId] ON [DebitOrderMandates] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_OrderId] ON [DebitOrderMandates] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_PreferredDebitDay] ON [DebitOrderMandates] ([PreferredDebitDay]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_StartDateUtc] ON [DebitOrderMandates] ([StartDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_Status] ON [DebitOrderMandates] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_DebitOrderMandates_UserId] ON [DebitOrderMandates] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_CreatedAtUtc] ON [Invoices] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_DueAtUtc] ON [Invoices] ([DueAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Invoices_ExternalReference] ON [Invoices] ([ExternalReference]) WHERE [ExternalReference] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Invoices_InvoiceNumber] ON [Invoices] ([InvoiceNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_IssuedAtUtc] ON [Invoices] ([IssuedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_LastStatusChangedByUserId] ON [Invoices] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_OrderId] ON [Invoices] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_PaidAtUtc] ON [Invoices] ([PaidAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_Status] ON [Invoices] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_CreatedAtUtc] ON [Payments] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Payments_ExternalReference] ON [Payments] ([ExternalReference]) WHERE [ExternalReference] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_FailedAtUtc] ON [Payments] ([FailedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_GatewayReference] ON [Payments] ([GatewayReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_GatewayTransactionId] ON [Payments] ([GatewayTransactionId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_InvoiceId] ON [Payments] ([InvoiceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_LastStatusChangedByUserId] ON [Payments] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_Method] ON [Payments] ([Method]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_PaidAtUtc] ON [Payments] ([PaidAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_PaymentNumber] ON [Payments] ([PaymentNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_RefundedAtUtc] ON [Payments] ([RefundedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Payments_Status] ON [Payments] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513144851_AddBillingFoundation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513144851_AddBillingFoundation', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE TABLE [SupportTickets] (
        [Id] uniqueidentifier NOT NULL,
        [TicketNumber] nvarchar(50) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NULL,
        [CoverageRequestId] uniqueidentifier NULL,
        [InstallationId] uniqueidentifier NULL,
        [InvoiceId] uniqueidentifier NULL,
        [PaymentId] uniqueidentifier NULL,
        [DebitOrderMandateId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [Category] int NOT NULL,
        [Priority] int NOT NULL,
        [Source] int NOT NULL,
        [Subject] nvarchar(200) NOT NULL,
        [Description] nvarchar(4000) NOT NULL,
        [AssignedToUserId] uniqueidentifier NULL,
        [FirstRespondedAtUtc] datetime2 NULL,
        [ResolvedAtUtc] datetime2 NULL,
        [ClosedAtUtc] datetime2 NULL,
        [CancelledAtUtc] datetime2 NULL,
        [ReopenedAtUtc] datetime2 NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [InternalSummary] nvarchar(2000) NULL,
        [ResolutionSummary] nvarchar(3000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_SupportTickets] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SupportTickets_AspNetUsers_AssignedToUserId] FOREIGN KEY ([AssignedToUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_CoverageRequests_CoverageRequestId] FOREIGN KEY ([CoverageRequestId]) REFERENCES [CoverageRequests] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_DebitOrderMandates_DebitOrderMandateId] FOREIGN KEY ([DebitOrderMandateId]) REFERENCES [DebitOrderMandates] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_Installations_InstallationId] FOREIGN KEY ([InstallationId]) REFERENCES [Installations] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTickets_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE TABLE [SupportTicketComments] (
        [Id] uniqueidentifier NOT NULL,
        [SupportTicketId] uniqueidentifier NOT NULL,
        [AuthorUserId] uniqueidentifier NOT NULL,
        [Body] nvarchar(4000) NOT NULL,
        [IsInternal] bit NOT NULL,
        [IsSystemGenerated] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_SupportTicketComments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SupportTicketComments_AspNetUsers_AuthorUserId] FOREIGN KEY ([AuthorUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SupportTicketComments_SupportTickets_SupportTicketId] FOREIGN KEY ([SupportTicketId]) REFERENCES [SupportTickets] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTicketComments_AuthorUserId] ON [SupportTicketComments] ([AuthorUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTicketComments_CreatedAtUtc] ON [SupportTicketComments] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTicketComments_IsInternal] ON [SupportTicketComments] ([IsInternal]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTicketComments_IsSystemGenerated] ON [SupportTicketComments] ([IsSystemGenerated]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTicketComments_SupportTicketId] ON [SupportTicketComments] ([SupportTicketId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_AssignedToUserId] ON [SupportTickets] ([AssignedToUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_Category] ON [SupportTickets] ([Category]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_ClosedAtUtc] ON [SupportTickets] ([ClosedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_CoverageRequestId] ON [SupportTickets] ([CoverageRequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_CreatedAtUtc] ON [SupportTickets] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_DebitOrderMandateId] ON [SupportTickets] ([DebitOrderMandateId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_FirstRespondedAtUtc] ON [SupportTickets] ([FirstRespondedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_InstallationId] ON [SupportTickets] ([InstallationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_InvoiceId] ON [SupportTickets] ([InvoiceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_LastStatusChangedByUserId] ON [SupportTickets] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_OrderId] ON [SupportTickets] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_PaymentId] ON [SupportTickets] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_Priority] ON [SupportTickets] ([Priority]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_ResolvedAtUtc] ON [SupportTickets] ([ResolvedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_Source] ON [SupportTickets] ([Source]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_Status] ON [SupportTickets] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SupportTickets_TicketNumber] ON [SupportTickets] ([TicketNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_UserId] ON [SupportTickets] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513175950_AddSupportTickets'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513175950_AddSupportTickets', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE TABLE [OutboundNotifications] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NULL,
        [Channel] int NOT NULL,
        [Type] int NOT NULL,
        [Status] int NOT NULL,
        [RecipientEmail] nvarchar(256) NULL,
        [RecipientPhone] nvarchar(50) NULL,
        [Subject] nvarchar(300) NULL,
        [Body] nvarchar(max) NOT NULL,
        [ProviderName] nvarchar(100) NULL,
        [ProviderMessageId] nvarchar(200) NULL,
        [FailureReason] nvarchar(2000) NULL,
        [AttemptCount] int NOT NULL,
        [LastAttemptAtUtc] datetime2 NULL,
        [SentAtUtc] datetime2 NULL,
        [RelatedEntityType] nvarchar(100) NULL,
        [RelatedEntityId] uniqueidentifier NULL,
        [MetadataJson] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OutboundNotifications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OutboundNotifications_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_Channel] ON [OutboundNotifications] ([Channel]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_CreatedAtUtc] ON [OutboundNotifications] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_RecipientEmail] ON [OutboundNotifications] ([RecipientEmail]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_RecipientPhone] ON [OutboundNotifications] ([RecipientPhone]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_RelatedEntityType_RelatedEntityId] ON [OutboundNotifications] ([RelatedEntityType], [RelatedEntityId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_SentAtUtc] ON [OutboundNotifications] ([SentAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_Status] ON [OutboundNotifications] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_Type] ON [OutboundNotifications] ([Type]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_OutboundNotifications_UserId] ON [OutboundNotifications] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513182755_AddNotifications'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513182755_AddNotifications', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE TABLE [WebhookInboxes] (
        [Id] uniqueidentifier NOT NULL,
        [Provider] int NOT NULL,
        [ProviderName] nvarchar(100) NOT NULL,
        [ProviderEventId] nvarchar(200) NULL,
        [EventType] int NOT NULL,
        [Status] int NOT NULL,
        [RawPayloadHash] nvarchar(128) NULL,
        [SignatureHeader] nvarchar(500) NULL,
        [IdempotencyKey] nvarchar(300) NULL,
        [PaymentReference] nvarchar(200) NULL,
        [InvoiceReference] nvarchar(200) NULL,
        [OrderReference] nvarchar(200) NULL,
        [GatewayTransactionId] nvarchar(200) NULL,
        [Amount] decimal(18,2) NULL,
        [CurrencyCode] nvarchar(3) NULL,
        [ProviderCreatedAtUtc] datetime2 NULL,
        [ProcessedAtUtc] datetime2 NULL,
        [FailureReason] nvarchar(2000) NULL,
        [ParsedSummaryJson] nvarchar(max) NULL,
        [PaymentId] uniqueidentifier NULL,
        [InvoiceId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_WebhookInboxes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_WebhookInboxes_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_WebhookInboxes_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_CreatedAtUtc] ON [WebhookInboxes] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_EventType] ON [WebhookInboxes] ([EventType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_GatewayTransactionId] ON [WebhookInboxes] ([GatewayTransactionId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_IdempotencyKey] ON [WebhookInboxes] ([IdempotencyKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_InvoiceId] ON [WebhookInboxes] ([InvoiceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_InvoiceReference] ON [WebhookInboxes] ([InvoiceReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_OrderReference] ON [WebhookInboxes] ([OrderReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_PaymentId] ON [WebhookInboxes] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_PaymentReference] ON [WebhookInboxes] ([PaymentReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_ProcessedAtUtc] ON [WebhookInboxes] ([ProcessedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_Provider] ON [WebhookInboxes] ([Provider]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_ProviderEventId] ON [WebhookInboxes] ([ProviderEventId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_ProviderName] ON [WebhookInboxes] ([ProviderName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WebhookInboxes_ProviderName_IdempotencyKey] ON [WebhookInboxes] ([ProviderName], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WebhookInboxes_ProviderName_ProviderEventId] ON [WebhookInboxes] ([ProviderName], [ProviderEventId]) WHERE [ProviderEventId] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_RawPayloadHash] ON [WebhookInboxes] ([RawPayloadHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    CREATE INDEX [IX_WebhookInboxes_Status] ON [WebhookInboxes] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513191219_AddWebhookInbox'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513191219_AddWebhookInbox', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE TABLE [PrivacyRequests] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Type] int NOT NULL,
        [Status] int NOT NULL,
        [RequestReason] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [RejectionReason] nvarchar(2000) NULL,
        [ReviewedAtUtc] datetime2 NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CompletedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PrivacyRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PrivacyRequests_AspNetUsers_CompletedByUserId] FOREIGN KEY ([CompletedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PrivacyRequests_AspNetUsers_ReviewedByUserId] FOREIGN KEY ([ReviewedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PrivacyRequests_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_CompletedAtUtc] ON [PrivacyRequests] ([CompletedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_CompletedByUserId] ON [PrivacyRequests] ([CompletedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_CreatedAtUtc] ON [PrivacyRequests] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_ReviewedAtUtc] ON [PrivacyRequests] ([ReviewedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_ReviewedByUserId] ON [PrivacyRequests] ([ReviewedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_Status] ON [PrivacyRequests] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_Type] ON [PrivacyRequests] ([Type]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    CREATE INDEX [IX_PrivacyRequests_UserId] ON [PrivacyRequests] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513200946_AddPrivacyRequests'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513200946_AddPrivacyRequests', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE TABLE [PaymentInitiations] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [PaymentId] uniqueidentifier NULL,
        [Provider] int NOT NULL,
        [Status] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [ProviderReference] nvarchar(200) NULL,
        [ProviderCheckoutId] nvarchar(200) NULL,
        [RedirectUrl] nvarchar(1000) NULL,
        [SuccessUrl] nvarchar(1000) NULL,
        [CancelUrl] nvarchar(1000) NULL,
        [FailureUrl] nvarchar(1000) NULL,
        [ExpiresAtUtc] datetime2 NULL,
        [FailureReason] nvarchar(2000) NULL,
        [MetadataJson] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PaymentInitiations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PaymentInitiations_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaymentInitiations_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_CreatedAtUtc] ON [PaymentInitiations] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_ExpiresAtUtc] ON [PaymentInitiations] ([ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_InvoiceId] ON [PaymentInitiations] ([InvoiceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_PaymentId] ON [PaymentInitiations] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_Provider] ON [PaymentInitiations] ([Provider]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_ProviderCheckoutId] ON [PaymentInitiations] ([ProviderCheckoutId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_ProviderReference] ON [PaymentInitiations] ([ProviderReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    CREATE INDEX [IX_PaymentInitiations_Status] ON [PaymentInitiations] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513202958_AddPaymentInitiations'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513202958_AddPaymentInitiations', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE TABLE [NetworkAccounts] (
        [Id] uniqueidentifier NOT NULL,
        [AccountNumber] nvarchar(40) NOT NULL,
        [Username] nvarchar(120) NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [Source] int NOT NULL,
        [ProviderName] nvarchar(60) NOT NULL,
        [ProviderReference] nvarchar(200) NULL,
        [PackageType] int NOT NULL,
        [PackageName] nvarchar(200) NOT NULL,
        [PackageSpeedLabel] nvarchar(100) NULL,
        [PackagePrice] decimal(18,2) NOT NULL,
        [ProvisionedAtUtc] datetime2 NULL,
        [SuspendedAtUtc] datetime2 NULL,
        [ResumedAtUtc] datetime2 NULL,
        [TerminatedAtUtc] datetime2 NULL,
        [LastPackageChangeAtUtc] datetime2 NULL,
        [AdminNotes] nvarchar(2000) NULL,
        [LastFailureReason] nvarchar(2000) NULL,
        [SuspensionReason] nvarchar(2000) NULL,
        [TerminationReason] nvarchar(2000) NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_NetworkAccounts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_NetworkAccounts_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_NetworkAccounts_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NetworkAccounts_AccountNumber] ON [NetworkAccounts] ([AccountNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_CreatedAtUtc] ON [NetworkAccounts] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_LastStatusChangedByUserId] ON [NetworkAccounts] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_OrderId] ON [NetworkAccounts] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_PackageType] ON [NetworkAccounts] ([PackageType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_ProviderName] ON [NetworkAccounts] ([ProviderName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_ProviderReference] ON [NetworkAccounts] ([ProviderReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_ProvisionedAtUtc] ON [NetworkAccounts] ([ProvisionedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_Status] ON [NetworkAccounts] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_TerminatedAtUtc] ON [NetworkAccounts] ([TerminatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NetworkAccounts_Username] ON [NetworkAccounts] ([Username]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260513212820_AddNetworkAccounts'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260513212820_AddNetworkAccounts', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260518145626_AddVerificationCodes'
)
BEGIN
    CREATE TABLE [VerificationCodes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Purpose] int NOT NULL,
        [Channel] int NOT NULL,
        [CodeHash] nvarchar(128) NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [ConsumedAtUtc] datetime2 NULL,
        [AttemptCount] int NOT NULL,
        [MaxAttempts] int NOT NULL,
        [LastAttemptAtUtc] datetime2 NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_VerificationCodes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_VerificationCodes_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260518145626_AddVerificationCodes'
)
BEGIN
    CREATE INDEX [IX_VerificationCodes_UserId_Purpose_ExpiresAtUtc] ON [VerificationCodes] ([UserId], [Purpose], [ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260518145626_AddVerificationCodes'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260518145626_AddVerificationCodes', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260518175429_AddInvoiceLineItems'
)
BEGIN
    CREATE TABLE [InvoiceLineItems] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [LineType] int NOT NULL,
        [Description] nvarchar(300) NOT NULL,
        [Quantity] int NOT NULL,
        [UnitAmount] decimal(18,2) NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [SortOrder] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_InvoiceLineItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_InvoiceLineItems_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260518175429_AddInvoiceLineItems'
)
BEGIN
    CREATE INDEX [IX_InvoiceLineItems_InvoiceId_SortOrder] ON [InvoiceLineItems] ([InvoiceId], [SortOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260518175429_AddInvoiceLineItems'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260518175429_AddInvoiceLineItems', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520102430_AddUserNumber'
)
BEGIN
    ALTER TABLE [AspNetUsers] ADD [UserNumber] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520102430_AddUserNumber'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_AspNetUsers_UserNumber] ON [AspNetUsers] ([UserNumber]) WHERE [UserNumber] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520102430_AddUserNumber'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260520102430_AddUserNumber', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520115349_AddUserPhoneNumberNormalized'
)
BEGIN
    ALTER TABLE [AspNetUsers] ADD [PhoneNumberNormalized] nvarchar(32) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520115349_AddUserPhoneNumberNormalized'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_AspNetUsers_PhoneNumberNormalized] ON [AspNetUsers] ([PhoneNumberNormalized]) WHERE [PhoneNumberNormalized] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520115349_AddUserPhoneNumberNormalized'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260520115349_AddUserPhoneNumberNormalized', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520150438_AddRequestedInstallationDate'
)
BEGIN
    ALTER TABLE [Orders] ADD [RequestedInstallationDateUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260520150438_AddRequestedInstallationDate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260520150438_AddRequestedInstallationDate', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE TABLE [OrderIntents] (
        [Id] uniqueidentifier NOT NULL,
        [IntentToken] nvarchar(128) NOT NULL,
        [ServicePackageId] uniqueidentifier NOT NULL,
        [FullName] nvarchar(200) NULL,
        [Email] nvarchar(256) NULL,
        [PhoneNumber] nvarchar(50) NULL,
        [AddressLine1] nvarchar(250) NULL,
        [AddressLine2] nvarchar(250) NULL,
        [Suburb] nvarchar(150) NULL,
        [City] nvarchar(150) NULL,
        [Province] nvarchar(150) NULL,
        [PostalCode] nvarchar(30) NULL,
        [Country] nvarchar(100) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [GooglePlaceId] nvarchar(200) NULL,
        [MapProviderReference] nvarchar(300) NULL,
        [RequestedInstallationDateUtc] datetime2 NULL,
        [CustomerNotes] nvarchar(2000) NULL,
        [Status] int NOT NULL,
        [ClaimedByUserId] uniqueidentifier NULL,
        [ConvertedOrderId] uniqueidentifier NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [ClaimedAtUtc] datetime2 NULL,
        [ConvertedAtUtc] datetime2 NULL,
        [Source] nvarchar(50) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_OrderIntents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OrderIntents_AspNetUsers_ClaimedByUserId] FOREIGN KEY ([ClaimedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_OrderIntents_Orders_ConvertedOrderId] FOREIGN KEY ([ConvertedOrderId]) REFERENCES [Orders] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_OrderIntents_ServicePackages_ServicePackageId] FOREIGN KEY ([ServicePackageId]) REFERENCES [ServicePackages] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_ClaimedByUserId] ON [OrderIntents] ([ClaimedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_ConvertedOrderId] ON [OrderIntents] ([ConvertedOrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_Email] ON [OrderIntents] ([Email]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_ExpiresAtUtc] ON [OrderIntents] ([ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OrderIntents_IntentToken] ON [OrderIntents] ([IntentToken]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_ServicePackageId] ON [OrderIntents] ([ServicePackageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_Status] ON [OrderIntents] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521130606_AddOrderIntents'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260521130606_AddOrderIntents', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521150309_AddPortalAuthHandoffTokens'
)
BEGIN
    CREATE TABLE [PortalAuthHandoffTokens] (
        [Id] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(128) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [IntentToken] nvarchar(128) NULL,
        [Purpose] int NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [ConsumedAtUtc] datetime2 NULL,
        [CreatedIpAddress] nvarchar(64) NULL,
        [CreatedUserAgent] nvarchar(512) NULL,
        [ConsumedIpAddress] nvarchar(64) NULL,
        [ConsumedUserAgent] nvarchar(512) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PortalAuthHandoffTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PortalAuthHandoffTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521150309_AddPortalAuthHandoffTokens'
)
BEGIN
    CREATE INDEX [IX_PortalAuthHandoffTokens_ConsumedAtUtc] ON [PortalAuthHandoffTokens] ([ConsumedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521150309_AddPortalAuthHandoffTokens'
)
BEGIN
    CREATE INDEX [IX_PortalAuthHandoffTokens_ExpiresAtUtc] ON [PortalAuthHandoffTokens] ([ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521150309_AddPortalAuthHandoffTokens'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PortalAuthHandoffTokens_TokenHash] ON [PortalAuthHandoffTokens] ([TokenHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521150309_AddPortalAuthHandoffTokens'
)
BEGIN
    CREATE INDEX [IX_PortalAuthHandoffTokens_UserId] ON [PortalAuthHandoffTokens] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260521150309_AddPortalAuthHandoffTokens'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260521150309_AddPortalAuthHandoffTokens', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260522084940_AddOrderIntentLegalConsent'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [PrivacyAcknowledgedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260522084940_AddOrderIntentLegalConsent'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [PrivacyVersion] nvarchar(20) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260522084940_AddOrderIntentLegalConsent'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [TermsAcceptedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260522084940_AddOrderIntentLegalConsent'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [TermsVersion] nvarchar(20) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260522084940_AddOrderIntentLegalConsent'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260522084940_AddOrderIntentLegalConsent', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260525214307_CoverageRequestNullableUserNoAction'
)
BEGIN
    ALTER TABLE [CoverageRequests] DROP CONSTRAINT [FK_CoverageRequests_AspNetUsers_UserId];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260525214307_CoverageRequestNullableUserNoAction'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[CoverageRequests]') AND [c].[name] = N'UserId');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [CoverageRequests] DROP CONSTRAINT [' + @var0 + '];');
    ALTER TABLE [CoverageRequests] ALTER COLUMN [UserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260525214307_CoverageRequestNullableUserNoAction'
)
BEGIN
    ALTER TABLE [CoverageRequests] ADD CONSTRAINT [FK_CoverageRequests_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260525214307_CoverageRequestNullableUserNoAction'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260525214307_CoverageRequestNullableUserNoAction', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE TABLE [ServiceChangeRequests] (
        [Id] uniqueidentifier NOT NULL,
        [RequestNumber] nvarchar(50) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [NetworkAccountId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NULL,
        [CurrentPackageId] uniqueidentifier NULL,
        [CurrentPackageName] nvarchar(150) NOT NULL,
        [CurrentMonthlyPrice] decimal(18,2) NOT NULL,
        [CurrentBillingCycle] int NOT NULL,
        [RequestedPackageId] uniqueidentifier NOT NULL,
        [RequestedPackageName] nvarchar(150) NOT NULL,
        [RequestedMonthlyPrice] decimal(18,2) NOT NULL,
        [RequestedBillingCycle] int NOT NULL,
        [ChangeType] int NOT NULL,
        [EffectiveMode] int NOT NULL,
        [Status] int NOT NULL,
        [Source] int NOT NULL,
        [ProRataAmount] decimal(18,2) NOT NULL,
        [ProRataCycleDays] int NOT NULL,
        [ProRataRemainingDays] int NOT NULL,
        [EffectiveDateUtc] datetime2 NOT NULL,
        [InvoiceId] uniqueidentifier NULL,
        [PaymentId] uniqueidentifier NULL,
        [CustomerNotes] nvarchar(2000) NULL,
        [AdminNotes] nvarchar(3000) NULL,
        [AppliedAtUtc] datetime2 NULL,
        [CancelledAtUtc] datetime2 NULL,
        [RejectedAtUtc] datetime2 NULL,
        [FailedAtUtc] datetime2 NULL,
        [CancellationReason] nvarchar(1000) NULL,
        [RejectionReason] nvarchar(1000) NULL,
        [FailureReason] nvarchar(1000) NULL,
        [LastStatusChangedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ServiceChangeRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServiceChangeRequests_AspNetUsers_LastStatusChangedByUserId] FOREIGN KEY ([LastStatusChangedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ServiceChangeRequests_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ServiceChangeRequests_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_ServiceChangeRequests_NetworkAccounts_NetworkAccountId] FOREIGN KEY ([NetworkAccountId]) REFERENCES [NetworkAccounts] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ServiceChangeRequests_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_ServiceChangeRequests_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_ServiceChangeRequests_ServicePackages_RequestedPackageId] FOREIGN KEY ([RequestedPackageId]) REFERENCES [ServicePackages] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_ChangeType] ON [ServiceChangeRequests] ([ChangeType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_CreatedAtUtc] ON [ServiceChangeRequests] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_EffectiveDateUtc] ON [ServiceChangeRequests] ([EffectiveDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_EffectiveMode] ON [ServiceChangeRequests] ([EffectiveMode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_InvoiceId] ON [ServiceChangeRequests] ([InvoiceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_LastStatusChangedByUserId] ON [ServiceChangeRequests] ([LastStatusChangedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_NetworkAccountId] ON [ServiceChangeRequests] ([NetworkAccountId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_OrderId] ON [ServiceChangeRequests] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_PaymentId] ON [ServiceChangeRequests] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_RequestedPackageId] ON [ServiceChangeRequests] ([RequestedPackageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServiceChangeRequests_RequestNumber] ON [ServiceChangeRequests] ([RequestNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_Source] ON [ServiceChangeRequests] ([Source]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_Status] ON [ServiceChangeRequests] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    CREATE INDEX [IX_ServiceChangeRequests_UserId] ON [ServiceChangeRequests] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260526164226_AddServiceChangeRequests'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260526164226_AddServiceChangeRequests', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [BurstSpeedMbps] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [ProvisioningType] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [RadiusProfileId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [RequiresProvisioning] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [CurrentIpAddress] nvarchar(45) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [LastProvisioningAttemptUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [NasIdentifier] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [PasswordHash] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [ProvisioningAttemptCount] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [ProvisioningStatus] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD [RadiusProfileId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE TABLE [ProvisioningEvents] (
        [Id] uniqueidentifier NOT NULL,
        [NetworkAccountId] uniqueidentifier NOT NULL,
        [EventType] int NOT NULL,
        [ProviderName] nvarchar(60) NOT NULL,
        [ProviderReference] nvarchar(200) NULL,
        [IsSuccess] bit NOT NULL,
        [FailureReason] nvarchar(2000) NULL,
        [Summary] nvarchar(500) NULL,
        [MetadataJson] nvarchar(4000) NULL,
        [TriggeredByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ProvisioningEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ProvisioningEvents_AspNetUsers_TriggeredByUserId] FOREIGN KEY ([TriggeredByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProvisioningEvents_NetworkAccounts_NetworkAccountId] FOREIGN KEY ([NetworkAccountId]) REFERENCES [NetworkAccounts] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE TABLE [RadiusProfiles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [DownloadMbps] int NOT NULL,
        [UploadMbps] int NOT NULL,
        [BurstMbps] int NULL,
        [Priority] int NOT NULL,
        [IsActive] bit NOT NULL,
        [Notes] nvarchar(2000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_RadiusProfiles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_RadiusProfileId] ON [ServicePackages] ([RadiusProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_RequiresProvisioning] ON [ServicePackages] ([RequiresProvisioning]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_ProvisioningStatus] ON [NetworkAccounts] ([ProvisioningStatus]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_NetworkAccounts_RadiusProfileId] ON [NetworkAccounts] ([RadiusProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ProvisioningEvents_CreatedAtUtc] ON [ProvisioningEvents] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ProvisioningEvents_EventType] ON [ProvisioningEvents] ([EventType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ProvisioningEvents_IsSuccess] ON [ProvisioningEvents] ([IsSuccess]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ProvisioningEvents_NetworkAccountId] ON [ProvisioningEvents] ([NetworkAccountId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_ProvisioningEvents_TriggeredByUserId] ON [ProvisioningEvents] ([TriggeredByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE INDEX [IX_RadiusProfiles_IsActive] ON [RadiusProfiles] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RadiusProfiles_Name] ON [RadiusProfiles] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [NetworkAccounts] ADD CONSTRAINT [FK_NetworkAccounts_RadiusProfiles_RadiusProfileId] FOREIGN KEY ([RadiusProfileId]) REFERENCES [RadiusProfiles] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD CONSTRAINT [FK_ServicePackages_RadiusProfiles_RadiusProfileId] FOREIGN KEY ([RadiusProfileId]) REFERENCES [RadiusProfiles] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260528121943_AddProvisioningArchitecture'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260528121943_AddProvisioningArchitecture', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    ALTER TABLE [PaymentInitiations] ADD [WebhookApplyMode] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    ALTER TABLE [PaymentInitiations] ADD [WebhookLastReceivedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    ALTER TABLE [CustomerProfiles] ADD [AutoBillingEnabled] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    CREATE TABLE [CustomerPaymentMandates] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Provider] int NOT NULL,
        [ProviderCustomerCode] nvarchar(200) NULL,
        [AuthorizationCodeProtected] nvarchar(2000) NOT NULL,
        [AuthorizationSignature] nvarchar(200) NULL,
        [Channel] nvarchar(40) NULL,
        [CardType] nvarchar(40) NULL,
        [Bank] nvarchar(120) NULL,
        [Last4] nvarchar(4) NULL,
        [ExpMonth] nvarchar(2) NULL,
        [ExpYear] nvarchar(4) NULL,
        [AccountName] nvarchar(200) NULL,
        [CustomerEmail] nvarchar(320) NULL,
        [IsReusable] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [IsDefault] bit NOT NULL,
        [ConsentGivenUtc] datetime2 NULL,
        [ConsentRevokedUtc] datetime2 NULL,
        [ConsentSource] int NOT NULL,
        [LastSuccessfulChargeUtc] datetime2 NULL,
        [LastFailedChargeUtc] datetime2 NULL,
        [ConsecutiveFailureCount] int NOT NULL,
        [MetadataJson] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CustomerPaymentMandates] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustomerPaymentMandates_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    CREATE INDEX [IX_CustomerPaymentMandates_IsActive] ON [CustomerPaymentMandates] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    CREATE INDEX [IX_CustomerPaymentMandates_IsDefault] ON [CustomerPaymentMandates] ([IsDefault]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    CREATE INDEX [IX_CustomerPaymentMandates_UserId] ON [CustomerPaymentMandates] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    CREATE INDEX [IX_CustomerPaymentMandates_UserId_Provider] ON [CustomerPaymentMandates] ([UserId], [Provider]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_CustomerPaymentMandates_UserId_Provider_AuthorizationSignature] ON [CustomerPaymentMandates] ([UserId], [Provider], [AuthorizationSignature]) WHERE [AuthorizationSignature] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530105550_AddColumn'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260530105550_AddColumn', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [Payments] ADD [ActualProviderAmount] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [Payments] ADD [InvoiceAmountAtTime] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [Payments] ADD [IsTestAmountOverrideApplied] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [Payments] ADD [TestOverrideReason] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [PaymentInitiations] ADD [ActualProviderAmount] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [PaymentInitiations] ADD [InvoiceAmountAtTime] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    ALTER TABLE [PaymentInitiations] ADD [IsTestAmountOverrideApplied] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE TABLE [PaymentRetryAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [MandateId] uniqueidentifier NULL,
        [Provider] int NOT NULL,
        [AttemptNumber] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [ProviderAmount] decimal(18,2) NULL,
        [Status] int NOT NULL,
        [FailureReason] nvarchar(2000) NULL,
        [ProviderReference] nvarchar(200) NULL,
        [Source] int NOT NULL,
        [ScheduledForUtc] datetime2 NOT NULL,
        [AttemptedUtc] datetime2 NULL,
        [PaymentId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PaymentRetryAttempts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PaymentRetryAttempts_AspNetUsers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaymentRetryAttempts_CustomerPaymentMandates_MandateId] FOREIGN KEY ([MandateId]) REFERENCES [CustomerPaymentMandates] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_PaymentRetryAttempts_Invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaymentRetryAttempts_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE INDEX [IX_PaymentRetryAttempts_CustomerId] ON [PaymentRetryAttempts] ([CustomerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE INDEX [IX_PaymentRetryAttempts_InvoiceId] ON [PaymentRetryAttempts] ([InvoiceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE INDEX [IX_PaymentRetryAttempts_MandateId] ON [PaymentRetryAttempts] ([MandateId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE INDEX [IX_PaymentRetryAttempts_PaymentId] ON [PaymentRetryAttempts] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE INDEX [IX_PaymentRetryAttempts_ProviderReference] ON [PaymentRetryAttempts] ([ProviderReference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    CREATE INDEX [IX_PaymentRetryAttempts_Status_ScheduledForUtc] ON [PaymentRetryAttempts] ([Status], [ScheduledForUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260530132718_AddAutoBillingTestOverrideAndRetryAttempts', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [ActivatedByUserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [ActivationNotes] nvarchar(2000) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [BillingAnchorDateUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [NextPayDateUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Orders] ADD [OpenserveActivationReference] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [CustomerSignOffName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [InstalledLocationNotes] nvarchar(2000) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [OntReference] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [RouterMacAddress] nvarchar(50) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [RouterMakeModel] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [RouterSerialNumber] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Installations] ADD [SpeedTestResult] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    CREATE INDEX [IX_Orders_ActivatedByUserId] ON [Orders] ([ActivatedByUserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    CREATE INDEX [IX_Orders_NextPayDateUtc] ON [Orders] ([NextPayDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    ALTER TABLE [Orders] ADD CONSTRAINT [FK_Orders_AspNetUsers_ActivatedByUserId] FOREIGN KEY ([ActivatedByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260531122008_AddGoLiveOrderLifecycleAndTechnicianFields', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531162039_AddPaystackWebhookLog'
)
BEGIN
    CREATE TABLE [PaystackWebhookLogs] (
        [Id] uniqueidentifier NOT NULL,
        [Provider] int NOT NULL,
        [ReceivedAtUtc] datetime2 NOT NULL,
        [RawBodyLength] int NOT NULL,
        [SignaturePresent] bit NOT NULL,
        [SignatureValid] bit NOT NULL,
        [Event] nvarchar(80) NULL,
        [Reference] nvarchar(200) NULL,
        [AmountSubunits] bigint NULL,
        [Currency] nvarchar(3) NULL,
        [Status] nvarchar(40) NULL,
        [PaymentInitiationId] uniqueidentifier NULL,
        [PaymentId] uniqueidentifier NULL,
        [InvoiceId] uniqueidentifier NULL,
        [Accepted] bit NOT NULL,
        [OutcomeMessage] nvarchar(500) NULL,
        [RejectionReason] nvarchar(200) NULL,
        [ApplyAttempted] bit NOT NULL,
        [ApplySucceeded] bit NOT NULL,
        [ApplyErrorCode] nvarchar(80) NULL,
        [ApplyErrorMessage] nvarchar(500) NULL,
        [HttpStatusReturned] int NOT NULL,
        [EnvironmentName] nvarchar(50) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_PaystackWebhookLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531162039_AddPaystackWebhookLog'
)
BEGIN
    CREATE INDEX [IX_PaystackWebhookLogs_ReceivedAtUtc] ON [PaystackWebhookLogs] ([ReceivedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531162039_AddPaystackWebhookLog'
)
BEGIN
    CREATE INDEX [IX_PaystackWebhookLogs_Reference] ON [PaystackWebhookLogs] ([Reference]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531162039_AddPaystackWebhookLog'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260531162039_AddPaystackWebhookLog', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentInvoiceAmountAtTime] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentIsTestAmountOverrideApplied] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentPaymentAccessCode] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentPaymentAmount] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentPaymentInitiatedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentPaymentRedirectUrl] nvarchar(1000) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [IntentPaymentReference] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_OrderIntents_IntentPaymentReference] ON [OrderIntents] ([IntentPaymentReference]) WHERE [IntentPaymentReference] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260531205558_AddOrderIntentPaystackFields'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260531205558_AddOrderIntentPaystackFields', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260603143010_AddServicesToCoverageRequest'
)
BEGIN
    ALTER TABLE [CoverageRequests] ADD [Services] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260603143010_AddServicesToCoverageRequest'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260603143010_AddServicesToCoverageRequest', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260604082645_AddIsTestAccount'
)
BEGIN
    ALTER TABLE [AspNetUsers] ADD [IsTestAccount] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260604082645_AddIsTestAccount'
)
BEGIN
    CREATE INDEX [IX_AspNetUsers_IsTestAccount] ON [AspNetUsers] ([IsTestAccount]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260604082645_AddIsTestAccount'
)
BEGIN
    UPDATE [AspNetUsers] SET [IsTestAccount] = 1 WHERE [NormalizedEmail] LIKE 'CUSTOMER1[0-9][0-9][0-9]@GMAIL.COM';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260604082645_AddIsTestAccount'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260604082645_AddIsTestAccount', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260609100945_AddOrderIntentProvider'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [Provider] int NOT NULL DEFAULT 3;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260609100945_AddOrderIntentProvider'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260609100945_AddOrderIntentProvider', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    ALTER TABLE [Invoices] ADD [PeriodEndUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    ALTER TABLE [Invoices] ADD [PeriodStartUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    ALTER TABLE [Invoices] ADD [ServiceBillingScheduleId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE TABLE [BillingRunLogs] (
        [Id] uniqueidentifier NOT NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [FinishedAtUtc] datetime2 NULL,
        [Status] int NOT NULL,
        [TriggeredBy] int NOT NULL,
        [DryRun] bit NOT NULL,
        [InvoicesGenerated] int NOT NULL,
        [ChargesAttempted] int NOT NULL,
        [ChargesSucceeded] int NOT NULL,
        [ChargesFailed] int NOT NULL,
        [RetriesProcessed] int NOT NULL,
        [SuspensionCandidates] int NOT NULL,
        [ErrorCount] int NOT NULL,
        [MachineName] nvarchar(200) NULL,
        [InstanceId] nvarchar(64) NOT NULL,
        [SummaryJson] nvarchar(4000) NULL,
        [ErrorText] nvarchar(4000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_BillingRunLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE TABLE [ServiceBillingSchedules] (
        [Id] uniqueidentifier NOT NULL,
        [NetworkAccountId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [BillingCycle] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [CurrencyCode] nvarchar(3) NOT NULL,
        [AnchorDayOfMonth] int NOT NULL,
        [CurrentPeriodStartUtc] datetime2 NULL,
        [CurrentPeriodEndUtc] datetime2 NULL,
        [NextInvoiceDateUtc] datetime2 NULL,
        [NextDueDateUtc] datetime2 NULL,
        [LastInvoicedPeriodEndUtc] datetime2 NULL,
        [LastInvoiceId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [IsAutoBillable] bit NOT NULL,
        [Notes] nvarchar(2000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ServiceBillingSchedules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServiceBillingSchedules_NetworkAccounts_NetworkAccountId] FOREIGN KEY ([NetworkAccountId]) REFERENCES [NetworkAccounts] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_Invoices_ServiceBillingScheduleId] ON [Invoices] ([ServiceBillingScheduleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_BillingRunLogs_StartedAtUtc] ON [BillingRunLogs] ([StartedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_BillingRunLogs_Status_StartedAtUtc] ON [BillingRunLogs] ([Status], [StartedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_ServiceBillingSchedules_NetworkAccountId] ON [ServiceBillingSchedules] ([NetworkAccountId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_ServiceBillingSchedules_OrderId] ON [ServiceBillingSchedules] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_ServiceBillingSchedules_Status_NextInvoiceDateUtc] ON [ServiceBillingSchedules] ([Status], [NextInvoiceDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    CREATE INDEX [IX_ServiceBillingSchedules_UserId] ON [ServiceBillingSchedules] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    ALTER TABLE [Invoices] ADD CONSTRAINT [FK_Invoices_ServiceBillingSchedules_ServiceBillingScheduleId] FOREIGN KEY ([ServiceBillingScheduleId]) REFERENCES [ServiceBillingSchedules] ([Id]) ON DELETE SET NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613172626_Phase0A_RecurringBillingFoundation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260613172626_Phase0A_RecurringBillingFoundation', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613175133_Phase0B_RecurringInvoiceUniqueIndex'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Invoices_ServiceBillingScheduleId_PeriodStartUtc] ON [Invoices] ([ServiceBillingScheduleId], [PeriodStartUtc]) WHERE [ServiceBillingScheduleId] IS NOT NULL AND [PeriodStartUtc] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260613175133_Phase0B_RecurringInvoiceUniqueIndex'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260613175133_Phase0B_RecurringInvoiceUniqueIndex', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260614145347_Phase_MobileAppVersionRules'
)
BEGIN
    CREATE TABLE [MobileAppVersionRules] (
        [Id] uniqueidentifier NOT NULL,
        [Platform] int NOT NULL,
        [Channel] int NOT NULL,
        [LatestVersion] nvarchar(32) NOT NULL,
        [LatestBuildNumber] int NOT NULL,
        [MinimumSupportedVersion] nvarchar(32) NOT NULL,
        [MinimumSupportedBuildNumber] int NOT NULL,
        [UpdateRequired] bit NOT NULL,
        [UpdateAvailable] bit NOT NULL,
        [IsEnabled] bit NOT NULL,
        [Title] nvarchar(120) NOT NULL,
        [Message] nvarchar(600) NOT NULL,
        [PrimaryButtonText] nvarchar(40) NOT NULL,
        [SecondaryButtonText] nvarchar(40) NULL,
        [StoreUrl] nvarchar(500) NOT NULL,
        [ReleaseNotes] nvarchar(2000) NULL,
        [UpdatedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_MobileAppVersionRules] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260614145347_Phase_MobileAppVersionRules'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Channel', N'CreatedAtUtc', N'IsEnabled', N'LatestBuildNumber', N'LatestVersion', N'Message', N'MinimumSupportedBuildNumber', N'MinimumSupportedVersion', N'Platform', N'PrimaryButtonText', N'ReleaseNotes', N'SecondaryButtonText', N'StoreUrl', N'Title', N'UpdateAvailable', N'UpdateRequired', N'UpdatedAtUtc', N'UpdatedByUserId') AND [object_id] = OBJECT_ID(N'[MobileAppVersionRules]'))
        SET IDENTITY_INSERT [MobileAppVersionRules] ON;
    EXEC(N'INSERT INTO [MobileAppVersionRules] ([Id], [Channel], [CreatedAtUtc], [IsEnabled], [LatestBuildNumber], [LatestVersion], [Message], [MinimumSupportedBuildNumber], [MinimumSupportedVersion], [Platform], [PrimaryButtonText], [ReleaseNotes], [SecondaryButtonText], [StoreUrl], [Title], [UpdateAvailable], [UpdateRequired], [UpdatedAtUtc], [UpdatedByUserId])
    VALUES (''a1111111-1111-1111-1111-111111111111'', 0, ''2026-06-14T00:00:00.0000000Z'', CAST(1 AS bit), 0, N''1.0.1'', N''A new version of SmartFuture is available with improvements and fixes.'', 0, N''1.0.0'', 0, N''Update app'', NULL, N''Later'', N''https://play.google.com/store/apps/details?id=com.smartfuture.app'', N''Update available'', CAST(0 AS bit), CAST(0 AS bit), NULL, NULL),
    (''a2222222-2222-2222-2222-222222222222'', 1, ''2026-06-14T00:00:00.0000000Z'', CAST(1 AS bit), 0, N''1.0.1'', N''A new version of SmartFuture is available with improvements and fixes.'', 0, N''1.0.0'', 0, N''Update app'', NULL, N''Later'', N''https://appgallery.huawei.com/app/C118015685'', N''Update available'', CAST(0 AS bit), CAST(0 AS bit), NULL, NULL),
    (''a3333333-3333-3333-3333-333333333333'', 2, ''2026-06-14T00:00:00.0000000Z'', CAST(1 AS bit), 0, N''1.0.1'', N''A new version of SmartFuture is available with improvements and fixes.'', 0, N''1.0.0'', 1, N''Update app'', NULL, N''Later'', N'''', N''Update available'', CAST(0 AS bit), CAST(0 AS bit), NULL, NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Channel', N'CreatedAtUtc', N'IsEnabled', N'LatestBuildNumber', N'LatestVersion', N'Message', N'MinimumSupportedBuildNumber', N'MinimumSupportedVersion', N'Platform', N'PrimaryButtonText', N'ReleaseNotes', N'SecondaryButtonText', N'StoreUrl', N'Title', N'UpdateAvailable', N'UpdateRequired', N'UpdatedAtUtc', N'UpdatedByUserId') AND [object_id] = OBJECT_ID(N'[MobileAppVersionRules]'))
        SET IDENTITY_INSERT [MobileAppVersionRules] OFF;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260614145347_Phase_MobileAppVersionRules'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MobileAppVersionRules_Platform_Channel] ON [MobileAppVersionRules] ([Platform], [Channel]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260614145347_Phase_MobileAppVersionRules'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260614145347_Phase_MobileAppVersionRules', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260628080314_AddSecurityServiceTypeAndImage'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [ImageStorageKey] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260628080314_AddSecurityServiceTypeAndImage'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [ImageUrl] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260628080314_AddSecurityServiceTypeAndImage'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260628080314_AddSecurityServiceTypeAndImage', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [FeaturesJson] nvarchar(4000) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    ALTER TABLE [Orders] ADD [FirstProRataInvoiceGeneratedAtUtc] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    ALTER TABLE [Orders] ADD [PreferredBillingDay] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [PreferredBillingDay] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    CREATE TABLE [BillingDayOptions] (
        [Id] uniqueidentifier NOT NULL,
        [Day] int NOT NULL,
        [Label] nvarchar(100) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [IsDefault] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_BillingDayOptions] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    CREATE UNIQUE INDEX [IX_BillingDayOptions_Day] ON [BillingDayOptions] ([Day]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    CREATE INDEX [IX_BillingDayOptions_DisplayOrder] ON [BillingDayOptions] ([DisplayOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    CREATE INDEX [IX_BillingDayOptions_IsEnabled] ON [BillingDayOptions] ([IsEnabled]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260701164213_AddBillingDayOptionsAndServicePackageFeatures', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD [SubTypeId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [Orders] ADD [PackageVariantName] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [Orders] ADD [ServicePackageVariantId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD [ServicePackageVariantId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE TABLE [ServicePackageSubTypes] (
        [Id] uniqueidentifier NOT NULL,
        [PackageType] int NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Slug] nvarchar(100) NOT NULL,
        [IsActive] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ServicePackageSubTypes] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE TABLE [ServicePackageVariants] (
        [Id] uniqueidentifier NOT NULL,
        [ServicePackageId] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Price] decimal(18,2) NOT NULL,
        [InstallationFee] decimal(18,2) NULL,
        [HasFreeInstallation] bit NULL,
        [IsActive] bit NOT NULL,
        [DisplayOrder] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_ServicePackageVariants] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ServicePackageVariants_ServicePackages_ServicePackageId] FOREIGN KEY ([ServicePackageId]) REFERENCES [ServicePackages] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CreatedAtUtc', N'DisplayOrder', N'IsActive', N'Name', N'PackageType', N'Slug', N'UpdatedAtUtc') AND [object_id] = OBJECT_ID(N'[ServicePackageSubTypes]'))
        SET IDENTITY_INSERT [ServicePackageSubTypes] ON;
    EXEC(N'INSERT INTO [ServicePackageSubTypes] ([Id], [CreatedAtUtc], [DisplayOrder], [IsActive], [Name], [PackageType], [Slug], [UpdatedAtUtc])
    VALUES (''5b1c0001-0000-4000-8000-0000000000c1'', ''2026-07-01T00:00:00.0000000Z'', 10, CAST(1 AS bit), N''CCTV'', 6, N''cctv'', NULL),
    (''5b1c0002-0000-4000-8000-0000000000c2'', ''2026-07-01T00:00:00.0000000Z'', 20, CAST(1 AS bit), N''Intercom'', 6, N''intercom'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CreatedAtUtc', N'DisplayOrder', N'IsActive', N'Name', N'PackageType', N'Slug', N'UpdatedAtUtc') AND [object_id] = OBJECT_ID(N'[ServicePackageSubTypes]'))
        SET IDENTITY_INSERT [ServicePackageSubTypes] OFF;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackages_SubTypeId] ON [ServicePackages] ([SubTypeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_Orders_ServicePackageVariantId] ON [Orders] ([ServicePackageVariantId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_OrderIntents_ServicePackageVariantId] ON [OrderIntents] ([ServicePackageVariantId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackageSubTypes_DisplayOrder] ON [ServicePackageSubTypes] ([DisplayOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackageSubTypes_IsActive] ON [ServicePackageSubTypes] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackageSubTypes_PackageType] ON [ServicePackageSubTypes] ([PackageType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServicePackageSubTypes_PackageType_Slug] ON [ServicePackageSubTypes] ([PackageType], [Slug]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackageVariants_DisplayOrder] ON [ServicePackageVariants] ([DisplayOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackageVariants_IsActive] ON [ServicePackageVariants] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    CREATE INDEX [IX_ServicePackageVariants_ServicePackageId] ON [ServicePackageVariants] ([ServicePackageId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [OrderIntents] ADD CONSTRAINT [FK_OrderIntents_ServicePackageVariants_ServicePackageVariantId] FOREIGN KEY ([ServicePackageVariantId]) REFERENCES [ServicePackageVariants] ([Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [Orders] ADD CONSTRAINT [FK_Orders_ServicePackageVariants_ServicePackageVariantId] FOREIGN KEY ([ServicePackageVariantId]) REFERENCES [ServicePackageVariants] ([Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    ALTER TABLE [ServicePackages] ADD CONSTRAINT [FK_ServicePackages_ServicePackageSubTypes_SubTypeId] FOREIGN KEY ([SubTypeId]) REFERENCES [ServicePackageSubTypes] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701182657_AddServicePackageSubTypesAndVariants'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260701182657_AddServicePackageSubTypesAndVariants', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701192951_AddCoverageMapRules'
)
BEGIN
    CREATE TABLE [CoverageMapRules] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [MatchText] nvarchar(200) NOT NULL,
        [RuleType] int NOT NULL,
        [MatchMode] int NOT NULL,
        [AllowedComponents] int NOT NULL,
        [IsActive] bit NOT NULL,
        [Priority] int NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_CoverageMapRules] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701192951_AddCoverageMapRules'
)
BEGIN
    CREATE INDEX [IX_CoverageMapRules_IsActive_RuleType_Priority] ON [CoverageMapRules] ([IsActive], [RuleType], [Priority]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701192951_AddCoverageMapRules'
)
BEGIN
    CREATE INDEX [IX_CoverageMapRules_MatchText] ON [CoverageMapRules] ([MatchText]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260701192951_AddCoverageMapRules'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260701192951_AddCoverageMapRules', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobAlertDeliveryLogs] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [Frequency] int NOT NULL,
        [WindowStartUtc] datetime2 NOT NULL,
        [WindowEndUtc] datetime2 NOT NULL,
        [JobCount] int NOT NULL,
        [RecipientEmail] nvarchar(256) NULL,
        [Subject] nvarchar(300) NULL,
        [FailureMessage] nvarchar(2000) NULL,
        [SentAtUtc] datetime2 NULL,
        [NotificationId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobAlertDeliveryLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JobAlertDeliveryLogs_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobAlertPreferences] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [IsSubscribed] bit NOT NULL,
        [Frequency] int NOT NULL,
        [CategoriesJson] nvarchar(2000) NULL,
        [LocationsJson] nvarchar(2000) NULL,
        [KeywordsJson] nvarchar(2000) NULL,
        [LastSentAtUtc] datetime2 NULL,
        [UnsubscribedAtUtc] datetime2 NULL,
        [UnsubscribeToken] nvarchar(128) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobAlertPreferences] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JobAlertPreferences_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobModuleSettings] (
        [Id] uniqueidentifier NOT NULL,
        [JobDetailsSubscribersOnly] bit NOT NULL,
        [JobAlertsEnabled] bit NOT NULL,
        [JobsModuleEnabled] bit NOT NULL,
        [AutoImportEnabled] bit NOT NULL,
        [ImportIntervalMinutes] int NOT NULL,
        [ExpiredJobRetentionDays] int NOT NULL,
        [PublicDisclaimer] nvarchar(1000) NULL,
        [UpdatedByUserId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobModuleSettings] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobSources] (
        [Id] uniqueidentifier NOT NULL,
        [SourceName] nvarchar(150) NOT NULL,
        [SourceUrl] nvarchar(1000) NOT NULL,
        [SourceType] int NOT NULL,
        [IsActive] bit NOT NULL,
        [Notes] nvarchar(2000) NULL,
        [DefaultCategory] nvarchar(100) NULL,
        [DefaultLocation] nvarchar(200) NULL,
        [CrawlFrequencyMinutes] int NULL,
        [AutoPublish] bit NOT NULL,
        [MaxJobsPerRun] int NOT NULL,
        [LastCheckedAtUtc] datetime2 NULL,
        [LastSuccessAtUtc] datetime2 NULL,
        [LastFailureAtUtc] datetime2 NULL,
        [LastFailureMessage] nvarchar(2000) NULL,
        [ConsecutiveFailureCount] int NOT NULL,
        [TotalJobsImported] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobSources] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobSubscriberDocuments] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [DocumentType] int NOT NULL,
        [ObjectKey] nvarchar(500) NOT NULL,
        [FileUrl] nvarchar(1000) NULL,
        [FileName] nvarchar(300) NOT NULL,
        [ContentType] nvarchar(150) NOT NULL,
        [SizeBytes] bigint NOT NULL,
        [IsCurrent] bit NOT NULL,
        [UploadedAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobSubscriberDocuments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JobSubscriberDocuments_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobSubscriberProfiles] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [PreferredFirstName] nvarchar(100) NULL,
        [ContactPhone] nvarchar(30) NULL,
        [ContactPhoneNormalized] nvarchar(30) NULL,
        [CurrentCity] nvarchar(100) NULL,
        [CurrentProvince] nvarchar(100) NULL,
        [CurrentCountry] nvarchar(100) NULL,
        [LinkedInProfileUrl] nvarchar(500) NULL,
        [WebsiteUrl] nvarchar(500) NULL,
        [SalaryExpectations] nvarchar(200) NULL,
        [HighestQualification] nvarchar(200) NULL,
        [YearsOfExperience] int NULL,
        [PreferredCategoriesJson] nvarchar(2000) NULL,
        [PreferredLocationsJson] nvarchar(2000) NULL,
        [CvObjectKey] nvarchar(500) NULL,
        [CvFileUrl] nvarchar(1000) NULL,
        [CvFileName] nvarchar(300) NULL,
        [CvContentType] nvarchar(150) NULL,
        [CvSizeBytes] bigint NULL,
        [CvUploadedAtUtc] datetime2 NULL,
        [CoverLetterObjectKey] nvarchar(500) NULL,
        [CoverLetterFileUrl] nvarchar(1000) NULL,
        [CoverLetterFileName] nvarchar(300) NULL,
        [CoverLetterContentType] nvarchar(150) NULL,
        [CoverLetterSizeBytes] bigint NULL,
        [CoverLetterUploadedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobSubscriberProfiles] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JobSubscriberProfiles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobImportRuns] (
        [Id] uniqueidentifier NOT NULL,
        [SourceId] uniqueidentifier NULL,
        [SourceName] nvarchar(150) NOT NULL,
        [Status] int NOT NULL,
        [Trigger] int NOT NULL,
        [TriggeredByUserId] uniqueidentifier NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [CompletedAtUtc] datetime2 NULL,
        [DurationMs] int NOT NULL,
        [JobsFound] int NOT NULL,
        [JobsCreated] int NOT NULL,
        [JobsUpdated] int NOT NULL,
        [JobsSkipped] int NOT NULL,
        [JobsExpired] int NOT NULL,
        [IsSuccess] bit NOT NULL,
        [FailureMessage] nvarchar(2000) NULL,
        [Notes] nvarchar(4000) NULL,
        [HttpStatusCode] int NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobImportRuns] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JobImportRuns_JobSources_SourceId] FOREIGN KEY ([SourceId]) REFERENCES [JobSources] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE TABLE [JobOpportunities] (
        [Id] uniqueidentifier NOT NULL,
        [Title] nvarchar(300) NOT NULL,
        [Slug] nvarchar(320) NOT NULL,
        [CompanyName] nvarchar(200) NULL,
        [Location] nvarchar(300) NULL,
        [Country] nvarchar(100) NULL,
        [Province] nvarchar(100) NULL,
        [City] nvarchar(100) NULL,
        [Category] nvarchar(100) NULL,
        [WorkplaceType] int NOT NULL,
        [EmploymentType] nvarchar(100) NULL,
        [SalaryText] nvarchar(300) NULL,
        [Summary] nvarchar(1000) NULL,
        [DescriptionHtml] nvarchar(max) NULL,
        [DescriptionText] nvarchar(max) NULL,
        [RequirementsText] nvarchar(max) NULL,
        [ApplicationInstructions] nvarchar(2000) NULL,
        [ApplyUrl] nvarchar(1000) NULL,
        [ApplyEmail] nvarchar(256) NULL,
        [SourceUrl] nvarchar(1000) NULL,
        [SourceName] nvarchar(150) NOT NULL,
        [SourceId] uniqueidentifier NULL,
        [ExternalId] nvarchar(300) NULL,
        [Fingerprint] nvarchar(64) NOT NULL,
        [PostedDateUtc] datetime2 NULL,
        [ClosingDateUtc] datetime2 NULL,
        [ImportedAtUtc] datetime2 NOT NULL,
        [LastSeenAtUtc] datetime2 NOT NULL,
        [Status] int NOT NULL,
        [IsFeatured] bit NOT NULL,
        [IsManuallyEdited] bit NOT NULL,
        [LogoUrl] nvarchar(1000) NULL,
        [TagsJson] nvarchar(2000) NULL,
        [RawContentSnapshot] nvarchar(max) NULL,
        [ViewCount] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_JobOpportunities] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JobOpportunities_JobSources_SourceId] FOREIGN KEY ([SourceId]) REFERENCES [JobSources] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobAlertDeliveryLogs_CreatedAtUtc] ON [JobAlertDeliveryLogs] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobAlertDeliveryLogs_Status] ON [JobAlertDeliveryLogs] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobAlertDeliveryLogs_UserId_WindowStartUtc] ON [JobAlertDeliveryLogs] ([UserId], [WindowStartUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobAlertPreferences_IsSubscribed_Frequency] ON [JobAlertPreferences] ([IsSubscribed], [Frequency]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_JobAlertPreferences_UnsubscribeToken] ON [JobAlertPreferences] ([UnsubscribeToken]) WHERE [UnsubscribeToken] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE UNIQUE INDEX [IX_JobAlertPreferences_UserId] ON [JobAlertPreferences] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobImportRuns_SourceId] ON [JobImportRuns] ([SourceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobImportRuns_StartedAtUtc] ON [JobImportRuns] ([StartedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobImportRuns_Status] ON [JobImportRuns] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_Category] ON [JobOpportunities] ([Category]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_City] ON [JobOpportunities] ([City]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_ClosingDateUtc] ON [JobOpportunities] ([ClosingDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_CreatedAtUtc] ON [JobOpportunities] ([CreatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE UNIQUE INDEX [IX_JobOpportunities_Fingerprint] ON [JobOpportunities] ([Fingerprint]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_IsFeatured] ON [JobOpportunities] ([IsFeatured]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_PostedDateUtc] ON [JobOpportunities] ([PostedDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_Province] ON [JobOpportunities] ([Province]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE UNIQUE INDEX [IX_JobOpportunities_Slug] ON [JobOpportunities] ([Slug]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_SourceId] ON [JobOpportunities] ([SourceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_Status] ON [JobOpportunities] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_Status_ClosingDateUtc_PostedDateUtc] ON [JobOpportunities] ([Status], [ClosingDateUtc], [PostedDateUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobOpportunities_WorkplaceType] ON [JobOpportunities] ([WorkplaceType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobSources_IsActive] ON [JobSources] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobSources_LastCheckedAtUtc] ON [JobSources] ([LastCheckedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobSources_SourceType] ON [JobSources] ([SourceType]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE UNIQUE INDEX [IX_JobSources_SourceUrl] ON [JobSources] ([SourceUrl]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobSubscriberDocuments_UploadedAtUtc] ON [JobSubscriberDocuments] ([UploadedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobSubscriberDocuments_UserId_DocumentType_IsCurrent] ON [JobSubscriberDocuments] ([UserId], [DocumentType], [IsCurrent]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE INDEX [IX_JobSubscriberProfiles_CompletedAtUtc] ON [JobSubscriberProfiles] ([CompletedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    CREATE UNIQUE INDEX [IX_JobSubscriberProfiles_UserId] ON [JobSubscriberProfiles] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260802063907_AddJobOpportunitiesModule'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260802063907_AddJobOpportunitiesModule', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803094512_AddJobSourceMaxPagesPerRun'
)
BEGIN
    ALTER TABLE [JobSources] ADD [MaxPagesPerRun] int NOT NULL DEFAULT 1;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803094512_AddJobSourceMaxPagesPerRun'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260803094512_AddJobSourceMaxPagesPerRun', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks'
)
BEGIN
    ALTER TABLE [JobModuleSettings] ADD [AppleAppStoreUrl] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks'
)
BEGIN
    ALTER TABLE [JobModuleSettings] ADD [GooglePlayUrl] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks'
)
BEGIN
    ALTER TABLE [JobModuleSettings] ADD [HuaweiAppGalleryUrl] nvarchar(500) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks'
)
BEGIN
    ALTER TABLE [JobModuleSettings] ADD [JobDetailsMobileAppOnly] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks', N'8.0.11');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260806131928_SetIosAppStoreUrl'
)
BEGIN
    EXEC(N'UPDATE [MobileAppVersionRules] SET [StoreUrl] = N''https://apps.apple.com/app/smartfuture-app/id6794007617''
    WHERE [Id] = ''a3333333-3333-3333-3333-333333333333'';
    SELECT @@ROWCOUNT');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260806131928_SetIosAppStoreUrl'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260806131928_SetIosAppStoreUrl', N'8.0.11');
END;
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

