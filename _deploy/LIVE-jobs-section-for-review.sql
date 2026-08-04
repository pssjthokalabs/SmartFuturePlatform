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

