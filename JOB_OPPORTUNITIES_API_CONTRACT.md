# Job Opportunities — API contract (for the mobile app + public website)

Backend reference for Claude 2. Everything here is implemented and green as of
the Phase 1–6 backend build. Nothing has been migrated or deployed yet.

---

## 0. Envelope + conventions

No custom JSON naming policy is configured, so ASP.NET Core's default
**camelCase** applies. Every endpoint returns the standard SmartFuture envelope:

```jsonc
// success
{ "isSuccess": true,  "message": "OK", "data": { /* … */ } }
// failure
{ "isSuccess": false, "code": "NOT_FOUND", "message": "…", "data": null }
```

`code` is the machine-readable branch key; `message` is safe to show a user.

**Enums travel as integers.** Every DTO also carries a `*Label` string
(`statusLabel`, `workplaceTypeLabel`, `frequencyLabel`, …). Send ints, render
labels — don't hardcode label strings.

**`PagedResult<T>`** — the shape of every list `data`:

```jsonc
{
  "items": [ /* … */ ],
  "page": 1, "pageSize": 20, "totalCount": 137, "totalPages": 7,
  "hasPreviousPage": false, "hasNextPage": true
}
```

`page` is 1-based. `pageSize` is clamped server-side (public jobs: 1–100,
default 20).

**Status codes** worth branching on: `400` validation, `401` unauthenticated,
`403` wrong role, `404` not found/module off, `409` conflict (incl.
`ACCOUNT_EXISTS_SIGN_IN_REQUIRED`), `503` provider not configured.

---

## 1. Enum values

| Enum | Values |
|---|---|
| `JobOpportunityStatus` | Draft 0, Active 1, Hidden 2, Expired 3, Deleted 4 |
| `JobWorkplaceType` | Unknown 0, Onsite 1, Hybrid 2, Remote 3 |
| `JobSourceType` | HtmlPage 0, RssFeed 1, Api 2, Manual 3 |
| `JobSubscriberDocumentType` | Cv 0, CoverLetter 1 |
| `JobAlertFrequency` | Daily 0, Weekly 1, Immediate 2 |
| `JobAlertDeliveryStatus` | Pending 0, Sent 1, Failed 2, Skipped 3 |

`Immediate` currently behaves as `Daily` — there is no real-time sender yet.

---

## 2. Public endpoints — no auth required

A bearer token is **optional** on `/api/jobs/*`. If one is sent it is parsed and
used to decide subscriber gating; if not, the caller is simply treated as a
non-subscriber.

### `GET /api/jobs` → `PagedResult<JobOpportunityDto>`

Query params (all optional): `search`, `category`, `location`,
`workplaceType` (int), `source` (source **name**, not id), `isFeatured` (bool),
`page`, `pageSize`, `sort`.

`sort` accepts `closing` | `title` | `company` | `oldest`. Anything else
(including omitted) = newest first, with featured jobs floated to the top.

Only Active jobs whose closing date hasn't passed are ever returned.
**The list projection never includes the long-form fields**, for anyone.

### `GET /api/jobs/{slugOrId}` → `JobOpportunityDto`

Accepts the slug or the raw GUID, so older shared links keep resolving.
404 when the job is hidden/expired/deleted or the module is off.

### `GET /api/jobs/categories` → `JobCategoryDto[]`
`{ category, jobCount }` — counts obey the same visibility rule as the list.

### `GET /api/jobs/locations` → `JobLocationDto[]`
`{ location, province, jobCount }` — city-first, falling back to free-text
location; merged case-insensitively.

### `GET /api/jobs/sources` → `JobSourceFacetDto[]`
`{ sourceName, jobCount }`.

### `GET /api/jobs/settings` → `PublicJobSettingsDto`

```jsonc
{ "jobsModuleEnabled": true, "jobDetailsSubscribersOnly": false,
  "jobAlertsEnabled": false, "publicDisclaimer": null }
```

**Call this first.** `jobsModuleEnabled: false` → hide the module entirely.
This endpoint never 500s; on an internal error it degrades to safe defaults.

### `JobOpportunityDto`

Always present: `id`, `slug`, `title`, `companyName`, `location`, `country`,
`province`, `city`, `category`, `workplaceType` + `workplaceTypeLabel`,
`employmentType`, `salaryText`, `summary`, `logoUrl`, `tags[]`,
`postedDateUtc`, `closingDateUtc`, `isClosingSoon`, `isExpired`, `isFeatured`,
`sourceName`, `sourceUrl`, `requiresSubscription`.

Detail-only + **subscriber-gated**: `descriptionHtml`, `descriptionText`,
`requirementsText`, `applicationInstructions`, `applyUrl`, `applyEmail`.

> `sourceUrl` is deliberately **never** gated — a reader must always be able to
> verify a listing at its origin.

---

## 3. Subscriber gating — the behaviour to build against

When `jobDetailsSubscribersOnly` is **on** and the caller is not a
JobSubscriber, `GET /api/jobs/{slugOrId}` still returns **200** with the full
card payload, the six gated fields `null`, and:

```json
{ "requiresSubscription": true }
```

Render a teaser + a sign-up CTA. **Do not treat this as an error.** Admins and
SuperAdmins count as subscribers so support staff see what a subscriber sees.

---

## 4. Auth + enrolment

There is **one** auth system. Job subscribers sign in through the existing
`POST /api/auth/login`. `POST /api/job-subscribers/login` is a verbatim
passthrough alias, provided only so the jobs client can keep one base path.

`CurrentUserDto` (returned by login, refresh, and `/api/auth/me`) now carries:

```jsonc
{ "isCustomer": true, "isJobSubscriber": true, "roles": ["Customer","JobSubscriber"] }
```

**Branch on each flag independently — they are not mutually exclusive.** A
person can legitimately be both.

### `POST /api/job-subscribers/register` — anonymous OR signed in

```jsonc
{ "firstName": "…", "lastName": "…", "email": "…", "phoneNumber": "…",
  "password": "…", "confirmPassword": "…",
  "currentCity": "…", "currentProvince": "…",
  "preferredCategories": ["…"], "preferredLocations": ["…"],
  "subscribeToAlerts": true }
```

Three outcomes, all returning `AuthTokenDto` on success:

| Caller | Behaviour |
|---|---|
| **Signed in** (bearer sent) | JobSubscriber role added to *that* user. Email/password fields ignored. No new user, ever. |
| **Anonymous, new email** | User created with **JobSubscriber only** — no Customer role, no `CustomerProfile`. |
| **Anonymous, known email + correct password** | Role added to the existing account. One user row. |

### `POST /api/job-subscribers/enrol` — signed in only

Same handler, body optional. Use this for the "existing customer wants job
access" journey; it reads clearer at the call site.

### The 409 you must handle

`ACCOUNT_EXISTS_SIGN_IN_REQUIRED` — the email belongs to an existing account and
the supplied password did **not** match (or the account is inactive).

> Route the user to **sign in**, then call `POST /api/job-subscribers/enrol`
> with the bearer token. Do **not** surface this as "email already taken" — that
> dead end is exactly what this code exists to avoid.

Knowing an email is not proof of ownership, so no role is ever attached without
a verified password or an authenticated session.

### Customer signup is unchanged

`POST /api/auth/register` with an email that already holds the **Customer** role
still returns `EMAIL_TAKEN`. Only a non-customer account (today: a
JobSubscriber) is upgraded in place, and only on a verified password.

---

## 5. Subscriber self-service — `RequireJobSubscriber`

All of these need the JobSubscriber role (Admin/SuperAdmin also pass).

### `GET /api/job-subscribers/me` → `JobSubscriberMeDto`

```jsonc
{ "userId": "…", "firstName": "…", "lastName": "…", "email": "…",
  "phoneNumber": "…", "roles": ["Customer","JobSubscriber"],
  "isJobSubscriber": true, "isCustomer": true,
  "hasProfile": true, "isProfileComplete": false,
  "missingFields": ["cv"],
  "profile": { /* JobSubscriberProfileDto */ },
  "alertPreference": { /* JobAlertPreferenceDto */ } }
```

**`missingFields` drives the wizard.** Possible values: `"profile"`, `"cv"`,
`"currentLocation"`. A profile is complete once a CV plus a city *or* province
is present. Cover letter is optional and never blocks completion.

### `POST` / `PUT /api/job-subscribers/me/profile`

Both verbs hit the same upsert. Every field optional; **`null` means "leave
unchanged"**, so a partial patch is safe.

`preferredFirstName`, `contactPhone`, `currentCity`, `currentProvince`,
`currentCountry`, `linkedInProfileUrl`, `websiteUrl`, `salaryExpectations`,
`highestQualification`, `yearsOfExperience` (0–70),
`preferredCategories[]`, `preferredLocations[]`.

LinkedIn/website must be valid `http(s)` URLs or you get a 400.

### `JobSubscriberProfileDto` — document metadata only

`hasCv`, `cvFileName`, `cvSizeBytes`, `cvUploadedAtUtc`, and the cover-letter
equivalents. **No object key and no storage URL are ever returned.**

---

## 6. CV / cover letter

### Upload — `POST /api/job-subscribers/me/cv` · `/me/cover-letter`

`multipart/form-data`, single field named **`file`**.

- Max **5 MB** (`RequestSizeLimit`; a bigger body is rejected before buffering).
- Accepted: **pdf, doc, docx, txt, rtf**. The **extension** is authoritative —
  mobile clients routinely send `application/octet-stream` for a valid PDF, so
  the declared content type is not trusted.
- CV required for profile completion; cover letter optional.
- Re-uploading supersedes the previous file; history is retained server-side.

Returns `JobSubscriberDocumentDto`:
`{ documentType, documentTypeLabel, fileName, contentType, sizeBytes, uploadedAtUtc }`.

### Download — `GET /api/job-subscribers/me/cv` · `/me/cover-letter`

Streams the **binary** back with `Content-Disposition` — this is the one place
that does *not* return the JSON envelope on success. Failures still do.

Send the bearer in the `Authorization` header; the objects are private in R2 and
there is **no anonymous URL to link to**. `fetch` → `blob` → object URL, don't
put the endpoint in an `<img>`/`<a href>` and expect it to work.

404 when nothing has been uploaded yet.

---

## 7. Alerts

- `GET /api/job-subscribers/me/alerts` → `JobAlertPreferenceDto`
- `PUT /api/job-subscribers/me/alerts` — `{ isSubscribed, frequency, categories[], locations[], keywords[] }`, all optional
- `POST /api/job-subscribers/unsubscribe?token=…` — **anonymous**, for the
  one-click link in alert emails. Always returns success; it never confirms
  whether a token was real.

Empty preference arrays mean "everything". Within a facet, any match qualifies;
across facets, all configured facets must match.

There is **no scheduler** — digests only go out when an admin triggers them, and
only while `jobAlertsEnabled` is on.

---

## 8. Error codes to branch on

| Code | HTTP | Meaning |
|---|---|---|
| `ACCOUNT_EXISTS_SIGN_IN_REQUIRED` | 409 | Route to sign-in, then `/enrol`. Not a dead end. |
| `EMAIL_TAKEN` | 409 | Existing **Customer** re-registering. Unchanged behaviour. |
| `PHONE_TAKEN` | 409 | Canonical phone already on another account. |
| `VALIDATION_ERROR` | 400 | Field-level problem; `message` is user-safe. |
| `WEAK_PASSWORD` | 400 | Identity password policy (8+, upper, lower, digit). |
| `UNAUTHORIZED` | 401 | No/expired token. |
| `FORBIDDEN` | 403 | Missing role. |
| `NOT_FOUND` | 404 | Gone, hidden, expired, or module disabled. |
| `PROVIDER_NOT_CONFIGURED` | 503 | R2 env vars absent — uploads/downloads unavailable. |

---

## 9. Known gaps (by design, not bugs)

- No background alert scheduler; admin-triggered, dry-run by default.
- Auto-import defaults **off**.
- `JobSourceType.Api` has no extractor and reports so explicitly.
- Multi-role articles import as **one grouped opportunity** in v1.
- `Immediate` alert frequency behaves as `Daily`.
