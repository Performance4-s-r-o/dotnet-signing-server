# dotnet-signing-server

A .NET 8 server for digitally signing PDF documents. It performs PAdES/PKCS#7
detached signing, RFC 3161 timestamping, PDF/A conversion, template filling and
barcode detection, and exposes all of it over a token-authenticated HTTP API.

It powers **Performance4PDF**, the SaaS operated by Performance4 s.r.o., and
ships the full product — API, web portal, billing and admin — in this
repository.

## License

This program is licensed under the **GNU Affero General Public License v3.0**
(AGPL-3.0). See [`LICENSE`](LICENSE) for the full text.

The AGPL applies because the PDF engine is [iText 9](https://itextpdf.com),
itself AGPL-3.0. If you deploy this software — modified or not — and let users
interact with it over a network, AGPL §13 requires you to offer those users the
complete corresponding source of your version.

Third-party components are credited under `/Legal/OpenSourceNotices` in the
running app. Bundled fonts are OFL-1.1; see [`Fonts/NOTICE.md`](Fonts/NOTICE.md).

## Quick start

### Docker

```bash
cp .env.example .env      # then fill in the values you need
docker compose up --build
```

`docker-compose.yml` does not publish a port (#78): it only `expose`s 8085 (public API, behind
the tunnel) and, when `INTERNAL_API_PORT` is set, 8086 (the trusted caller) to the Docker network. To reach it from the host locally, add a
`docker-compose.override.yml` with `ports: ["${HTTP_PORT:-8085}:8085"]`.

### Local .NET

```bash
dotnet restore
dotnet run
```

With `UseLocalDb: true` (the default in `appsettings.Development.json`) the app
starts a throwaway PostgreSQL container via Testcontainers, so **Docker must be
running**. Point `ConnectionStrings__DefaultConnection` at a real PostgreSQL
instance and set `UseLocalDb=false` to opt out.

### Tests

```bash
dotnet test tests/DotNetSigningServer.Tests/DotNetSigningServer.Tests.csproj
```

## API

All `/api/*` routes require `Authorization: Bearer <token>`; tokens are issued
from the portal under **API Tokens**. Interactive documentation is served at
`/swagger`.

### Signing

| Endpoint | Purpose |
|---|---|
| `POST /api/presign` | Prepare a PDF for external signing; returns the hash to sign |
| `POST /api/sign` | Complete the flow with an externally produced signature |
| `POST /api/sign-pfx` | Sign with a PKCS#12 certificate in one call |
| `POST /api/visual-sign` | Apply a visible signature appearance |
| `POST /api/seal` | Removed (#122): the server has no certificate of its own, answers `422 SEAL_CERTIFICATE_REQUIRED`. Seal with your own certificate via `presign`/`sign` or `sign-pfx` |
| `POST /api/timestamp` | Apply an RFC 3161 timestamp |
| `POST /api/tsa-probe` | Check that a TSA is reachable and RFC 3161 compliant |
| `POST /api/timestamp-digest` | RFC 3161 token over a SHA-256 digest you supply |
| `POST /api/attachment` | Embed a file attachment in a PDF |

The two-step `presign` → `sign` flow exists so the private key never leaves the
signer's device: the server returns a digest, the client signs it locally, and
the server injects the resulting PKCS#7 container into the prepared placeholder.

### Retries, operation state and credits

Every paid call (`sign`, `sign-pfx`, `timestamp`, `extend-signature`,
`visual-sign`, `attachment`, `convert/pdfa`, `fill-pdf`, `find-codes`,
`ai/detect-fields`, `ai/extract-data`) is a durable operation:

- **Credits are reserved before the work** and returned when it fails. `402`
  (`code: INSUFFICIENT_CREDITS`) means nothing ran.
- **`Idempotency-Key: <id>`** (optional, 1–128 characters `A-Z a-z 0-9 - _ . :`)
  makes a retry safe: the same key with the same request returns the stored
  result (header `Idempotent-Replayed: true`) without working or charging again,
  for 60 minutes (results over 25 MB are delivered but not kept). `/api/sign` is keyed by its presign even without the header,
  and the presign is kept until the signature succeeds, so a failed attempt can
  be retried.
- While an attempt runs, the same key gets `409 OPERATION_IN_PROGRESS` with
  `Retry-After`; a key reused for a different request gets
  `422 IDEMPOTENCY_KEY_REUSED`.
- Responses carry `X-Operation-Id` and `X-Operation-State`
  (`running`, `completed`, `failed`, `failed_uncertain`).
- `GET /api/operations/{id}` reports the state (free): `completed` = done and
  charged, `failed` = nothing irreversible happened, `failed_uncertain` = an
  external step (TSA, model) may have happened, but nothing was delivered or
  charged. `404` = the request never arrived. States are kept for 7 days.
- **Deadlines** (under Cloudflare's 100 s; the portal's post-sign wait is 30 s, see docs/operations.md): 45 s sign/timestamp/…, 60 s
  fill-pdf and PDF/A, 75 s find-codes/extend-signature/AI. Expiry cancels the work
  and answers `504 OPERATION_DEADLINE_EXCEEDED` (`failed`, or `failed_uncertain`
  when a TSA or model call had started).
- **TSA errors** are `TSA_UNREACHABLE` with a generic `message`; a TSA host that
  keeps failing is skipped for a while (`503` + `Retry-After`, circuit breaker
  `TSA_BREAKER_*`). The web reference page "Operation state and safe retries"
  documents this too.

Details, limits and the behaviour changes for existing callers: [docs/operations.md](docs/operations.md).

Internal only (the portal, not the public API): `presign` and `find-codes` also take the
PDF as raw bytes in `multipart/form-data` — see [docs/binary-channel.md](docs/binary-channel.md).

### Browser tokens

A browser token checks the `Origin` header, but that only binds a browser — any other
client can send any `Origin`. Treat a browser token as public. It is therefore limited to
`POST /api/presign`, `POST /api/sign`, `GET /api/operations/{id}` and `GET /api/capabilities`;
anything else answers `403 TOKEN_SCOPE`. A token can also carry a credit limit
(`402 TOKEN_CREDIT_LIMIT` once spent). Server tokens are not affected. A completed password
reset revokes the account's browser tokens (server tokens stay).

### Admin audit

Admin actions on accounts (enterprise mode, seal permission, queue timeout) and on the
installation (outbox requeue, consent backfill) are recorded in `AdminAuditEntries` with the
actor, the target and the change, and shown on the user's admin page. The table is
append-only: the application refuses updates and deletes, and a database trigger refuses them
for any other client. Two limits of that:

- Where one database role both migrates and serves (on-prem with `RUN_MIGRATIONS_ON_START=true`,
  or an empty `MIGRATION_DB_USER`), that role owns the table and can disable the trigger.
  Append-only then holds against the application, not against the database role.
- `ActorEmail` is personal data that the table never deletes. An erasure request from a former
  administrator has to be weighed against the audit trail (and, if granted, done by the table
  owner with the trigger disabled).

### Templates and utilities

| Endpoint | Purpose |
|---|---|
| `GET POST PUT DELETE /api/pdf-template[/{id}]` | Template CRUD |
| `POST /api/fill-pdf` | Render a template with supplied field data |
| `POST /api/convert/pdfa` | Convert a PDF to PDF/A |
| `POST /api/find-codes` | Detect barcodes and QR codes |
| `POST /api/ai/detect-fields` | Suggest template fields from a PDF (optional, needs `AI__Enabled`) |
| `POST /api/ai/extract-data` | Extract structured data from a PDF (optional) |

## Configuration

Configuration binds from `appsettings.json`, then environment variables (`__`
separates nested keys). `appsettings.json` holds defaults and empty
placeholders only — never commit real secrets to it. Local overrides belong in
`appsettings.Development.json` or `.env`, both gitignored.

| Variable | Required | Purpose |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | yes (unless `UseLocalDb`) | PostgreSQL connection string |
| `DB_MAX_POOL_SIZE` / `DB_COMMAND_TIMEOUT_SECONDS` | no (20 / 15) | Npgsql `Maximum Pool Size` and `Command Timeout` appended to the connection string in `docker-compose.yml` (#75) |
| `Token__Secret` | yes | Signing key for issued API tokens |
| `FqdnServerName` | yes | Public hostname, used in generated links |
| `AllowedHosts` | recommended | Host filtering, `*` by default |
| `CORS_ALLOWED_ORIGINS` (`Cors__AllowedOrigins`) | in production, when browser tokens are used | Comma-separated browser origins allowed on `/api`. In Production an empty list means only the server's own origin (it no longer falls back to the origins of every user's browser tokens) and startup logs a warning |
| `AUTH_REAUTH_MINUTES` | no (15) | Step-up re-authentication: creating an API token and changing billing need a password or e-mailed code confirmed within this many minutes; otherwise `403 REAUTH_REQUIRED` and a confirmation form (`/Account/Reauth`) |
| `AUTH_OTP_MAX_FAILED_ATTEMPTS` | no (5) | Wrong 2FA codes after which the code is void. The count is cleared only by a successful code or a password reset, not by signing in again |
| `AUTH_ACCOUNT_MAX_ATTEMPTS` / `AUTH_ACCOUNT_WINDOW_MINUTES` | no (30 / 15) | Sign-in and 2FA attempts per account whatever the IP (on top of the per-IP+address limit), counted in PostgreSQL |
| `MAX_API_TOKENS_PER_USER` | no (100) | Live API tokens per account; 0 = no limit |
| `GLOBAL_CONCURRENCY_LIMIT` / `GLOBAL_CONCURRENCY_WAIT_MS` | no (off / 2000) | Cap on concurrent operations of the whole server (#69), taken after the user's own slot. Empty or `0` = off; a start hypothesis is H + R + A (logged at startup), to be set only once every caller's own limit is below it (otherwise the caller gets `503`). A request waits up to the given milliseconds for a slot of the global, heavy or AI pool, then gets `503` + `Retry-After`. Negative values stop the server at start; this variable wins over `Billing__GlobalConcurrencyLimit`. Note: a flood of find-codes scans holds a slot of G and of the user's limit for up to `RASTER_WAIT_MS` (30 s) per page while it waits for pool R |
| `HEAVY_OPERATIONS_LIMIT` / `AI_OPERATIONS_LIMIT` | no (off / off) | Pools H and A (#69): presign, sign, sign-pfx, visual-sign, timestamp, extend-signature, fill-pdf and PDF/A at once, and AI calls at once. Empty or `0` = off. Start hypotheses: H = the CPU count, A = 4. Full after `GLOBAL_CONCURRENCY_WAIT_MS` = `503` + `Retry-After: 10`. Negative or above 10000 stops the server at start |
| `RASTER_OPERATIONS_LIMIT` / `RASTER_WAIT_MS` | no (CPUs / 2, at least 1 / 30000) | Pool R (#69): PDF pages rasterised at once (find-codes). The limit counts pages, not requests: a slot is taken per page, so one scan of 30 pages needs 30 turns. Unlike H and A it is on by default, because the limit existed before as a fixed one; `0` = off. A page that finds R full waits `RASTER_WAIT_MS`, then the request gets `503` + `Retry-After: 10` and the charge is released. Negative, or a wait above 600000, stops the server at start |
| `USER_QUEUE_MAX_SECONDS` / `USER_QUEUE_LENGTH_FACTOR` | no (30 / 2) | A user's queue (#69): the longest wait whatever the account stores (0–600), and requests that may wait per slot of the user's limit (1–100); the next one gets `429` at once. `Retry-After` after a `429` is the queue wait, at least 5 and at most 60 s. The trusted caller is exempt from both. Out of range stops the server at start |
| `CONTAINER_MEM_LIMIT` / `CONTAINER_CPUS` / `CONTAINER_STOP_GRACE_PERIOD` | no (0 / 0 / 135s) | Read by `docker-compose.yml` itself (#69): `mem_limit`, `cpus` and `stop_grace_period` of the server container. `0` = no limit. Tune to the machine; `cpus` also sets the CPU count the pools are sized from |
| `INTERNAL_API_PORT` | no (empty) | Second listener for a trusted internal caller (#78), added next to `ASPNETCORE_URLS`; `docker-compose.yml` only `expose`s 8086, so with the shipped file use 8086. A token of an account marked *Trusted caller* in the administration is accepted on it only; on the public port it gets `401`. Only that account's server token, on the internal port, is a trusted caller: it alone may skip the attachment debit (`X-P4PDF-Attachment-Billing-Bypass`) and its `X-P4-Tenant` / `X-Request-Id` are logged and stored on presigns, operations and usage records. `/api/sign` (a replay included), any keyed replay and `/api/operations/{id}` refuse another tenant's presign or operation (`403 TENANT_MISMATCH`). No marked account = the start only warns and nobody is trusted. Empty = one listener, a plain API, and the flag has no effect. |
| `PORTAL_BOOTSTRAP_TOKEN` | no (empty = off) | `PortalBootstrap:Token` (#169). The portal's server token, at least 43 characters, no whitespace or quote, not a placeholder or known default, random-looking (otherwise the start is refused; the value is never logged); generate with `openssl rand -base64 32`. At start, after migrations, the server creates a **service account** `portal@service.invalid` (`IsServiceAccount`: no usable password, e-mail unverified, refused by sign-in, password reset, the e-mail code and any cookie session) with *Trusted caller* and the seal grant, and with `PRIVATE_SERVER` also *Enterprise* (no credit check), and registers an API token labelled `bootstrap` holding only the HMAC of the value (`Token__Secret`). Idempotent. A changed value revokes the previous bootstrap token and adds the new one; so does a changed `Token__Secret` (the old hash is dead). A token that already exists on any other account (e.g. a portal account made by hand) is left alone: nothing is created and a line is logged. A value that was revoked is refused at start. Removing the variable leaves the token valid; it stops working by rotating the value or by a change in the database (an admin action to revoke an account's tokens is a topic for #131). A deactivated or erased service account stops the start. Addresses under `.invalid` cannot sign up. SaaS: Coolify shared `DOTNET_PORTAL_BOOTSTRAP_TOKEN`, set on purpose (e.g. to the value of `P4PDF_SIGN_API_TOKEN`). |
| `TRUSTED_CF_PROXIES` | in production (startup fails when empty) | Address / CIDR of cloudflared (#106). `CF-Connecting-IP` is the client (rate limits, token IP whitelist, consent evidence) only for a request that came from cloudflared — it is the TCP peer, or a `TRUSTED_PROXIES` proxy is the peer and cloudflared the forwarded address. Empty = never believed; `none` = no tunnel in front, on purpose |
| `TRUSTED_PROXIES` | no | Reverse proxy (Traefik/Caddy) addresses / CIDRs (#106): whose `X-Forwarded-For` is applied. An explicit `ForwardedHeaders__KnownProxies` / `__KnownNetworks` wins; empty = the private ranges and loopback (as before); `none` = nobody's. A reverse proxy alone never vouches for `CF-Connecting-IP` |
| `DataProtection__CertificateBase64` / `__CertificatePassword` (compose: `DATA_PROTECTION_CERTIFICATE_BASE64` / `_PASSWORD`) | in production, unless the next row is `true` | Base64 PFX that encrypts the Data Protection key ring, which also protects stored TSA credentials |
| `DATA_PROTECTION_ALLOW_PLAINTEXT_KEYS` | no | `true` = a Production start without the certificate is allowed, with a warning (single host, protected volume). Otherwise Production refuses to start with an unencrypted key ring; with `true` it starts and prints a warning. Outside Production nothing is checked or printed. Back up the certificate: adding it does not encrypt keys already written in plaintext (they are replaced when the ring rotates, about every 90 days), and a lost certificate makes the ring unreadable |
| `P4Backoffice__Modules__Billing` | in production (startup fails otherwise) | `On`: payments go through the billing service; this product holds no payment provider key. See *Payments* below |
| `Billing__Currency` (compose: `BILLING_CURRENCY`) | no (`EUR`; empty or an unresolved placeholder = `EUR`) | Three-letter ISO 4217 code (anything else stops a production start). Currency the product sells in: selects the price list's prices and is the currency of the configured fallback prices |
| `P4Backoffice__BaseUrl` + `P4Backoffice__SecretKey` | in production (startup fails otherwise) | All e-mail (2FA, password reset, notifications) goes through the backoffice outbox; there is no other provider. Without them outside production messages are only logged |
| `P4Backoffice__Email__From` / `__ReplyTo` (compose: `P4_BACKOFFICE_EMAIL_FROM` / `_REPLY_TO`) | | Sender and Reply-To of every message; the sender's domain must be the one configured for the environment in the service. Empty = the service's default |
| `OsTicket__Url` / `OsTicket__ApiKey` | | Support form; without them (and with `Modules:Support` not `On`) the routes 404 and the nav entry hides |
| `AI__Enabled` / `AI__Google__ApiKey` | | AI-assisted template field detection; `AI__Enabled` is on only when exactly `true`, otherwise every `/api/ai/*` route answers 403 `AI_DISABLED` |
| `Sentry__Dsn` | | Error monitoring; disabled when empty |
| `Loki__Url` | | Log shipping; disabled when empty |
| `Limits__*` | | Request/PDF/image/attachment size caps |
| `P4Backoffice__Mode` / `P4Backoffice__Modules__*` | | P4 Backoffice integration: `Off` (default) or `On`, globally or per module (`Docs`, `Consents`, `Pricing`, `Support`, `Billing`; `Billing` never follows the global mode) |
| `P4Backoffice__BaseUrl` / `P4Backoffice__SecretKey` | when not `Off` | Service URL (https) and `p4sk_` key; startup fails without them |
| `P4Backoffice__Webhook__Secret` | | Webhook signing secret (`whsec_` + base64 of at least 24 bytes); `…__PreviousSecret` during rotation |
| `P4Backoffice__CookieWidget__PublishableKey` / `…__Url` | | Cookie banner of the service: publishable key `p4pk_…` (empty = no banner) and the origin of `widget.js` (default `https://legal.performance4.cz`) |
| `P4Backoffice__Email__Templates` / `P4_BACKOFFICE_TEMPLATES` | | Templates the service renders: `*` for all, otherwise keys separated by commas or whitespace (empty = all rendered locally) |
| `P4Backoffice__Polling__Interval` | | How often `/v1/events` is polled (default `00:15:00`, `00:02:00` without a webhook secret) |
| `P4Backoffice__DocumentsTtl` | | How long a legal document from the service is shown before it is revalidated (default `00:05:00`) |
| `P4Backoffice__Consents__Documents__0…` / `…__Acknowledged__0…` | | Documents consented to at sign-up (default `terms`, `dpa`) and documents only acknowledged (default `privacy`) |

The `#80` variables (`CORS_ALLOWED_ORIGINS`, `AUTH_*`, `MAX_API_TOKENS_PER_USER`,
`GLOBAL_CONCURRENCY_*`, `HEAVY_OPERATIONS_LIMIT`, `AI_OPERATIONS_LIMIT`, `RASTER_*`, `USER_QUEUE_*`) and `TRUSTED_CF_PROXIES` / `TRUSTED_PROXIES` fall back to Coolify project variables (`DOTNET_*`) in
`docker-compose.yml`. An empty value or an unresolved `{{project.…}}` placeholder means the
default; any other value that does not parse stops the server at startup.

`GET /internal/concurrency` shows what the limits are doing (#69): occupancy, waits and
rejections of the global, heavy and AI pools, the users' queues together (no user ids), the
rasterisation pool, process memory and the thread pool, plus the start hypotheses for this
machine. The pools show p95/p99 of the wait for a slot, and `operations` shows p50/p95/p99 and
the outcome counts (completed, failed, deadline) of each paid operation, from the last 512 calls
of each since start. Counts only, and no token of its own: it answers on the `INTERNAL_API_PORT` listener
and is `404` everywhere else (and everywhere while that port is not set).

Pool H counts the request, not the work: when a call's deadline (#72) answers the client, the
slot goes back while iText may still finish the abandoned operation in the background, so CPU
in use can briefly exceed H.

Every integration degrades gracefully: leave a section empty and the feature
switches itself off rather than failing at startup.

### P4 Backoffice

The shared backoffice service can take over legal documents, consents, e-mail,
pricing and support one module at a time. Nothing changes until a module is
switched to `On`; `PrivateServer__Enabled=true` forces every module
`Off`. The integration calls the service's HTTP API directly (its own clients in
`Services/Backoffice/`, written from the service's OpenAPI document); it needs no
SDK or private package, so every build — forks included — can switch it on.
`/Admin` shows the effective mode of each module.

Writes to the service never happen inside a request. They are stored in the
`BackofficeOutboxItems` table in the same `SaveChangesAsync` as the change they
belong to (payload encrypted with the Data Protection key ring) and sent by a
background dispatcher, which runs only while a module is `On`.
Each attempt sends `Idempotency-Key` = item id; failures are retried from 5 s
up to every 6 h and given up 20 h after the first attempt
(`P4Backoffice__Outbox__GiveUpAfter`, must stay under 24 h: the service keeps
an idempotency key 24 h, and a later retry would create the e-mail, ticket or
consent batch twice). Items refused with 401/403 stay
`Blocked` until the key is fixed and they are requeued from `/Admin`, where the
outbox card also shows counts per status, the oldest pending item and the last
error. Finished items are deleted after 30 days.

E-mails with secrets (2FA code, password reset, verification link): the service has no
"sensitive" marker and keeps the body of a sent message in its log for its own retention (30 days
at the time of writing). The outbox payload here is deleted once the item is sent, and for a critical or e-mail item that ended Dead or Blocked it is dropped after 24 h (the code has expired by then). While the service is down a 2FA or reset e-mail waits in the outbox and goes out once it is back (see *E-mail*). What this
product can do today is `critical` (so the message is not held back) and, with
`P4Backoffice__Email__Templates`, sending a template key plus variables instead of raw HTML;
the code or link is then still a variable stored by the service. What stays open is a retention
or redaction switch on the service's side for these messages. A dead inbox event is logged as an
error starting with `Backoffice inbox alert:` (a fixed marker, also repeated daily); with
`Sentry__Dsn` set, Sentry captures it, and there is no other alert channel in this server.

A consent form for a document with no known version answers `503` and records nothing, instead
of recording a made-up version. A document with a static page is version 1, the page the reader
sees, unless `docs:meta` says a newer version is in force (cold cache): then it is refused too.
`docs:meta` counts only with `Modules:Docs=On`, both for the version a form records and for the version the re-consent gate requires (`ConsentDocsMeta`), so a Docs rollback cannot start a re-consent loop. With Docs not On, startup is refused when a
`Consents:Documents` / `Acknowledged` key has no page in this product.

#### Backoffice webhooks

Events from the service (`document.*`, `cookie_declaration.published`,
`price.*`, `email.*`, `support.ticket_failed`) arrive at `POST /api/webhooks/p4`
(`Controllers/BackofficeWebhookController.cs`), signed per
[Standard Webhooks](https://www.standardwebhooks.com). Register the endpoint in
the service admin as `https://<FqdnServerName>/api/webhooks/p4` with the event
types listed in `Services/Backoffice/Inbox/BackofficeEventTypes.cs`, put its
signing secret in `P4Backoffice__Webhook__Secret` and check it with the admin's
"Test" button (a `webhook.test` event is logged and marked processed).

The endpoint only verifies the signature (5-minute timestamp tolerance), stores
the event in `BackofficeWebhookInboxItems` (one row per `webhook-id`,
redeliveries are ignored) and answers `200`; handlers run in a background
processor, retried from 30 s up to hourly. After 20 failed attempts an event
is dead (`DeadAt`): it is logged as an error starting with `Backoffice inbox
alert:`, kept by the cleanup, counted on `/Admin` and put back in the queue
with "Replay dead events" there (an unprocessed `billing.checkout.completed`
means credits not granted — replaying grants them once). Several instances
claim due events with `FOR UPDATE SKIP LOCKED`, so each runs once (handlers
must keep event data out of exception messages; e-mail addresses are
masked in the stored error). It
answers `404` while every module is `Off` or no secret is set, and does not
exist on a private server.

As a backup — and as the only channel when no secret is set — the same events
are polled from `GET /v1/events` (every 15 min, or 2 min without a secret). The
cursor is kept in `BackofficeStates` (`events:cursor`), so a restart continues
where it stopped; the first run starts one day back. With several instances on
one database only one polls at a time: a lease row in `BackofficeStates`
(`lease:events-poll`), taken and renewed with single statements, so it works
through a transaction pooler (Supabase `:6543`) where a session advisory lock
does not; the others skip that run, and a crashed holder's lease expires after
2 minutes. The daily consent reconciliation uses the same kind of lease. If the cursor has fallen
out of the service's 30-day window, the modules resynchronise from the source
APIs. Processed events are deleted after 45 days.

Rotating the secret: create the new secret in the service admin, move the old
value to `P4Backoffice__Webhook__PreviousSecret` and set the new one in
`P4Backoffice__Webhook__Secret`, redeploy, and clear `PreviousSecret` after
24 hours. Rollback: disable the endpoint in the service admin, or set the
modules `Off` (processing and polling stop; the tables stay).

#### Legal documents (`Modules:Docs`)

The `/Legal/*` pages (`Controllers/LegalController.cs`) read their text through
`ILegalDocumentSource` (`Services/Legal/`):

- `Off` — the hand-maintained `LegalDocuments` rows (`Source = manual`,
  Markdown), then the static Razor views. Exactly the behaviour before the
  integration.
- `On` — the service's sanitized HTML (all eight documents, `oss` and
  `license` included; slug ↔ type map in `LegalSlugMap`). A page reads the
  in-memory copy and never waits for the service: a stale copy is shown while
  it is revalidated (`If-None-Match`) in the background, and without one the
  page reads the snapshot in `LegalDocuments` (`Source = backoffice`, or the
  service HTML stored next to a manual row of the same version), then English,
  then Razor. After a failed call the service is left alone for 30 s.
  `document.published` / `document.minor_corrected` refetch the text right
  away; `document.scheduled` / `unscheduled` update `docs:meta` in
  `BackofficeStates` (`requires_consent`, `current_version`,
  `required_version`, `upcoming`). A startup warm-up and the `window_clamped`
  resync rebuild both.

Rollback: `P4Backoffice__Modules__Docs=Off`. Snapshot rows are ignored in
`Off` and can stay.

The cookies policy (`/Legal/CookiesPolicy`) lists the cookies in a table under
the text. With `Docs=On` the table comes from the service's cookie declaration
(`GET /v1/cookie-declaration?locale=`, `Services/Legal/CookieDeclarationReader.cs`):
read from memory or the `cookies:{locale}` snapshot in `BackofficeStates`,
revalidated in the background, and refetched on `cookie_declaration.published`
(webhook or polling), at startup and on the `window_clamped` resync. Otherwise
— and whenever no declaration is available — the table shows the audited
cookies in `Services/Legal/AuditedCookies.cs`; the Razor fallback page shows
the same list.

#### Cookie banner

This product sets only strictly necessary cookies (`.AspNetCore.Cookies`,
`.AspNetCore.Antiforgery.*`, `.AspNetCore.Mvc.CookieTempDataProvider`,
`.AspNetCore.Culture`), which need no consent, so there is **no cookie banner**
and `_Layout.cshtml` loads no consent widget while `P4_BACKOFFICE_PK` is empty. Before adding analytics,
marketing or any other optional cookie or script:

1. Add the cookies to the declaration in the service under a new optional
   category (`preferences`, `analytics` or `marketing`) and publish it; update
   `AuditedCookies` and the Razor cookies policy.
2. Set `P4_BACKOFFICE_PK` (publishable key `p4pk_…`, never the secret key).
   `_Layout.cshtml` then loads the service's widget from the public legal viewer
   (`P4_BACKOFFICE_WIDGET_URL`, default `https://legal.performance4.cz`), which
   shows the banner and passes its API calls to the service.
3. Write the new script as `<script type="text/plain" data-p4-category="analytics" …>`:
   the widget runs it only after the visitor consented to that category.

#### Public support form

The contact page frames the support form hosted by the legal viewer
(`P4_BACKOFFICE_SUPPORT_FORM_PRODUCT` = the product slug in the service,
`P4_BACKOFFICE_SUPPORT_FORM_URL` to point elsewhere than
`https://legal.performance4.cz`). The form belongs to the service: the captcha,
the attachment rules, the rate limits and the helpdesk connection live there,
and a browser cannot reach the service directly.

Two things have to be set on the other side: the product's **embed origins**
must list this app's origin, or the browser refuses to frame the form, and the
helpdesk connection must be configured for the product's live environment.
Without `…_PRODUCT` the contact page is an address and a mail link, as before.

Signed-in users keep the in-app form at `/support`, which posts through the
product API with the secret key.

#### Consents (`Modules:Consents`)

The sentence next to the sign-up checkbox comes from the service when it
publishes one for the `registration` context, with its version and hash
recorded alongside the consent; `P4Backoffice__ConsentPromptsTtl` sets how long
a copy is shown without revalidation (five minutes by default). The form never
waits for the service — it renders what is already in memory and falls back to
this app's own wording until there is a copy.

Sign-up has one unchecked checkbox for the documents in
`P4Backoffice__Consents__Documents` (default `terms`, `dpa`: recorded as
`granted`) and an information sentence for `P4Backoffice__Consents__Acknowledged`
(default `privacy`: `acknowledged`). The checkbox is validated on the server.
Every sign-up stores one `ConsentRecords` row per document (version, language
and content hash of the text shown, from local data only: `docs:meta` and
`LegalDocuments`). The table is append-only; on PostgreSQL a trigger refuses
`UPDATE`, `DELETE` and `TRUNCATE`. Subjects are sent as `dotnet:user:{id}`,
never as an e-mail address.

- `Off` — records are stored locally only; no outbox item, no gate.
- `On` — the form sends the versions it showed (a previous version is
  accepted for 10 minutes after a new one came into force, then the form is
  reloaded with `LegalVersionOutdated`); records plus one `consent` outbox item
  (`POST /v1/consents`, one batch) are written in the same `SaveChangesAsync`
  as the user, and `RequireCurrentConsentFilter` sends signed-in users
  (cookie only; never `/api/*` or Bearer tokens) whose newest records do not
  reach `required_version` of `docs:meta` to `/Account/Consent`, which returns
  them to the page they asked for. Users registered before consents were
  recorded confirm once (`flow=initial`). A banner announces upcoming versions.

A daily job compares the last 7 days of records with
`GET /v1/subjects/{ref}/consent-status` and only logs differences.

Rollback: `P4Backoffice__Modules__Consents=Off`. Records made while `Off` are
sent later with the admin action *Send consents recorded while the module was
Off* (`/Admin`), which queues every record without an outbox item once.

Account deletion: a signed-in user deletes the account at `/Account/Delete` (recent
re-authentication and an explicit confirmation). `AccountErasureService` erases or
pseudonymises the personal data in one database transaction and queues outbox item
`subject.erase` (`POST /v1/erasures`) in it. An erasure requested by the service
(`subject.erasure.requested` webhook / event) runs the same erasure and queues
`subject.erasure.ack`. Both kinds are retried for 30 days, alert from 72 h on, and are never
dropped quietly (Dead, kept). Invoices, audit actions and pseudonymised consent records are
retained and acknowledged as `retained`.

#### E-mail

There is one path and no switch: every message is rendered locally
(`EmailTemplateRenderer`, the user's language) and queued as outbox item
`email.raw` (`POST /v1/emails` with `to`, `subject`, `html`, `from` =
`P4Backoffice__Email__From`, tags `template`, `locale`, `user_id`, `critical`,
`category=transactional`). The product has no mail provider of its own; the
service is the SaaS backoffice, an on-prem installation runs `notify-gateway`
with the same `/v1` API. Sign-up, 2FA and password reset queue their message
in the same `SaveChangesAsync` as the token, so they add no latency when the
service is down. The code or link is only in the encrypted payload, which is
cleared once sent.

E-mail is on when both `P4Backoffice__BaseUrl` and `SecretKey` are set (one alone
is not enough). Production refuses to start without them, and `From` / `ReplyTo`,
when set, must be valid addresses. An unresolved Coolify placeholder counts as unset.

Without a connection (Development, Staging, a private server without a gateway)
`LoggingEmailSender` delivers nothing: in Development it logs the subject and a
masked recipient (the body only with `P4Backoffice__Email__LogBodies=true`, at
Debug); anywhere else it throws, so a flow that waits for a mail (2FA, reset)
reports a failure instead of "code sent". `email.*` outbox items already queued
stay Pending until a connection exists.

On-prem: a private server (`PrivateServer__Enabled=true`) with
`P4Backoffice__OnPremGateway=true` (exactly `true`) sends e-mail, and only e-mail,
through the installation's `notify-gateway`: `BaseUrl` is the gateway
(`http://notify-gateway:8090` is accepted), `SecretKey` its key. No inbox, polling,
webhook, erasure queue or service templates there (`Email:TemplateKeys` must be
empty). Everything else stays off.

With e-mail as the only module (Development, Staging), a `subject.erasure.requested` event is
marked processed without a handler. Production never is in that state, it requires
Billing On.

Events: bounces and complaints arrive as events, so a connected hosted installation
runs the inbox, the poll and the webhook whenever any module, e-mail included, is
on. The erasure queue to the service only runs with a module other than e-mail.

Service templates: a template whose key is selected — in
`P4Backoffice__Email__TemplateKeys__0`, `__1`, … or, as a single value,
`P4Backoffice__Email__Templates` / `P4_BACKOFFICE_TEMPLATES` (`*` for every
template - not checked at start: a key the service does not know ends `Dead` and alerts; otherwise keys separated by commas or whitespace; empty by default) —
is queued as outbox item `email.template` instead (`POST /v1/emails` with `template`,
`variables`, `locale` = `cs` or `en`, tags) and rendered by the service. All
callers go through `ITemplatedEmailSender`; the variables are defined in
`EmailTemplateVariables` (same names as the local `{{…}}` placeholders). The
selection is read per message, so removing a key switches back to the local
rendering without a deployment. A `422 template_variables_invalid` ends the
item `Dead` with an error log, without retries. Keys: `auto_recharge_success`,
`auto_recharge_failed`, `payment_failed`, `price_change_notice`,
`email_verification`, `password_reset`, `two_factor_code` — add them one at a
time, the critical three last. The local templates stay as the fallback. With
`*` a key the service does not know is sent to it anyway and refused; the
message then ends `Dead` with an error log, so a new local template is worth
publishing in the service first.

Outage: there is no direct fallback. While the service is unreachable, answers 5xx
or refuses the key, the item stays in the outbox and is retried with backoff until
`P4Backoffice__Outbox__GiveUpAfter` (default 20 h, always under 24 h because of the
service's `Idempotency-Key`), then it is `Dead`. Critical e-mail ends sooner, when its
content expires, counted from the moment it was queued (not from the first attempt)
and checked before every attempt, a requeue of a Blocked item included: 2FA code 10
minutes, password reset 1 hour, e-mail verification 20 hours (`CriticalEmails.ValidFor`).
It is then `Dead` with "Not confirmed before expiry", its payload is dropped and it is
never sent late. A critical item that stays Pending without any attempt (the kind is
paused for lack of a connection) loses its payload after 24 h. A user waiting for a 2FA
code or a reset link does not get it until the service is back. An e-mail not accepted
five minutes after queueing raises an ops alert at its next failed attempt (about 12
minutes in with the retry schedule); `outbox-overdue:email.*`, and `outbox-dead:email.*`
when it ends dead (see
`OpsAlert__WebhookUrl`). Outbox items with the old status `FallbackSent` no longer
exist in code; no installation ever held one, a database that did needs them set to
`Sent` by hand.

Background e-mails (auto-recharge, failed payment, price change) use the
language of the user's last sign-up or sign-in (`Users.Locale`, default `en`).
`email.bounced` / `email.complained` set `Users.EmailBouncedAt` (matched by the
`user_id` tag, else the address), shown in `/Admin/Users/{id}`; `email.failed`
is logged. The sending domain configured for the environment in the service
must be the domain of `P4Backoffice__Email__From` (DKIM).

Rollback: remove a key from `P4Backoffice__Email__TemplateKeys` (local rendering
again). Queued items are never lost by that.

#### Pricing

Credit packs are 100, 300, 500 and 1000 credits. Every price the app shows or
charges (`/pricing`, `/Billing`, Checkout, auto-recharge, the price-change
monitor) comes from `ICreditPricingProvider` (`Services/Pricing/`); nothing on
those paths calls the service.

- `P4Backoffice__Modules__Pricing=Off` (default): `Billing__PricePer100` and the
  `Billing__Discount*` volume discounts, in `Billing__Currency`. They are what a private
  server shows; a payment is taken only with `Modules:Billing=On`, which needs this module `On`.
- `On`: prices from the snapshot of `GET /v1/pricing/current` (items of kind
  `credits` with `attributes.quantity`, the one-time price in `Billing__Currency`),
  kept in `BackofficeStates` (`pricing:current`) and in memory, refreshed hourly
  (with `ETag`), right after `price.effective` and on `window_clamped`. Without a
  snapshot, or for a pack missing from it, the configured price is used and a
  warning logged. The billing service charges the pack's Stripe Price by its
  `lookup_key` (see *Payments* below); checkout metadata (`documents`) is unchanged.

Price-change notices (`price_change_notice` to users with auto-recharge):

- `Off`: `PriceChangeMonitorService` compares the price of 100
  credits with each user's stored price once a day, as before.
- `On`: the monitor does not run. `price.scheduled` notifies every non-Enterprise
  user with auto-recharge on a pack whose price changes, once per price-list
  version (`Users.PriceChangeNotifiedVersion`, stored with the queued e-mail);
  `price.effective` stores the new price of 100 credits in
  `AutoRechargePricePer100` and clears the notice markers; `price.unscheduled`
  clears the marker of that version (no e-mail); `price.sync_failed` is logged as
  an error. A daily check of `GET /v1/pricing/upcoming` sends the notices itself
  (with a warning) when a version takes effect in less than its notice period and
  nobody has been notified yet.

Rollback: `P4Backoffice__Modules__Pricing=Off`. Keep `Billing__*` in line with
the price list while the module is `On`: it is the fallback, and after a rollback
the monitor compares against `Billing__PricePer100` again
(`PriceChangeNotifiedVersion` is then ignored).

#### Support form (`Modules:Support`)

The in-app form at `/support` (signed-in users). The ticket always carries the
user's e-mail address, looked up by account id.

- `P4Backoffice__Modules__Support=Off` (default): the ticket goes to
  osTicket directly (`OsTicket__*`), as before.
- `On`: the ticket is queued in the outbox (`support.ticket`, in the same
  transaction) and attempted right away for at most 5 seconds. The user sees the
  ticket number when the service answered in time, otherwise "received, the
  number comes by e-mail" — never an error because the service is down; the
  outbox delivers it later with the item id as `Idempotency-Key`, and a repeated
  submission of the same form is not queued twice. The message is sent as plain
  text (the service escapes it). Categories come from
  `GET /v1/support/categories` per site language, refreshed hourly in the
  background and kept in `BackofficeStates` (`support:categories:{locale}`); the
  form's own list is used until then. `support.ticket_failed` is logged as an
  error. The form is shown with `On` even without `OsTicket__*`.

Rollback: `P4Backoffice__Modules__Support=Off` (keep `OsTicket__*` until then).
Tickets already queued are still delivered.

#### Payments (`Modules:Billing`)

Only its own setting switches it: `P4Backoffice__Mode=On` leaves payments Off.

- `P4Backoffice__Modules__Billing=On` is required in production (a hosted
  installation without it refuses to start; a private server has no billing
  pages and is exempt). It needs `Modules:Pricing=On`, refused at startup
  otherwise. Without it (development) every action that would start a payment
  fails with an error and nothing is ever charged: the product holds no payment
  provider key and exposes no provider webhook.
- Checkout, card setup, the confirm pages, invoices, the card on file, the portal
  and the enterprise card removal go through `/v1/billing/*`; the customer is
  `user:<id>`. Checkout charges the pack's lookup key from the price list in
  force (no inline prices; tax and address collection are the billing service's
  settings). Auto-recharge uses `POST /v1/billing/charges` with
  an `Idempotency-Key` kept in `WebhookEvents` (`auto_recharge_claim_<user>`),
  so replicas never charge twice and a charge without an answer is repeated
  with the same key — never after 23 hours (the service keeps answers for 24):
  such a charge stops auto-recharge and is logged as an error for an admin. Credits are granted by the confirm page or by
  `billing.checkout.completed` / `billing.payment.succeeded`, whichever comes
  first, exactly once per payment: a `CreditGrants` row — unique key = checkout
  session id (or `auto_recharge_<payment intent>`), unique payment intent — is
  inserted with `ON CONFLICT DO NOTHING` in the same transaction as the credits
  and never deleted, so a late, repeated or concurrent delivery grants nothing
  more and a failed one leaves nothing behind for the retry to trip on.
  `billing.payment_method.detached` switches auto-recharge off when no card is
  left. Success and failure e-mails of auto-recharge are sent by this app.
  Existing Stripe customers must be linked in the service first;
  otherwise the service creates a new customer without the saved card.

## Project layout

| Path | Contents |
|---|---|
| `Controllers/` | API endpoints and portal MVC controllers |
| `Services/` | Signing engine, billing, auth, email, template rendering |
| `Models/` | Request/response DTOs and EF entities |
| `Data/` | EF Core `DbContext` and design-time factory |
| `Migrations/` | EF Core migrations (schema `dotnet_signing`) |
| `Views/` | Razor views for the portal |
| `Resources/` | Localised strings (EN, CS, DE, ES) and email templates |
| `Fonts/` | Bundled OFL-1.1 fonts used for stamping |
| `examples/react-example/` | Minimal React client for the presign/sign flow |
| `tests/` | xUnit test suite |

### Database migrations

```bash
dotnet ef migrations add <Name> -p dotnet-signing-server.csproj
dotnet ef database update
```

Migrations are a release step: `dotnet dotnet-signing-server.dll --migrate`
(the `migrate` service in `docker-compose.yml`) applies them once before the
new version starts. The server refuses to start while any are pending, unless
`RUN_MIGRATIONS_ON_START=true` (single instance only).

The history starts with one baseline, `InitialSchema` (2026-10-28), which
replaced the 24 migrations written before the first release. It is not an
upgrade path for a database built from that earlier history: such a database
is refused by both `--migrate` and start, with a message naming the baseline.
Drop schema `dotnet_signing` and run `--migrate` against the empty database.
If a database administrator created the schema in the first place, re-create
it after the drop with the same owner
(`CREATE SCHEMA dotnet_signing AUTHORIZATION <migration role>`) and restore
the default privileges for the runtime role before running `--migrate`.
The baseline holds hand-written SQL the model does not describe (the
`lower("Email")` unique index and the append-only triggers on
`ConsentRecords` and `AdminAuditEntries`); keep that in mind before ever
regenerating it.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). To report a security issue, follow
[`SECURITY.md`](SECURITY.md) — please do not open a public issue.
