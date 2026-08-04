/* =====================================================================
   LIVE "Import cancelled" — diagnostics (READ ONLY except section 4)

   Sections 0-3 are SELECTs only. Section 4 is the ONE optional write:
   it lowers a single source's crawl limits for a safe test refresh.
   It is commented out. Nothing runs until you uncomment it.

   Column names verified against the EF model
   (SmartFuture.Domain/Jobs/JobSource.cs and JobImportRun.cs).
   ===================================================================== */

/* --- 0. Confirm you are on LIVE, not UAT -------------------------- */
SELECT DB_NAME() AS CurrentDatabase, @@SERVERNAME AS ServerName;


/* --- 1. Live source settings (the prime suspect) ------------------ */
/* Compare these against UAT. The refresh does ONE sequential HTTP
   fetch per listing page plus ONE per job link, each with a 20s
   timeout, inside a 60s request budget. So:

       worst-case fetches  =  MaxPagesPerRun + MaxJobsPerRun

   1 + 20  =   21 fetches  -> finishes inside 60s
   38 + 500 = 538 fetches  -> cannot possibly finish; cancels every time  */
SELECT
    Id,
    SourceName,
    SourceUrl,
    SourceType,                  -- 0=Manual is never crawled
    IsActive,
    AutoPublish,
    MaxPagesPerRun,
    MaxJobsPerRun,
    CrawlFrequencyMinutes,       -- NULL = manual-refresh only, never scheduled
    MaxPagesPerRun + MaxJobsPerRun AS WorstCaseSequentialFetches,
    LastCheckedAtUtc,
    LastSuccessAtUtc,
    LastFailureAtUtc,
    ConsecutiveFailureCount,
    LastFailureMessage
FROM dbo.JobSources
ORDER BY SourceName;


/* --- 2. The failed run(s) ----------------------------------------- */
SELECT TOP 20
    Id,
    SourceId,
    SourceName,
    Trigger,                     -- 0=Manual 1=Scheduled
    Status,                      -- 0=Running 1=Succeeded 2=PartiallySucceeded 3=Failed
    IsSuccess,
    StartedAtUtc,
    CompletedAtUtc,
    DurationMs,                  -- ~60000 => the 60s refresh deadline fired
    HttpStatusCode,
    JobsFound,
    JobsCreated,
    JobsUpdated,
    JobsSkipped,
    JobsExpired,
    FailureMessage,
    Notes
FROM dbo.JobImportRuns
ORDER BY StartedAtUtc DESC;

/* HOW TO READ DurationMs — this is the discriminator you asked for:

   ~60 000 ms  -> B. the 60s refresh deadline. Crawl budget too large.
   ~20 000 ms  -> C. a single HttpClient fetch timed out (20s). One
                     unreachable/slow URL. Check Notes for which.
   << 60 000   -> A. client disconnect, OR the run genuinely failed
                     early — read FailureMessage.
   Status=0 (Running) with CompletedAtUtc NULL
               -> the process died mid-run (app-pool recycle), not a
                  cancellation at all.                                  */


/* --- 3. Anything still stuck Running ------------------------------ */
SELECT Id, SourceName, StartedAtUtc, DurationMs, Status
FROM dbo.JobImportRuns
WHERE Status = 0 AND CompletedAtUtc IS NULL
ORDER BY StartedAtUtc DESC;


/* =====================================================================
   4. OPTIONAL — safe test settings for ONE source.

   COMMENTED OUT. Uncomment, set @SourceId from section 1, then run.
   Sets AutoPublish OFF so nothing can go public, and shrinks the crawl
   to 1 page + 3 jobs = 4 fetches, which cannot reach the 60s deadline.

   This does NOT start an import. Click Refresh in the admin portal
   afterwards.
   ===================================================================== */

-- DECLARE @SourceId uniqueidentifier = '00000000-0000-0000-0000-000000000000';
--
-- SELECT SourceName, IsActive, AutoPublish, MaxPagesPerRun, MaxJobsPerRun
-- FROM dbo.JobSources WHERE Id = @SourceId;          -- before
--
-- UPDATE dbo.JobSources
--    SET AutoPublish    = 0,   -- nothing auto-publishes
--        MaxPagesPerRun = 1,
--        MaxJobsPerRun  = 3
--  WHERE Id = @SourceId;
--
-- SELECT SourceName, IsActive, AutoPublish, MaxPagesPerRun, MaxJobsPerRun
-- FROM dbo.JobSources WHERE Id = @SourceId;          -- after
