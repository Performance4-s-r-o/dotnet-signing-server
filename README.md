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

The app listens on `http://localhost:8085` (override with `HTTP_PORT`).

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
| `POST /api/seal` | Server-side seal using the configured PFX |
| `POST /api/timestamp` | Apply an RFC 3161 timestamp |
| `POST /api/tsa-probe` | Check that a TSA is reachable and RFC 3161 compliant |
| `POST /api/attachment` | Embed a file attachment in a PDF |

The two-step `presign` → `sign` flow exists so the private key never leaves the
signer's device: the server returns a digest, the client signs it locally, and
the server injects the resulting PKCS#7 container into the prepared placeholder.

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
| `Token__Secret` | yes | Signing key for issued API tokens |
| `FqdnServerName` | yes | Public hostname, used in generated links |
| `AllowedHosts` | recommended | Host filtering, `*` by default |
| `Cors__AllowedOrigins` | recommended | Comma-separated browser origins |
| `Seal__Enabled` | | Server-side sealing; needs `Seal__PfxBase64` + `Seal__PfxPassword` |
| `Stripe__ApiKey` / `Stripe__WebhookSecret` | for billing | Payments; the webhook secret must start with `whsec_` in production |
| `Resend__ApiKey` / `Resend__From` | for email | Transactional email |
| `OsTicket__Url` / `OsTicket__ApiKey` | | Support form; without them (and with `Modules:Support` not `On`) the routes 404 and the nav entry hides |
| `AI__Enabled` / `AI__Google__ApiKey` | | AI-assisted template field detection |
| `Sentry__Dsn` | | Error monitoring; disabled when empty |
| `Loki__Url` | | Log shipping; disabled when empty |
| `Limits__*` | | Request/PDF/image/attachment size caps and per-key concurrency |
| `P4Backoffice__Mode` / `P4Backoffice__Modules__*` | | P4 Backoffice integration: `Off` (default), `Shadow` or `On`, globally or per module (`Docs`, `Consents`, `Email`, `Pricing`, `Support`) |
| `P4Backoffice__BaseUrl` / `P4Backoffice__SecretKey` | when not `Off` | Service URL (https) and `p4sk_` key; startup fails without them |
| `P4Backoffice__Webhook__Secret` | | Webhook signing secret (`whsec_` + base64 of at least 24 bytes); `…__PreviousSecret` during rotation |
| `P4Backoffice__Polling__Interval` | | How often `/v1/events` is polled (default `00:15:00`, `00:02:00` without a webhook secret) |
| `P4Backoffice__DocumentsTtl` | | How long a legal document from the service is shown before it is revalidated (default `00:05:00`) |
| `P4Backoffice__Consents__Documents__0…` / `…__Acknowledged__0…` | | Documents consented to at sign-up (default `terms`, `dpa`) and documents only acknowledged (default `privacy`) |

Every integration degrades gracefully: leave a section empty and the feature
switches itself off rather than failing at startup.

### P4 Backoffice

The shared backoffice service can take over legal documents, consents, e-mail,
pricing and support one module at a time. Nothing changes until a module is
switched to `Shadow` or `On`; `PrivateServer__Enabled=true` forces every module
`Off`. The integration calls the service's HTTP API directly (its own clients in
`Services/Backoffice/`, written from the service's OpenAPI document); it needs no
SDK or private package, so every build — forks included — can switch it on.
`/Admin` shows the effective mode of each module.

Writes to the service never happen inside a request. They are stored in the
`BackofficeOutboxItems` table in the same `SaveChangesAsync` as the change they
belong to (payload encrypted with the Data Protection key ring) and sent by a
background dispatcher, which runs only while a module is `Shadow` or `On`.
Each attempt sends `Idempotency-Key` = item id; failures are retried from 5 s
up to every 6 h and given up after 72 h. Items refused with 401/403 stay
`Blocked` until the key is fixed and they are requeued from `/Admin`, where the
outbox card also shows counts per status, the oldest pending item and the last
error. Finished items are deleted after 30 days.

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
processor, retried from 30 s up to hourly and given up after 20 attempts
(handlers must keep event data out of exception messages; e-mail addresses are
masked in the stored error). It
answers `404` while every module is `Off` or no secret is set, and does not
exist on a private server.

As a backup — and as the only channel when no secret is set — the same events
are polled from `GET /v1/events` (every 15 min, or 2 min without a secret). The
cursor is kept in `BackofficeStates` (`events:cursor`), so a restart continues
where it stopped; the first run starts one day back. With several instances on
one database only one polls at a time (a PostgreSQL advisory lock per run; the
others skip that run). If the cursor has fallen
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
- `Shadow` — the same pages; in the background the service's version is
  fetched and differences in version, title and effective date are logged.
  Nothing is written.
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
and `_Layout.cshtml` loads no consent widget. Before adding analytics,
marketing or any other optional cookie or script:

1. Add the cookies to the declaration in the service under a new optional
   category (`preferences`, `analytics` or `marketing`) and publish it; update
   `AuditedCookies` and the Razor cookies policy.
2. Add the service's consent widget to `_Layout.cshtml`
   (`<script src="<service>/v1/widget.js" data-pk="p4pk_…">`, publishable key
   only, never the secret key).
3. Load the new script only after the visitor consented to its category, never
   unconditionally.

#### Consents (`Modules:Consents`)

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
- `Shadow` — the form sends the versions it showed (a previous version is
  accepted for 10 minutes after a new one came into force, then the form is
  reloaded with `LegalVersionOutdated`); records plus one `consent` outbox item
  (`POST /v1/consents`, one batch) are written in the same `SaveChangesAsync`
  as the user. The re-consent gate only logs.
- `On` — as `Shadow`, and `RequireCurrentConsentFilter` sends signed-in users
  (cookie only; never `/api/*` or Bearer tokens) whose newest records do not
  reach `required_version` of `docs:meta` to `/Account/Consent`, which returns
  them to the page they asked for. Users registered before consents were
  recorded confirm once (`flow=initial`). A banner announces upcoming versions.

A daily job compares the last 7 days of records with
`GET /v1/subjects/{ref}/consent-status` and only logs differences.

Rollback: `P4Backoffice__Modules__Consents=Off`. Records made while `Off` are
sent later with the admin action *Send consents recorded while the module was
Off* (`/Admin`), which queues every record without an outbox item once.

TODO: there is no account deletion yet. When it is added, queue an outbox item
`erase` (`POST /v1/subjects/{ref}/erase`) in the same transaction.

#### E-mail (`Modules:Email`)

Every message is still rendered locally (`EmailTemplateRenderer`, the user's
language). What happens next depends on the mode:

- `Off` — sent through Resend during the call (`ResendEmailSender`), exactly as
  before, including the error messages when Resend fails.
- `Shadow` — not supported for raw e-mail (the service has no "log only" send,
  so every message would go out twice); treated as `Off` with a startup warning.
  Templates listed in `P4Backoffice__Email__TemplateKeys` are additionally
  rendered by the service in the background (`POST /v1/templates/{key}/render`,
  key scope `templates:read`, skipped without it) and differences in subject and
  text are logged, with variable values masked.
- `On` — queued as outbox item `email.raw` (`POST /v1/emails` with `to`,
  `subject`, `html`, `from` = `EMAIL_FROM`, tags `template`, `locale`,
  `user_id`, `critical`, `category=transactional`). Sign-up, 2FA and password
  reset queue their message in the same `SaveChangesAsync` as the token, so
  they add no latency when the service is down. The code or link is only in the
  encrypted payload, which is cleared once sent.

Service templates: with `On`, a template whose key is listed in
`P4Backoffice__Email__TemplateKeys__0`, `__1`, … (empty by default) is queued as
outbox item `email.template` instead (`POST /v1/emails` with `template`,
`variables`, `locale` = `cs` or `en`, tags) and rendered by the service. All
callers go through `ITemplatedEmailSender`; the variables are defined in
`EmailTemplateVariables` (same names as the local `{{…}}` placeholders). The
list is read per message, so removing a key switches back to the local
rendering without a deployment. A `422 template_variables_invalid` ends the
item `Dead` with an error log, without retries. Keys: `auto_recharge_success`,
`auto_recharge_failed`, `payment_failed`, `price_change_notice`,
`email_verification`, `password_reset`, `two_factor_code` — add them one at a
time, the critical three last. The local templates stay as the fallback.

Break-glass: 2FA codes, password resets and e-mail verification are `critical`.
When the service has not taken one within `P4Backoffice__Email__FallbackAfter`
(default 60 s), when it is known to be down (circuit breaker, or the item's own
attempt got no connection or a 5xx — then within a dispatcher pass), or when
the key is refused (`Blocked`), the dispatcher sends it directly through Resend
(`RESEND_API_KEY` must stay configured), marks it `FallbackSent` and logs a
warning; the service never sends it afterwards. Critical items older than 24 h
are left alone. Critical `email.template` items carry the local rendering in
their encrypted payload (never sent to the service), so break-glass does not
need the service either. Disable with `P4Backoffice__Email__FallbackDirect=false`.

Background e-mails (auto-recharge, failed payment, price change) use the
language of the user's last sign-up or sign-in (`Users.Locale`, default `en`).
`email.bounced` / `email.complained` set `Users.EmailBouncedAt` (matched by the
`user_id` tag, else the address), shown in `/Admin/Users/{id}`; `email.failed`
is logged. The sending domain configured for the environment in the service
must be the domain of `EMAIL_FROM` (DKIM).

Rollback: remove a key from `P4Backoffice__Email__TemplateKeys` (local
rendering again), or `P4Backoffice__Modules__Email=Off` sends through Resend
directly again. E-mail items still pending stay in the outbox untouched while the module
is not `On` (critical ones will already have gone out by break-glass) and are
sent once it is `On` again.

#### Pricing

Credit packs are 100, 300, 500 and 1000 credits. Every price the app shows or
charges (`/pricing`, `/Billing`, Checkout, auto-recharge, the price-change
monitor) comes from `ICreditPricingProvider` (`Services/Pricing/`); nothing on
those paths calls the service.

- `P4Backoffice__Modules__Pricing=Off` (default): `Billing__PricePer100` and the
  `Billing__Discount*` volume discounts, charged inline (`price_data`) as before.
- `Shadow`: the same prices, but the service's price list is fetched in the
  background (at startup, hourly, after `price.effective`) and every difference
  from the configuration is logged as a warning.
- `On`: prices from the snapshot of `GET /v1/pricing/current` (items of kind
  `credits` with `attributes.quantity`, the one-time price in `Stripe__Currency`),
  kept in `BackofficeStates` (`pricing:current`) and in memory, refreshed hourly
  (with `ETag`), right after `price.effective` and on `window_clamped`. Without a
  snapshot, or for a pack missing from it, the configured price is used and a
  warning logged. Checkout charges the pack's Stripe Price, found by its
  `lookup_key` (cached for 10 minutes) and used only when it is active, one-time
  and its amount and currency match the price list; otherwise, and when Stripe
  refuses the session with that Price (e.g. no default tax behaviour on the
  account while automatic tax is on), the amount is sent inline and a warning
  logged. Checkout metadata (`documents`) and the Stripe webhook are unchanged.

Price-change notices (`price_change_notice` to users with auto-recharge):

- `Off` and `Shadow`: `PriceChangeMonitorService` compares the price of 100
  credits with each user's stored price once a day, as before. In `Shadow` the
  `price.*` events are only logged (who would be notified).
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

- `P4Backoffice__Modules__Support=Off` (default) and `Shadow`: the ticket goes to
  osTicket directly (`OsTicket__*`), as before. `Shadow` also fetches the
  service's categories in the background and logs how they differ from the form's.
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

## Stripe webhooks

Endpoint: `POST /api/webhooks/stripe` (`Controllers/StripeWebhookController.cs`).
Subscribe exactly these event types in the Stripe Dashboard — local development
via `stripe listen --forward-to localhost:5000/api/webhooks/stripe` forwards
everything, production must opt in explicitly.

| Event | Purpose |
|---|---|
| `checkout.session.completed` | Safety net for crediting a one-time credit-pack purchase when the user closes the browser before the confirm redirect |
| `payment_intent.succeeded` | Credits off-session auto-recharge purchases (`metadata.type = "auto_recharge"`) |
| `payment_intent.payment_failed` | Emails the user when an off-session auto-recharge is declined |
| `payment_method.detached` | Disables auto-recharge when the user's last saved card is removed |

Every delivery is recorded in the `WebhookEvents` table (schema
`dotnet_signing`) keyed on the Stripe event id, so redeliveries are skipped.
`checkout.session.completed` writes a second idempotency row keyed on the
session id to avoid double-granting against the confirm redirect.

The platform sells usage credits rather than subscriptions, so invoice and
charge events are deliberately ignored. Revisit that if subscriptions are added.

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

Migrations run automatically at startup; a failure aborts the boot so the
orchestrator sees a failed deploy rather than a half-migrated database.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). To report a security issue, follow
[`SECURITY.md`](SECURITY.md) — please do not open a public issue.
