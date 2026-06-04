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

# Simple Microsoft 365 SMTP — Phase 35C primary path. Templates send
# HTML + plain-text from `EmailSettings__FromEmail` regardless of
# `SenderType` (a single mailbox keeps testing simple). The Phase 35
# multi-sender pool (`Provider=MultiSmtp`) is still compiled but no
# longer required for any flow.
EmailSettings__Provider=Smtp
EmailSettings__FromEmail=noreply@smartfuture.co.za
EmailSettings__FromName=Smart Future
EmailSettings__Smtp__Host=smtp.office365.com
EmailSettings__Smtp__Port=587
EmailSettings__Smtp__EnableSsl=true
EmailSettings__Smtp__Username=noreply@smartfuture.co.za
EmailSettings__Smtp__Password=<smtp-password>

# Phase 35D — single-mailbox test-mode override. When Enabled=true
# this forces ALL outbound email through one SMTP account and
# ignores SenderType. Useful for UAT debugging when you don't want
# to set up the multi-sender pool or the production mailbox. Leave
# it off in normal UAT once SMTP is working.
# EmailTestMode__Enabled=true
# EmailTestMode__Host=smtp.office365.com
# EmailTestMode__Port=587
# EmailTestMode__EnableSsl=true
# EmailTestMode__Username=developers@smartfuture.co.za
# EmailTestMode__Password=<smtp-password>
# EmailTestMode__FromEmail=developers@smartfuture.co.za
# EmailTestMode__FromName=Smart Future Test

# Twilio (Phase 35C) — supplied via env when ready. With AccountSid
# + AuthToken set, future concrete providers will use this block;
# until then the NotConfigured stubs continue to return
# PROVIDER_NOT_CONFIGURED. Twilio is never used for email.
# Twilio__AccountSid=<twilio-account-sid>
# Twilio__AuthToken=<twilio-auth-token>
# Twilio__Sms__FromPhoneNumber=+27821234567
# Twilio__Sms__MessagingServiceSid=<optional-messaging-service-sid>
# Twilio__WhatsApp__FromPhoneNumber=whatsapp:+14155238886
# Twilio__WhatsApp__DefaultContentSid=<optional-template-sid>
# Twilio__Verify__ServiceSid=<verify-service-sid>
# Twilio__Verify__ResendCooldownSeconds=30

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

Production is split across two portal subdomains:
  - Admin Portal:  `https://admin.smartfuture.co.za`
  - Client Zone:   `https://clientzone.smartfuture.co.za`

Both run the same SmartFuturePortal codebase, built with
`VITE_PORTAL_MODE=admin` and `=client` respectively. Reset-password
links must point at the host that hosts each surface.

```
ASPNETCORE_ENVIRONMENT=Production

# IMPORTANT: ConnectionStringResolver picks `LiveConnection` when the
# environment name is "Production" or "Live" — not UatConnection.
# Setting __UatConnection on Production crashes the API at boot with
# "ConnectionStrings:LiveConnection is not configured".
ConnectionStrings__LiveConnection=<live-db-connection-string>

JwtSettings__Issuer=https://api.smartfuture.co.za
# Audience covers both portal subdomains. The API doesn't issue
# different tokens per portal — one JWT works on both surfaces.
JwtSettings__Audience=https://admin.smartfuture.co.za
JwtSettings__Key=<strong-secret-key-at-least-32-chars>
JwtSettings__AccessTokenMinutes=60
JwtSettings__RefreshTokenDays=14

# CORS — one entry per browser origin that calls the API. No
# wildcards (the API rejects them when AllowCredentials is on).
Cors__AllowedOrigins__0=https://admin.smartfuture.co.za
Cors__AllowedOrigins__1=https://clientzone.smartfuture.co.za
Cors__AllowedOrigins__2=https://smartfuture.co.za
Cors__AllowedOrigins__3=https://www.smartfuture.co.za
# Keep UAT origin allowed if Production API is also used as a
# fallback by the UAT portal during cut-over; remove once the
# split has stabilised.
# Cors__AllowedOrigins__4=https://uat.portal.smartfuture.co.za

Database__ApplyMigrationsOnStartup=false
Swagger__Enabled=false
Diagnostics__ExposeExceptionDetails=false

# Reset-password links embedded in emails. The path must match the
# React route exposed on each portal subdomain. Internal routes
# remain `/admin/reset-password` and `/client/reset-password`; only
# the host changes per surface.
FrontendSettings__AdminResetPasswordUrl=https://admin.smartfuture.co.za/admin/reset-password
FrontendSettings__ClientResetPasswordUrl=https://clientzone.smartfuture.co.za/client/reset-password

EmailSettings__Provider=Smtp
EmailSettings__FromEmail=no-reply@smartfuture.co.za
EmailSettings__FromName=Smart Future
EmailSettings__Smtp__Host=<smtp-host>
EmailSettings__Smtp__Port=587
EmailSettings__Smtp__EnableSsl=true
EmailSettings__Smtp__Username=<smtp-user>
EmailSettings__Smtp__Password=<smtp-password>

# Phase 35D — keep test-mode OFF in Production. With this enabled,
# every customer email would route through a single test mailbox.
# The startup banner prints `EmailTestMode:Enabled` on every boot so
# a stray override is impossible to miss.
EmailTestMode__Enabled=false

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

### `EmailTestMode` (single-mailbox override — Phase 35D)

When `Enabled=true`, **every** outbound email is routed through the
mailbox in this section and the request's `SenderType` is ignored.
Templates still render (HTML + plain-text alternate view); the only
difference is the From-address and the SMTP credentials used to send.
Intended for local development and UAT debugging when you don't want
to configure the multi-sender pool or production mailboxes.

**Production policy**: keep `EmailTestMode__Enabled=false` on the
Production app-pool. The startup banner prints the resolved value at
every boot to make a stray override obvious.

Selection priority (see `AddEmailServices`):

1. `EmailTestMode:Enabled = true` → `TestModeSmtpNotificationSender`
2. `EmailSettings:Provider = MultiSmtp` → `SmtpMultiSenderEmailSender`
3. `EmailSettings:Provider = Smtp` → `SmtpEmailSender`
4. anything else / unset → `LoggingNotificationSender`

| Key | Purpose |
| --- | --- |
| `EmailTestMode:Enabled` | Master switch. Default `false`. |
| `EmailTestMode:Host` / `Port` / `EnableSsl` | SMTP endpoint. For Microsoft 365 use `smtp.office365.com` / `587` / `true`. |
| `EmailTestMode:Username` | SMTP-AUTH user. Typically a licensed mailbox. |
| `EmailTestMode:Password` | **Secret.** Supply via env var / user-secrets / app-pool only. Never commit. |
| `EmailTestMode:FromEmail` | Visible From-address; used as-is regardless of `SenderType`. |
| `EmailTestMode:FromName` | Display name beside `FromEmail`. Defaults to `Smart Future Test`. |

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

### `Twilio` (SMS / WhatsApp / Verify — Phase 35C nested shape)

Phase 35 lands the abstractions; Phase 35C restructures the config to
nested per-channel groups so future concrete providers don't need a
config migration. A concrete Twilio implementation is still deferred —
the registered providers are NotConfigured stubs that return
`PROVIDER_NOT_CONFIGURED` / `SMS_NOT_CONFIGURED` and log the attempt
without secrets. Twilio is never used for email.

| Key | Purpose |
| --- | --- |
| `Twilio:AccountSid` | Twilio account SID. Blank value means Twilio is treated as not configured. |
| `Twilio:AuthToken` | **Secret.** Supply via env var / user-secrets only. |
| `Twilio:ApiKeySid` / `Twilio:ApiKeySecret` | Optional API key pair (preferred over `AuthToken` long-term). **Secret.** |
| `Twilio:Sms:FromPhoneNumber` | E.164 SMS sender, e.g. `+27821234567`. Required unless `MessagingServiceSid` is set. |
| `Twilio:Sms:MessagingServiceSid` | Optional Twilio Messaging Service SID. |
| `Twilio:WhatsApp:FromPhoneNumber` | WhatsApp sender, e.g. `whatsapp:+14155238886`. |
| `Twilio:WhatsApp:MessagingServiceSid` | Optional WhatsApp Messaging Service SID. |
| `Twilio:WhatsApp:DefaultContentSid` | Optional approved template (Content) SID for WhatsApp messages. |
| `Twilio:Verify:ServiceSid` | Twilio Verify service SID — required for OTP send/check via Twilio Verify. |
| `Twilio:Verify:ResendCooldownSeconds` | Minimum seconds between resend requests for the same number. Defaults to 30. |

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
