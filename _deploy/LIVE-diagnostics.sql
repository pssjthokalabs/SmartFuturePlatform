/* =====================================================================
   LIVE Jobs 500 — diagnostic queries (READ ONLY)

   Run these against the LIVE database ONLY, from the SmarterASP.NET
   control panel or SSMS. Every statement here is a SELECT: nothing is
   created, altered or deleted.

   CONFIRM FIRST that you are on Live and not UAT:
       SELECT DB_NAME() AS CurrentDatabase, @@SERVERNAME AS ServerName;

   Run section 0 and paste the results back before applying anything.
   ===================================================================== */

/* --- 0. Which database am I connected to? ------------------------- */
SELECT DB_NAME() AS CurrentDatabase, @@SERVERNAME AS ServerName, SUSER_SNAME() AS LoginName;


/* --- 1. EF migration history ------------------------------------- */
SELECT MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId;

/* Expected TAIL of that list if Live is fully up to date:
       20260701192951_AddCoverageMapRules
       20260802063907_AddJobOpportunitiesModule
       20260803094512_AddJobSourceMaxPagesPerRun
       20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks
   If the list stops at AddCoverageMapRules, all three Jobs migrations
   are pending and that is the whole story. */


/* --- 2. Are the three Jobs migrations recorded? ------------------- */
SELECT m.MigrationId,
       CASE WHEN h.MigrationId IS NULL THEN 'MISSING' ELSE 'applied' END AS Status
FROM (VALUES
        ('20260802063907_AddJobOpportunitiesModule'),
        ('20260803094512_AddJobSourceMaxPagesPerRun'),
        ('20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks')
     ) AS m(MigrationId)
LEFT JOIN dbo.__EFMigrationsHistory h ON h.MigrationId = m.MigrationId;


/* --- 3. Do the Jobs tables exist? -------------------------------- */
SELECT 'JobSources'              AS TableName, OBJECT_ID('dbo.JobSources')              AS ObjectId
UNION ALL SELECT 'JobOpportunities',       OBJECT_ID('dbo.JobOpportunities')
UNION ALL SELECT 'JobSubscriberProfiles',  OBJECT_ID('dbo.JobSubscriberProfiles')
UNION ALL SELECT 'JobSubscriberDocuments', OBJECT_ID('dbo.JobSubscriberDocuments')
UNION ALL SELECT 'JobModuleSettings',      OBJECT_ID('dbo.JobModuleSettings')
UNION ALL SELECT 'JobImportRuns',          OBJECT_ID('dbo.JobImportRuns')
UNION ALL SELECT 'JobAlertPreferences',    OBJECT_ID('dbo.JobAlertPreferences')
UNION ALL SELECT 'JobAlertDeliveryLogs',   OBJECT_ID('dbo.JobAlertDeliveryLogs');
/* NULL ObjectId = table does not exist. */


/* --- 4. JobSources columns (only if the table exists) ------------- */
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'JobSources'
ORDER BY ORDINAL_POSITION;
/* MaxPagesPerRun (int, NOT NULL, default 1) must be present —
   it is added by 20260803094512. */


/* --- 5. JobModuleSettings columns (only if the table exists) ------ */
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'JobModuleSettings'
ORDER BY ORDINAL_POSITION;
/* JobDetailsMobileAppOnly (bit), GooglePlayUrl, HuaweiAppGalleryUrl,
   AppleAppStoreUrl (all nvarchar(500) NULL) must be present —
   added by 20260803164015. */


/* --- 6. Live Jobs data volumes (safe even if empty) --------------- */
/* Run ONLY the lines whose tables exist per section 3. */
SELECT COUNT(*) AS JobSourcesCount        FROM dbo.JobSources;
SELECT COUNT(*) AS JobOpportunitiesCount  FROM dbo.JobOpportunities;

/* Status breakdown. Enum: 0=Draft 1=Active 2=Hidden 3=Expired 4=Deleted */
SELECT Status, COUNT(*) AS Rows
FROM dbo.JobOpportunities
GROUP BY Status
ORDER BY Status;

/* Crawler safety settings on any existing sources. */
SELECT Id, Name, IsActive, AutoPublish, MaxPagesPerRun, MaxJobsPerRun
FROM dbo.JobSources
ORDER BY Name;
