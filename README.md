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
| `TimestampAuthority__Url` | | TSA endpoint; also `__Username` / `__Password` |
| `Seal__Enabled` | | Server-side sealing; needs `Seal__PfxBase64` + `Seal__PfxPassword` |
| `Stripe__ApiKey` / `Stripe__WebhookSecret` | for billing | Payments; the webhook secret must start with `whsec_` in production |
| `Resend__ApiKey` / `Resend__From` | for email | Transactional email |
| `OsTicket__Url` / `OsTicket__ApiKey` | | Support form; without them the routes 404 and the nav entry hides |
| `AI__Enabled` / `AI__Google__ApiKey` | | AI-assisted template field detection |
| `Sentry__Dsn` | | Error monitoring; disabled when empty |
| `Loki__Url` | | Log shipping; disabled when empty |
| `Limits__*` | | Request/PDF/image/attachment size caps and per-key concurrency |

Every integration degrades gracefully: leave a section empty and the feature
switches itself off rather than failing at startup.

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
