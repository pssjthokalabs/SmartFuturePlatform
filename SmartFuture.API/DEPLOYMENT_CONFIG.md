# SmartFuture.API — Deployment Configuration

This document lists the environment variables the SmartFuture .NET API
expects in each deployment environment. **No real secret values appear in
this file** — every secret is shown as a `<placeholder>` and must be
supplied via the hosting environment (e.g. IIS application-pool
environment variables, user-secrets, Azure Key Vault, etc.).

The API reads configuration in this order (later wins):

1. `appsettings.json` (committed defaults / placeholders)
2. `appsettings.{Environment}.json` (committed environment overrides)
3. Environment variables (e.g. IIS app-pool settings) — **secrets go here**

Environment variable names use the standard ASP.NET `__` (double
underscore) section separator. Example: `JwtSettings:Key` in JSON ↔
`JwtSettings__Key` as an environment variable.

---

## UAT API — `https://uatapi.smartfuture.co.za`

```
ASPNETCORE_ENVIRONMENT=UAT

ConnectionStrings__UatConnection=<uat-db-connection-string>

JwtSettings__Issuer=https://uatapi.smartfuture.co.za
JwtSettings__Audience=https://uat.portal.smartfuture.co.za
JwtSettings__Key=<strong-secret-key-at-least-32-chars>
JwtSettings__AccessTokenMinutes=60
JwtSettings__RefreshTokenDays=14

Cors__AllowedOrigins__0=https://uat.portal.smartfuture.co.za
Cors__AllowedOrigins__1=http://localhost:5173

Database__ApplyMigrationsOnStartup=true
Swagger__Enabled=true
Diagnostics__ExposeExceptionDetails=true

FrontendSettings__AdminResetPasswordUrl=https://uat.portal.smartfuture.co.za/admin/reset-password
FrontendSettings__ClientResetPasswordUrl=https://uat.portal.smartfuture.co.za/client/reset-password

EmailSettings__Provider=Logging

SeedSuperAdmin__Enabled=true
SeedSuperAdmin__Email=developers@smartfuture.co.za
SeedSuperAdmin__PhoneNumber=0737942244
SeedSuperAdmin__FirstName=Developers
SeedSuperAdmin__LastName=Smart Future
SeedSuperAdmin__Password=<set-strong-password-here>

PackageSeed__Enabled=true
PackageSeed__UpdateExisting=false
```

UAT runs with `EmailSettings__Provider=Logging` until real SMTP is
provisioned — password-reset emails are recorded in the
`OutboundNotifications` table and visible in the app logs but are not
actually delivered. Switch to the `Smtp` block below once SMTP is ready.

---

## Production API — `https://api.smartfuture.co.za`

```
ASPNETCORE_ENVIRONMENT=Production

ConnectionStrings__UatConnection=<live-db-connection-string>

JwtSettings__Issuer=https://api.smartfuture.co.za
JwtSettings__Audience=https://portal.smartfuture.co.za
JwtSettings__Key=<strong-secret-key-at-least-32-chars>
JwtSettings__AccessTokenMinutes=60
JwtSettings__RefreshTokenDays=14

Cors__AllowedOrigins__0=https://portal.smartfuture.co.za

Database__ApplyMigrationsOnStartup=false
Swagger__Enabled=false
Diagnostics__ExposeExceptionDetails=false

FrontendSettings__AdminResetPasswordUrl=https://portal.smartfuture.co.za/admin/reset-password
FrontendSettings__ClientResetPasswordUrl=https://portal.smartfuture.co.za/client/reset-password

EmailSettings__Provider=Smtp
EmailSettings__FromEmail=no-reply@smartfuture.co.za
EmailSettings__FromName=Smart Future
EmailSettings__Smtp__Host=<smtp-host>
EmailSettings__Smtp__Port=587
EmailSettings__Smtp__EnableSsl=true
EmailSettings__Smtp__Username=<smtp-user>
EmailSettings__Smtp__Password=<smtp-password>

# Disable the bootstrap seeder in Production once the Super Admin has been
# created. Leaving Enabled=true is safe (it's idempotent and won't reset the
# password on subsequent boots) but disabling it removes the password env var
# requirement entirely.
SeedSuperAdmin__Enabled=false

# Production package seeding is opt-in. Run once on a clean DB then disable
# so admin pricing changes through the portal can't be clobbered by a deploy.
PackageSeed__Enabled=false
PackageSeed__UpdateExisting=false
```

The `Database__ApplyMigrationsOnStartup=false` setting in production
exists so a bad deploy can't run migrations against the live database
automatically. Run migrations manually as a deliberate step.

---

## Key reference

### `JwtSettings`
| Key | Purpose |
| --- | --- |
| `Issuer` | JWT `iss` claim — the API base URL. |
| `Audience` | JWT `aud` claim — the portal base URL. |
| `Key` | HMAC signing key. **Must be ≥ 32 chars** and **must not** be the placeholder from `appsettings.json`; startup validation will reject it. |
| `AccessTokenMinutes` | Access-token lifetime. |
| `RefreshTokenDays` | Refresh-token lifetime. |

### `Cors:AllowedOrigins`
Array of exact origins permitted by the CORS policy. Use the indexed
syntax (`Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, …) to add
multiple. **Do not use wildcards** — credentials are sent and a wildcard
origin combined with `AllowCredentials` is rejected by browsers anyway.

### `FrontendSettings`
| Key | Purpose |
| --- | --- |
| `AdminResetPasswordUrl` | Base URL the API embeds in admin password-reset emails. The API appends `?email=…&token=…`. |
| `ClientResetPasswordUrl` | Base URL the API embeds in client password-reset emails. |

### `EmailSettings`
| Key | Purpose |
| --- | --- |
| `Provider` | `"Logging"` (default, no real delivery) or `"Smtp"`. Picked up at startup by `AddEmailServices`. |
| `FromEmail` | Required when `Provider=Smtp`. Visible "From" address on outbound mail. |
| `FromName` | Display name beside `FromEmail`. |
| `Smtp:Host` / `Smtp:Port` | SMTP relay endpoint. |
| `Smtp:EnableSsl` | TLS on/off. |
| `Smtp:Username` / `Smtp:Password` | SMTP credentials. **Never commit these — set via environment variables or user-secrets only.** |

### Diagnostics / Swagger
| Key | UAT | Production |
| --- | --- | --- |
| `Swagger:Enabled` | `true` (useful for QA) | `false` |
| `Diagnostics:ExposeExceptionDetails` | `true` | `false` |

### `SeedSuperAdmin`
Bootstrap-seeds a single Super Admin account on startup so the very first
admin can sign in. Idempotent: re-running on a populated DB only ensures
the `SuperAdmin` + `Admin` roles are assigned and **never** resets the
password.

| Key | Purpose |
| --- | --- |
| `Enabled` | `true` to run the seeder; `false` to skip entirely. |
| `Email` | Login email for the seeded user. |
| `PhoneNumber` | Phone number stored on the User (no SMS verification yet). |
| `FirstName` / `LastName` | Display name. |
| `Password` | **Set via environment variable only — never commit.** Used only when creating a brand-new user. |

### `PackageSeed`
Runtime seeder for the initial Smart Future fibre packages (Openserve
20/10 through 500/250). Dedupes by `ExternalReference` (e.g.
`openserve-fibre-100-50`); existing rows are not modified unless
`UpdateExisting=true`. The Openserve 300/150 row is deliberately skipped
because the source price sheet shows R243.00pm, which is below the
neighbouring 100/100 (R920) and almost certainly a typo — add it via the
admin portal once pricing is confirmed.

| Key | Purpose |
| --- | --- |
| `Enabled` | `true` to seed on startup. |
| `UpdateExisting` | `false` (default) skips already-seeded rows. `true` overwrites name / price / features from the seed payload — useful for UAT resets, not recommended in Production. |

---

## Local development

Local dev uses `appsettings.Development.json` (committed, no secrets)
plus optional user-secrets. Run:

```
dotnet user-secrets init
dotnet user-secrets set "JwtSettings:Key" "<your-local-dev-key-at-least-32-chars>"
```

Local dev defaults `EmailSettings:Provider` to `"Logging"`, so no SMTP
credentials are needed.
