/* =====================================================================
   SmartFuture — backfill IsTestAccount for controlled QA test accounts
   (SQL Server / T-SQL). Idempotent — safe to run repeatedly on UAT then
   LIVE.

   The EF migration 20260604082645_AddIsTestAccount already runs this same
   UPDATE on deploy. This standalone script exists for two cases:
     1. Re-flagging after rows were created out-of-band, or
     2. Repairing without a redeploy.

   Rule: a test account is EXACTLY customer{1000-1999}@gmail.com
   (case-insensitive). Matched on the uppercase NormalizedEmail so the
   result is collation-independent. "[0-9]" matches one digit each, so
   "CUSTOMER1[0-9][0-9][0-9]" is precisely customer1000..customer1999.
   The dot is a literal in LIKE. Operator accounts (developers@,
   vuyani@smartfuture.co.za) never match and are never flagged.
   ===================================================================== */

SET NOCOUNT ON;

-- Flag matching accounts.
UPDATE [AspNetUsers]
SET [IsTestAccount] = 1
WHERE [NormalizedEmail] LIKE 'CUSTOMER1[0-9][0-9][0-9]@GMAIL.COM'
  AND [IsTestAccount] = 0;

-- Safety: ensure nothing OUTSIDE the pattern is flagged (e.g. a row
-- wrongly set by hand). Comment this out if you intentionally flag
-- extra accounts another way.
UPDATE [AspNetUsers]
SET [IsTestAccount] = 0
WHERE [IsTestAccount] = 1
  AND [NormalizedEmail] NOT LIKE 'CUSTOMER1[0-9][0-9][0-9]@GMAIL.COM';

-- Report.
SELECT
    COUNT(*)                                              AS TotalUsers,
    SUM(CASE WHEN [IsTestAccount] = 1 THEN 1 ELSE 0 END)  AS TestAccounts
FROM [AspNetUsers];

SELECT [Email], [IsTestAccount]
FROM [AspNetUsers]
WHERE [IsTestAccount] = 1
ORDER BY [Email];
