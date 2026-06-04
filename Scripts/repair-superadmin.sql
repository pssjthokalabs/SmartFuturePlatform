/* =====================================================================
   SmartFuture — SuperAdmin permission repair (SQL Server / T-SQL)
   ---------------------------------------------------------------------
   Symptom this fixes:
     A user shows as "SuperAdmin" in the portal but gets "no permission"
     / 403 on admin pages and actions.

   Root cause:
     Authorization is ROLE-based (ASP.NET Identity roles + policies).
     There is NO separate Permissions / RolePermissions / UserPermissions
     table — "permissions" == role membership. The API's RequireAdmin
     policy historically accepted ONLY the "Admin" role, so a user linked
     to ONLY "SuperAdmin" failed every admin endpoint. The backend fix
     makes RequireAdmin accept SuperAdmin too; this script ALSO grants the
     "Admin" role so the account works even on an API build that predates
     the fix, and normalises the account to Active.

   What it does (idempotent — safe to run repeatedly, on UAT then LIVE):
     1. Ensures the SuperAdmin and Admin role rows exist.
     2. Finds the target user by email.
     3. Links the user to BOTH SuperAdmin and Admin (only if missing).
     4. Sets the user Active / IsActive / EmailConfirmed.
   It does NOT delete users, roles, or existing links, and does NOT touch
   passwords. Admins do not need a CustomerProfile row, so none is created
   (a CustomerProfile would mis-classify the account as a customer).

   USAGE:
     - Set @Email below.
     - Run against the correct database (UAT first, then LIVE).
     - Have the user LOG OUT and LOG IN again afterwards (a fresh JWT must
       be issued so the new role claims are present in the token).
   ===================================================================== */

SET NOCOUNT ON;

DECLARE @Email           nvarchar(256) = N'developers@smartfuture.co.za';
DECLARE @NormalizedEmail nvarchar(256) = UPPER(@Email);

DECLARE @UserId        uniqueidentifier;
DECLARE @SuperAdminId  uniqueidentifier;
DECLARE @AdminId       uniqueidentifier;

/* 1. Ensure the roles exist (NormalizedName is the lookup key). ---------- */
IF NOT EXISTS (SELECT 1 FROM [AspNetRoles] WHERE [NormalizedName] = N'SUPERADMIN')
    INSERT INTO [AspNetRoles] ([Id], [Name], [NormalizedName], [ConcurrencyStamp])
    VALUES (NEWID(), N'SuperAdmin', N'SUPERADMIN', CONVERT(nvarchar(36), NEWID()));

IF NOT EXISTS (SELECT 1 FROM [AspNetRoles] WHERE [NormalizedName] = N'ADMIN')
    INSERT INTO [AspNetRoles] ([Id], [Name], [NormalizedName], [ConcurrencyStamp])
    VALUES (NEWID(), N'Admin', N'ADMIN', CONVERT(nvarchar(36), NEWID()));

SELECT @SuperAdminId = [Id] FROM [AspNetRoles] WHERE [NormalizedName] = N'SUPERADMIN';
SELECT @AdminId      = [Id] FROM [AspNetRoles] WHERE [NormalizedName] = N'ADMIN';

/* 2. Find the user. ----------------------------------------------------- */
SELECT @UserId = [Id] FROM [AspNetUsers] WHERE [NormalizedEmail] = @NormalizedEmail;

IF @UserId IS NULL
BEGIN
    PRINT 'ABORT: no AspNetUsers row with NormalizedEmail = ' + @NormalizedEmail
        + '. Create the account first, then re-run.';
    RETURN;
END

/* 3. Link the user to SuperAdmin + Admin (only if missing). ------------- */
IF NOT EXISTS (SELECT 1 FROM [AspNetUserRoles] WHERE [UserId] = @UserId AND [RoleId] = @SuperAdminId)
    INSERT INTO [AspNetUserRoles] ([UserId], [RoleId]) VALUES (@UserId, @SuperAdminId);

IF NOT EXISTS (SELECT 1 FROM [AspNetUserRoles] WHERE [UserId] = @UserId AND [RoleId] = @AdminId)
    INSERT INTO [AspNetUserRoles] ([UserId], [RoleId]) VALUES (@UserId, @AdminId);

/* 4. Normalise the account so RequireActiveUser / the verification gate
      never block it. AccountStatus 0 = Active (UserAccountStatus enum). -- */
UPDATE [AspNetUsers]
SET [AccountStatus] = 0,
    [IsActive]      = 1,
    [EmailConfirmed] = 1
WHERE [Id] = @UserId
  AND ([AccountStatus] <> 0 OR [IsActive] = 0 OR [EmailConfirmed] = 0);

/* 5. Report final state. ----------------------------------------------- */
PRINT 'Repaired user: ' + @Email;
SELECT
    u.[Id]            AS UserId,
    u.[Email],
    u.[AccountStatus] AS AccountStatus_0IsActive,
    u.[IsActive],
    u.[EmailConfirmed],
    STUFF((
        SELECT ', ' + r.[Name]
        FROM [AspNetUserRoles] ur
        JOIN [AspNetRoles] r ON r.[Id] = ur.[RoleId]
        WHERE ur.[UserId] = u.[Id]
        FOR XML PATH('')), 1, 2, '') AS Roles
FROM [AspNetUsers] u
WHERE u.[Id] = @UserId;
