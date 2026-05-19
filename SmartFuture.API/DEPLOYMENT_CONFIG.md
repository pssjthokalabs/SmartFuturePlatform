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

# Multi-sender Microsoft 365 SMTP pool (Phase 35). Switch
# `EmailSettings__Provider=MultiSmtp` to activate the multi-sender
# implementation. Host/port/SSL/FromEmail/FromName placeholders live
# in appsettings.json; only the SMTP user + password come from env.
# EmailSettings__Provider=MultiSmtp
# EmailProviders__Senders__NoReply__Username=<licensed-smtp-user-or-noreply-mailbox>
# EmailProviders__Senders__NoReply__Password=<smtp-password>
# EmailProviders__Senders__Support__Username=<licensed-smtp-user-or-support-mailbox>
# EmailProviders__Senders__Support__Password=<smtp-password>
# EmailProviders__Senders__Accounts__Username=<licensed-smtp-user-or-accounts-mailbox>
# EmailProviders__Senders__Accounts__Password=<smtp-password>
# EmailProviders__Senders__Payments__Username=<licensed-smtp-user-or-payments-mailbox>
# EmailProviders__Senders__Payments__Password=<smtp-password>
# EmailProviders__Senders__Security__Username=<licensed-smtp-user-or-noreply-mailbox>
# EmailProviders__Senders__Security__Password=<smtp-password>

# Twilio is not configured by default (Phase 35 lands the abstraction
# only — real provider deferred). When set, AccountSid + AuthToken
# unlock the future Twilio SMS / WhatsApp / Verify implementations.
# Twilio__AccountSid=<twilio-account-sid>
# Twilio__AuthToken=<twilio-auth-token>
# Twilio__VerifyServiceSid=<verify-service-sid>
# Twilio__SmsFromNumber=+27821234567
# Twilio__WhatsAppFromNumber=whatsapp:+14155238886

PaymentSettings__MockCheckoutEnabled=true

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

# PaymentSettings__MockCheckoutEnabled MUST stay false in Production.
# When true (UAT only), the customer order endpoint also persists a
# fake Invoice + Payment so the billing surface can be exercised
# end-to-end before real Ozow integration lands. Leaving it on in
# Production would let clients trigger "Paid" invoices without ever
# moving money.
PaymentSettings__MockCheckoutEnabled=false

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
| `Provider` | `"Logging"` (default, no real delivery), `"Smtp"` (single-sender legacy), or `"MultiSmtp"` (Phase 35 multi-sender pool — see `EmailProviders` below). Picked up at startup by `AddEmailServices`. |
| `FromEmail` | Required when `Provider=Smtp`. Visible "From" address on outbound mail. Ignored by `MultiSmtp` — see `EmailProviders:Senders:*:FromEmail`. |
| `FromName` | Display name beside `FromEmail`. |
| `Smtp:Host` / `Smtp:Port` | SMTP relay endpoint. |
| `Smtp:EnableSsl` | TLS on/off. |
| `Smtp:Username` / `Smtp:Password` | SMTP credentials. **Never commit these — set via environment variables or user-secrets only.** |

### `EmailProviders` (multi-sender SMTP pool — Phase 35)

Activated when `EmailSettings:Provider=MultiSmtp`. Lets each logical
sender (`NoReply` / `Support` / `Accounts` / `Payments` / `Security`)
have its own From-address + SMTP credentials. Templates declare the
sender they want; the runtime resolves it via this section.

| Key | Purpose |
| --- | --- |
| `DefaultSender` | Logical sender name (`NoReply` / `Support` / …) used when the caller doesn't specify one. Defaults to `NoReply`. |
| `Senders:<name>:Host` / `Port` / `EnableSsl` | SMTP endpoint for that sender. Typically `smtp.office365.com` / `587` / `true` for Microsoft 365. |
| `Senders:<name>:Username` | SMTP-AUTH user. On Microsoft 365 this must be a **licensed mailbox or SMTP-enabled account**. If a sender's Username is blank, the runtime falls back to `Senders:Default:Username` — useful when one licensed mailbox holds Send-As rights for all shared mailboxes. |
| `Senders:<name>:Password` | **Secret.** Supply via env var / user-secrets / app-pool only. Never commit. |
| `Senders:<name>:FromEmail` | Visible From-address. For shared Microsoft 365 mailboxes (e.g. `accounts@…`), this can differ from `Username` provided **Send As** permission is granted to the SMTP-AUTH mailbox. |
| `Senders:<name>:FromName` | Display name shown beside the From-address. |

**Microsoft 365 SMTP notes**

- Host / port / TLS: `smtp.office365.com`, `587`, STARTTLS (`EnableSsl=true`).
- SMTP AUTH may be disabled at tenant or mailbox level. Enable it for the SMTP-AUTH user under Microsoft 365 Admin Center → Active users → Mail → Manage email apps → Authenticated SMTP.
- Each licensed mailbox (`noreply@`, `support@`, `accounts@`, `payments@`) can authenticate as itself; or one licensed mailbox can be used as the SMTP user for all senders if Send-As is granted on each shared mailbox.
- DNS: ensure `smartfuture.co.za` SPF/DKIM/DMARC records cover Microsoft 365 outbound, otherwise recipients will mark the mail as spam.

### `Twilio` (SMS / WhatsApp / Verify — interfaces only, Phase 35)

Phase 35 lands the abstractions (`ISmsProvider`, `IWhatsAppProvider`,
`IPhoneVerificationService`) and the `TwilioSettings` POCO. A concrete
Twilio implementation is deferred — the registered providers are
NotConfigured stubs that return `PROVIDER_NOT_CONFIGURED` /
`SMS_NOT_CONFIGURED` and log the attempt without secrets.

| Key | Purpose |
| --- | --- |
| `AccountSid` | Twilio account SID. Blank value means Twilio is treated as not configured. |
| `AuthToken` | **Secret.** Supply via env var / user-secrets only. |
| `ApiKeySid` / `ApiKeySecret` | Optional API key pair (preferred over `AuthToken` long-term). **Secret.** |
| `VerifyServiceSid` | Twilio Verify service SID — required for OTP send/check via Twilio Verify. |
| `SmsFromNumber` | E.164 SMS sender, e.g. `+27821234567`. |
| `WhatsAppFromNumber` | WhatsApp sender, e.g. `whatsapp:+14155238886`. |

Until real values are supplied the existing `SMS_NOT_CONFIGURED`
behaviour persists: `AuthService.RequestChangePasswordCodeAsync` with
`channel=Sms` continues to return the same friendly error.

### Diagnostics / Swagger
| Key | UAT | Production |
| --- | --- | --- |
| `Swagger:Enabled` | `true` (useful for QA) | `false` |
| `Diagnostics:ExposeExceptionDetails` | `true` | `false` |

### `PaymentSettings`
UAT-only mock-checkout switch for the customer order flow.

| Key | Purpose |
| --- | --- |
| `MockCheckoutEnabled` | `true` (UAT) makes `POST /api/orders` additionally create an Invoice + Payment when the request carries the Phase 27 mock Ozow hint. Server-authoritative amount = monthly + installation. Order status is unchanged (admin still owns activation). **`false` in Production.** When `false`, the mock fields are silently ignored. |

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
