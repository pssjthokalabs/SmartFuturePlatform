# Client Services + Order → Installation Lifecycle

Status: **research note — implementation deferred to a follow-up
phase.** Phase 36 polished the existing admin pages and added
pagination; the operational lifecycle work captured here is too large
to land alongside the polish work safely.

## Current state (post-Phase 36)

What works today end-to-end:

1. Customer self-registers (`POST /api/auth/register`).
2. Customer places an order (`POST /api/orders`) — Order created with
   `Status=Submitted`.
3. UAT-only: mock-Ozow checkout persists an Invoice + Payment in the
   same call (Phase 28/31, gated by `PaymentSettings:MockCheckoutEnabled`).
4. Admin views the order in `/admin/orders/{id}`.
5. Admin manually sets an install date + updates the order status.
6. Admin manually creates an Installation record via the existing
   `/api/installations/admin` POST endpoint.

What does **not** work today:

- There's no link between "admin set an install date" and "an
  Installation record exists for this order". A clerk has to do both
  steps; nothing surfaces the missing installation.
- There's no `ClientService` / `NetworkAccount` admin surface in the
  portal. The backend has `NetworkAccount` (provisioned services
  linked to an order via `NetworkAccountSource`), but no admin page
  reads/writes it.
- Suspend / Reactivate / Activate-on-installation flows aren't wired
  through the admin UI. Backend supports termination via
  `INetworkAccountService.TerminateForOrderAsync(...)`, used today
  only when an order moves to a terminal status.
- Technician assignment exists in the DB (`InstallationDto` carries
  `TechnicianEmail` + `TechnicianPhone`) but there's no
  `Technician` user role + lookup, so admins type the email by hand.

## Target lifecycle (when fully wired)

1. **Client places order** → Order `Submitted`. Email sent.
2. **Payment confirmed** (mock or real Ozow webhook later) → Invoice
   `Paid`, Payment `Completed`. Order **stays** `Submitted`.
3. **Admin verifies / reviews order** → Order `Confirmed` (optional
   intermediate state).
4. **Admin schedules install** → Order `Scheduled` / installation
   gets a `ScheduledForUtc`. **At this step the system creates the
   `Installation` record automatically** if none exists for the order.
5. **Admin assigns technician** from a `Technician` user list.
6. **Install progresses**: `Scheduled` → `InProgress` → `Completed`.
7. **On `InstallationStatus = Completed`** the system creates the
   `NetworkAccount` (`ClientService`) row, status `Active`. Only at
   this point does the customer's `/client/services` page show an
   active service. Service activation is intentionally **not** tied
   to payment.
8. Admin can `Suspend` / `Reactivate` an active service from
   `/admin/services` (new page, see below).

## Backend gaps for the follow-up phase

| Area | Current | Needed |
| --- | --- | --- |
| Order → Installation linking | Manual two-step. Admin sets date + creates Installation separately. | When admin updates order to `Scheduled` (or sets ExpectedInstallationDate via `AdminUpdateAsync`), `OrderService` should auto-create an `Installation` row with `Status=Scheduled`, `OrderId`, customer + package snapshot, `ScheduledForUtc = ExpectedInstallationDateUtc`. Idempotent — if an installation already exists for the order, update its `ScheduledForUtc` instead. |
| Installation → Service activation | `NetworkAccount` exists; nothing currently auto-creates one on `Installation.Status = Completed`. | `InstallationService.AdminUpdateStatusAsync(...)` should, when the new status is `Completed`, call `NetworkAccountService.ActivateForOrderAsync(orderId)` to create / re-activate the `NetworkAccount`. Idempotent. |
| Client Services admin DTO / endpoint | `NetworkAccount` is the right entity but it has no admin search / detail endpoint exposed yet (only the internal termination hook). | Add `GET /api/network-accounts/admin` (paged list, filter by customer/order/status) and `POST /api/network-accounts/admin/{id}/suspend` + `/reactivate`. |
| Technician users | No `Technician` role; no admin user list returns them. | Add `Technician` to `SystemRoles`, plus an admin endpoint `GET /api/users/admin?role=Technician`. Until then the admin installation page just shows "Unassigned" and accepts a free-text email like today. |

## Frontend gaps for the follow-up phase

| Area | Current | Needed |
| --- | --- | --- |
| `/admin/services` page | Doesn't exist. | New page lists `NetworkAccount` rows (service ref, customer, package, address, status `Active`/`Suspended`/`PendingActivation`, activated date). Suspend / Reactivate actions hit the new admin endpoints. Sidebar entry under Operations. |
| Order detail "Schedule Installation" button | Order detail has "Set Install Date" + "Update Status" buttons today. There's no single action that says "Schedule Installation". | Add a "Schedule Installation" CTA on the order detail that takes a date, posts to the existing `AdminUpdateAsync` with `ExpectedInstallationDateUtc`, then optionally navigates to the newly-created Installation detail. Disabled if order already has an installation. |
| Technician dropdown | Free-text email today. | When the technician endpoint exists, replace the input with a dropdown that defaults to "Unassigned". |
| Customer "My Services" | Already filtered to `NetworkAccount`-backed services (Phase 28). | No change. |

## What Phase 36 explicitly does NOT do

- Does **not** auto-create `Installation` records from `OrderService`.
- Does **not** auto-create `NetworkAccount` records from
  `InstallationService`.
- Does **not** add a `/admin/services` page.
- Does **not** add a Technician role or user filter.
- Does **not** mark services Active when payment lands.

Phase 36 keeps the existing admin flows working (Mark Payment
Verified, Set Install Date, Update Status, create Installation via
`InstallationService.CreateAsync`). The polish work (pagination,
real user display, KPI grid, Users rename, OTP reset, etc.) is
independent of this lifecycle work and is safe to ship without it.

## Suggested follow-up phase split

Splitting the lifecycle work into two focused phases is safer than
landing it all at once:

**Phase 37 — Backend lifecycle wiring**
- Auto-create Installation when order is scheduled.
- Auto-create NetworkAccount when installation completes.
- Add `Technician` role.
- Add `GET /api/network-accounts/admin` + suspend/reactivate.
- Migration + integration tests for state transitions.

**Phase 38 — Admin Client Services page**
- New `/admin/services` listing + detail.
- Order detail "Schedule Installation" CTA.
- Technician dropdown on installation forms.
- Sidebar entry; update Dashboard "Active Services" KPI to read from
  the new endpoint.

Until both phases land the admin still has the manual path that
works today; the only inconvenience is the two-step
date-set-then-installation-create dance.
