# Openserve Integration — Research Notes

Status: **superseded for the Product Ordering / fulfilment API** — see
"2026-09-09 update" below. Still current for the Product Qualification
sections (coverage-check) which this file's `OpenserveFibreCoverageProvider`
already covers via the public, unauthenticated GIS endpoint.
Owner: Smart Future engineering.

## 2026-10-01 update — provisioned STAGING config + Postman collection v02

Openserve supplied Smart Future's own staging/UAT tenant plus the
"Fulfilment API Collection (Broadband)" Postman collection v02. The
collection is now the executable contract; where it disagrees with the
PDF (ITSD-179559 Rev 04.002) the collection wins.

| Postman variable | Setting (`OpenserveFulfilment:*` / admin console) | Staging value |
|---|---|---|
| `HOST_URL` | `BaseUrl` (stored with `https://`) | `stapitrx.openserve.co.za` |
| `API_KEY` | `ApiKey` (secret, DataProtection-encrypted) | supplied separately |
| `isp_tag` | `WsIspCode` | `ws-marut` |
| `ISPID` | `IspIdentifier` | `WS MARUT` |
| `SenderID` | `SenderId` | `SMARTFUTURE` |
| `ReplyToAddress` | `ReplyToAddress` | `https://stapitrx.openserve.co.za/ws-marut/productordercallback` |

Implemented operations (all under `https://{HOST_URL}/{isp_tag}/`):

| Operation | Method + path | FromLocation | ReplyToAddress |
|---|---|---|---|
| Product Qualification (AMID / LatLong) | `GET productqualification?AMID=..&BuildingInfo=Y` / `?LAT=..&LON=..&BuildingInfo=Y` | `isp_tag` | not sent |
| Create New Order (UC 1) | `POST productorder` | `ISPID` | sent |
| Query Order Details | `GET getproductorder/{order_id}` | `ISPID` | sent |
| Cancel Inflight Order | `POST cancelproductorder` | `ISPID` | sent |

Every call also sends `MessageID` (fresh UUID), `SenderID` and `api_key`.

Corrections made against the collection: GET order path
(`/productorder/{id}` → `/getproductorder/{id}`), create path casing
(`/productOrder` → `/productorder`), `FromLocation` was never sent,
`ReplyToAddress` was wrongly sent on qualification, place `@type: "A"`
was missing, MDU `buildingName/floor/unit/buildingNumId` were never
populated, and Test Connection used the PDF-only `/upp/getactions`.

**ReplyToAddress is an Openserve-provided URL** — not our
`/api/openserve/callback`. The collection contains no inbound callback
or event payloads, no registration mechanism and no authentication
scheme (the PDF lists callback/event auth as "N/a"). Our inbound
endpoints are preserved but unconfirmed; GET `getproductorder` polling
is the confirmed status path. Not implemented (outside the current
fulfilment flow): UC 2 change ownership, UC 3 cease, UC 4 change speed,
UC 5 change product, Suspend/Resume/GetServiceDetails (note: the
collection uses `/wsaccessservice` for suspend/resume, the PDF
`/wsserviceaccess`).

## 2026-09-09 update — Fulfilment API spec received, Phase 1 landed

The client supplied the authoritative "Openserve Fulfilment API
Specification" (ITSD-179559 Rev 04.002) plus a reseller reference
("Smartfuture 04"). This answers several of the "What we need to
confirm" items below:

- **API access**: confirmed — reseller Product Ordering, Product
  Qualification, Product Inventory, Suspend/Resume and Comments APIs
  are documented. Auth is a single `api_key` header (no OAuth/mTLS).
- **Order provisioning**: confirmed async — `POST .../productOrder`
  returns a sync ack (order id + Validated state), then Openserve
  posts a full result to a `ReplyToAddress` callback, AND pushes
  ongoing `ProductOrderCreateEvent`/`ProductOrderStateChangeEvent`/
  `CancelProductOrderCreateEvent`/`CancelProductOrderStateChangeEvent`
  notifications to a separately-registered event endpoint.
- **Installation/fault tracking**: the spec has NO dedicated
  appointment/technician fields despite a changelog entry claiming one
  was added (rev 03.001) — full details in
  `SmartFuture.Application/Openserve/OpenserveFulfilmentSettings.cs`
  remarks and the project's Openserve scratch notes.

Still genuinely open (not answered by the spec document itself):

- Production BaseUrl (doc only gives the shared test host).
- Whether the client's "Smartfuture 04" reference is the `{ws-ispcode}`
  URL segment, the "ISP Identifier" payload field, or both — kept as
  two independently configurable settings pending confirmation.
- Callback/event authentication scheme (spec documents none).
- Whether a fuller version of the spec exists with the referenced
  Appointment API / Figure 1 event-transition diagram.

**Phase 1 landed** (config scaffolding, `OpenserveOrder` /
`OpenserveOrderStatusHistory` / `OpenserveIntegrationLog` /
`PackageOpenserveMapping` entities + migration, admin package-mapping
API) — everything gated behind `OpenserveFulfilment:Enabled=false`.
No order-submission, callback, or notification logic exists yet
(Phase 2/3). See `SmartFuture.Application/Openserve/` and
`SmartFuture.Domain/Openserve/`.

## Purpose

Smart Future resells fibre on the Openserve network. Customers in the
Client Zone need to:

1. Check whether their address is covered by Openserve fibre.
2. Place an order against a covered service package.
3. Track installation, faults and service state.

This document captures what we know about Openserve's available
surfaces, what we still need to confirm with Openserve / the
customer's account team, and what the interim manual workflow looks
like. **Do not implement any Openserve API calls until the questions
below have answers in writing.**

## What Openserve exposes publicly

- A consumer-facing coverage-map / address-check page on the
  Openserve website.
- A self-service app for end customers (account management, fault
  logging, "where is my technician").

These are **not stable integration points** — they're built for end
users and the response shapes / availability are at Openserve's
discretion.

## What we need to confirm

Before any integration work begins, we need written answers from
Openserve or our reseller account manager on:

1. **API access**
   - Does Smart Future have ISP / reseller portal access?
   - Is there a documented REST or SOAP API for resellers? Where?
   - Auth method (OAuth2 client-credentials, API key, mTLS, IP-allowlist)?

2. **Coverage availability**
   - Endpoint for "is this address covered?" lookup.
   - Required input shape — full address, geocoded lat/lng, ERF
     number, suburb/street?
   - Response shape — boolean coverage, list of available speeds /
     CPE options, estimated install date, FTTH vs FTTB?
   - Rate limits (per IP, per account, per minute / hour / day)?
   - Allowed display terms — can we present results inline in the
     Client Zone, or must we frame Openserve's branding?

3. **Order provisioning**
   - Endpoint to submit a new service order.
   - Required fields — customer details, package SKU mapping,
     install date preferences, on-site contact, indemnity flags?
   - Async/sync — does the order return a tracking reference
     immediately, then update by webhook, or do we poll?

4. **Installation / fault tracking**
   - Endpoint to query install status by reference.
   - Endpoint to log faults against an active service.
   - Webhook delivery — how does Openserve push status updates to
     us, and what's the signature scheme?

5. **Commercial / legal**
   - Acceptable usage policy for the coverage check (e.g. can we
     cache it; for how long?).
   - SLA on the integration endpoints.
   - Branding / attribution rules in the Client Zone.

## What we will NOT do

- Scrape Openserve's public coverage-map page.
- Reverse-engineer the consumer app's private endpoints.
- Spoof / proxy customer browsers to query coverage on their behalf.

These are fragile (Openserve can change them any day) and put us in
breach of the consumer-app terms. The above questions get formal
answers first.

## Interim workflow (no API)

Until reseller access lands, the customer-facing coverage check is a
form, and admin reviews it manually:

1. Customer fills in `/client/coverage` with their address and the
   service they're after.
2. We persist a `CoverageRequest` (already in DB, exposed via
   `/api/coverage-requests`).
3. Admin sees the request in `/admin/coverage-requests` and:
   - manually checks the Openserve coverage page (or whatever
     internal tool is current),
   - sets the result on the request (Covered / Partial / Not
     Covered + optional note),
   - replies to the customer via the existing notification system
     once a `SupportTicket` or `SendNotification` is wired to that
     flow.
4. If covered, admin tells the customer the package + price; the
   customer places an order which we still install manually (admin
   schedules an `Installation` record).

This keeps the experience honest — customers see "we'll get back to
you" rather than a fake "covered!" answer.

## Possible enhancements before full API access

These are **optional** improvements we can add without depending on
Openserve:

- **Static polygon overlay**: if Openserve gives us a GeoJSON of
  their FTTH zones, render it as a Mapbox / Leaflet overlay so the
  customer can self-confirm before submitting. Refresh quarterly.
- **CSV import**: an admin CSV upload of "ERF → covered yes/no" so
  the coverage check can return an instant answer for known suburbs
  without round-tripping to Openserve.

Both are deferred until the coverage backlog actually warrants them
— right now the manual workflow is simpler than the import pipeline.

## Open items / next steps

- [ ] Email Openserve reseller team for API documentation pack.
- [ ] Confirm whether Smart Future is registered as a fibre reseller
      against Openserve (account number, contact).
- [ ] Get sample API response shapes for the four endpoints above
      to model our DTOs against without committing.
- [ ] Decide caching window for the coverage-availability lookup.
- [ ] Define the SLA we will offer customers on the coverage check
      response (today: "within 1 business day"; with API: instant).

## Related code

- `SmartFuture.Domain/CoverageRequests/CoverageRequest.cs`
- `SmartFuture.Application/CoverageRequests/CoverageRequestService.cs`
- `SmartFuture.API/Controllers/CoverageRequestsController.cs`
- Portal admin: `src/pages/admin/coverage/CoverageRequests.jsx`
- Portal client: `src/pages/client/coverage/ClientCoverageCheck.jsx`

The interim workflow runs entirely through the above; nothing in
that path needs to change until we have answers on the questions
above.
