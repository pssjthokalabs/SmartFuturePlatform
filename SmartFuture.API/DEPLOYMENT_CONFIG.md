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
