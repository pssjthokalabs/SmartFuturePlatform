/* =====================================================================
   LIVE — Jobs enrolment diagnostics + JobSubscriber role seed

   Sections 0-4 are READ ONLY. Section 5 is the only write, and it is
   commented out.

   BACKGROUND

   Role rows are NOT created by any migration. They come from
   DbInitializer.SeedRolesAsync, which runs only via
   ApplyDatabaseMigrationsAsync and only after its
   `if (!enabled) return;` guard on Database:ApplyMigrationsOnStartup.
   Live has that flag off, and Live's Jobs schema was applied from the
   idempotent SQL script — so the tables arrived and the role row did not.

   UserManager.AddToRoleAsync THROWS when the role row is missing, which
   surfaced as "An unexpected error occurred while enabling job seeker
   access".

   NOTE: the API fix in this same change makes the application create a
   missing SYSTEM role automatically on first enrolment. Once that build
   is deployed this script is a verification tool, not a required step.
   ===================================================================== */

/* --- 0. Confirm you are on LIVE, not UAT -------------------------- */
SELECT DB_NAME() AS CurrentDatabase, @@SERVERNAME AS ServerName;


/* --- 1. Jobs migrations ------------------------------------------- */
SELECT m.MigrationId,
       CASE WHEN h.MigrationId IS NULL THEN 'MISSING' ELSE 'applied' END AS Status
FROM (VALUES
        ('20260802063907_AddJobOpportunitiesModule'),
        ('20260803094512_AddJobSourceMaxPagesPerRun'),
        ('20260803164015_AddJobDetailsMobileAppOnlyAndStoreLinks')
     ) AS m(MigrationId)
LEFT JOIN dbo.__EFMigrationsHistory h ON h.MigrationId = m.MigrationId;


/* --- 2. Jobs tables ----------------------------------------------- */
SELECT 'JobSubscriberProfiles'  AS TableName, OBJECT_ID('dbo.JobSubscriberProfiles')  AS ObjectId
UNION ALL SELECT 'JobSubscriberDocuments', OBJECT_ID('dbo.JobSubscriberDocuments')
UNION ALL SELECT 'JobAlertPreferences',    OBJECT_ID('dbo.JobAlertPreferences')
UNION ALL SELECT 'JobAlertDeliveryLogs',   OBJECT_ID('dbo.JobAlertDeliveryLogs')
UNION ALL SELECT 'JobModuleSettings',      OBJECT_ID('dbo.JobModuleSettings')
UNION ALL SELECT 'JobSources',             OBJECT_ID('dbo.JobSources')
UNION ALL SELECT 'JobOpportunities',       OBJECT_ID('dbo.JobOpportunities');
/* NULL = missing. */


/* --- 3. THE PRIME SUSPECT: system roles --------------------------- */
/* SystemRoles.All = SuperAdmin, Admin, Agent, Technician, Support,
                     Customer, JobSubscriber                          */
SELECT r.Name AS ExpectedRole,
       CASE WHEN a.Id IS NULL THEN '*** MISSING ***' ELSE 'present' END AS Status,
       a.Id, a.NormalizedName
FROM (VALUES
        ('SuperAdmin'), ('Admin'), ('Agent'), ('Technician'),
        ('Support'), ('Customer'), ('JobSubscriber')
     ) AS r(Name)
LEFT JOIN dbo.AspNetRoles a ON a.NormalizedName = UPPER(r.Name)
ORDER BY r.Name;

/* If JobSubscriber shows MISSING, that is the Live website failure and
   the mobile 403, both explained by one absent row. */


/* --- 4. Who currently holds JobSubscriber ------------------------- */
SELECT COUNT(*) AS JobSubscriberUserCount
FROM dbo.AspNetUserRoles ur
JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
WHERE r.NormalizedName = 'JOBSUBSCRIBER';

SELECT COUNT(*) AS JobSubscriberProfileCount FROM dbo.JobSubscriberProfiles;

/* Users holding BOTH Customer and JobSubscriber — the same-account rule.
   This must never be achieved by creating a second user row. */
SELECT u.Id AS UserId, u.Email,
       MAX(CASE WHEN r.NormalizedName = 'CUSTOMER'      THEN 1 ELSE 0 END) AS IsCustomer,
       MAX(CASE WHEN r.NormalizedName = 'JOBSUBSCRIBER' THEN 1 ELSE 0 END) AS IsJobSubscriber
FROM dbo.AspNetUsers u
JOIN dbo.AspNetUserRoles ur ON ur.UserId = u.Id
JOIN dbo.AspNetRoles r      ON r.Id = ur.RoleId
GROUP BY u.Id, u.Email
HAVING MAX(CASE WHEN r.NormalizedName = 'JOBSUBSCRIBER' THEN 1 ELSE 0 END) = 1;


/* =====================================================================
   5. OPTIONAL WRITE — create any missing system role.

   COMMENTED OUT. Take a database backup first.

   IDEMPOTENT: inserts only what is absent, so it is safe to re-run.
   It matches exactly what DbInitializer.SeedRolesAsync would have
   written — name, upper-case NormalizedName, a fresh GUID, and a
   ConcurrencyStamp. It grants the role to NOBODY; users only get it by
   enrolling through the app.
   ===================================================================== */

-- INSERT INTO dbo.AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp)
-- SELECT NEWID(), r.Name, UPPER(r.Name), CONVERT(nvarchar(36), NEWID())
-- FROM (VALUES
--         ('SuperAdmin'), ('Admin'), ('Agent'), ('Technician'),
--         ('Support'), ('Customer'), ('JobSubscriber')
--      ) AS r(Name)
-- WHERE NOT EXISTS (
--     SELECT 1 FROM dbo.AspNetRoles a WHERE a.NormalizedName = UPPER(r.Name)
-- );
--
-- -- Verify
-- SELECT Id, Name, NormalizedName FROM dbo.AspNetRoles ORDER BY Name;
