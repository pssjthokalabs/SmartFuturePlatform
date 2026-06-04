/* =====================================================================
   SmartFuture — verify real vs test counts (SQL Server / T-SQL).
   Read-only. Run on UAT/LIVE to confirm what the portal SHOULD show
   after the test-account stats-exclusion sweep.

   Test account = AspNetUsers.IsTestAccount = 1
   (auto-set for customer{1000-1999}@gmail.com).

   FK paths used:
     CustomerProfiles.UserId            → AspNetUsers.Id
     Orders.UserId                      → AspNetUsers.Id
     Invoices.OrderId                   → Orders.Id (→ Orders.UserId)
     Installations.OrderId              → Orders.Id
     NetworkAccounts.OrderId            → Orders.Id
     Payments.InvoiceId                 → Invoices.Id (→ Order → User)
   ===================================================================== */

SET NOCOUNT ON;

/* ---- Users / customers --------------------------------------------- */
SELECT 'Users (all)'              AS Metric, COUNT(*) AS Cnt FROM [AspNetUsers]
UNION ALL SELECT 'Users (test)',        COUNT(*) FROM [AspNetUsers] WHERE [IsTestAccount] = 1
UNION ALL SELECT 'Users (real)',        COUNT(*) FROM [AspNetUsers] WHERE [IsTestAccount] = 0
UNION ALL SELECT 'CustomerProfiles (all)',  COUNT(*) FROM [CustomerProfiles]
UNION ALL SELECT 'CustomerProfiles (test)', COUNT(*)
    FROM [CustomerProfiles] cp JOIN [AspNetUsers] u ON u.Id = cp.UserId WHERE u.IsTestAccount = 1
UNION ALL SELECT 'CustomerProfiles (REAL → dashboard Total Customers)', COUNT(*)
    FROM [CustomerProfiles] cp JOIN [AspNetUsers] u ON u.Id = cp.UserId WHERE u.IsTestAccount = 0;

/* ---- Orders / installations / services ----------------------------- */
SELECT 'Orders (all)'   AS Metric, COUNT(*) AS Cnt FROM [Orders]
UNION ALL SELECT 'Orders (test)', COUNT(*)
    FROM [Orders] o JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 1
UNION ALL SELECT 'Orders (real)', COUNT(*)
    FROM [Orders] o JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 0
UNION ALL SELECT 'Installations (test)', COUNT(*)
    FROM [Installations] i JOIN [Orders] o ON o.Id = i.OrderId
                            JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 1
UNION ALL SELECT 'NetworkAccounts/services (test)', COUNT(*)
    FROM [NetworkAccounts] n JOIN [Orders] o ON o.Id = n.OrderId
                             JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 1;

/* ---- Invoices / payments / revenue --------------------------------- */
SELECT 'Invoices (test)' AS Metric, COUNT(*) AS Cnt, CAST(ISNULL(SUM(i.BalanceDue),0) AS DECIMAL(18,2)) AS TestOutstanding
    FROM [Invoices] i JOIN [Orders] o ON o.Id = i.OrderId
                      JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 1
UNION ALL SELECT 'Invoices (real)', COUNT(*), CAST(ISNULL(SUM(i.BalanceDue),0) AS DECIMAL(18,2))
    FROM [Invoices] i JOIN [Orders] o ON o.Id = i.OrderId
                      JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 0;

SELECT 'Payments (test)' AS Metric, COUNT(*) AS Cnt
    FROM [Payments] p JOIN [Invoices] i ON i.Id = p.InvoiceId
                      JOIN [Orders] o ON o.Id = i.OrderId
                      JOIN [AspNetUsers] u ON u.Id = o.UserId WHERE u.IsTestAccount = 1;

/* Expectation when LIVE has ONLY test customers: every "(real)" row = 0,
   so dashboard/reports Total Customers, revenue, outstanding, active
   services, pending installations all read 0. */
