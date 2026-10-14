# Paid operations: state, reservation, retries (#77, #82)

Every paid call is a durable operation (table `ApiOperations`, migration
`AddApiOperations`): `pending → running → completed | failed | failed_uncertain`.
Endpoints: `sign`, `sign-pfx`, `timestamp`, `extend-signature`, `visual-sign`,
`seal`, `attachment`, `convert/pdfa`, `fill-pdf`, `find-codes`,
`ai/detect-fields`, `ai/extract-data`.

## Contract

| | |
|---|---|
| Request header | `Idempotency-Key: <operationId>` — optional, 1–128 chars `A-Z a-z 0-9 - _ . :`. The portal sends the UUID of its activity row. `/api/sign` is keyed by its presign even without it. |
| Response headers | `X-Operation-Id`, `X-Operation-State`; `Idempotent-Replayed: true` on a replay. Exposed to browsers via CORS. |
| Replay | Same key + same request → the stored response, no work, no charge. Kept 60 min (`Operations:ResultRetention`). |
| `409 OPERATION_IN_PROGRESS` | Another attempt holds the key; `Retry-After: 5`. |
| `422 IDEMPOTENCY_KEY_REUSED` | The key belongs to a different request (endpoint, body or presign). |
| `410 OPERATION_RESULT_EXPIRED` | Completed and charged, but the result is not kept (past retention, over the size limit, storing it failed, or it can no longer be decrypted). **Not a refund**: from `/api/sign` it means "done and paid, result lost" — the credits are not returned. |
| `400 IDEMPOTENCY_KEY_INVALID` | Malformed key. |
| `402` + `code: INSUFFICIENT_CREDITS` | The reservation failed; nothing ran. |
| `GET /api/operations/{operationId}` | Free. `state`, `creditsCharged`, `creditsReserved`, `attempts`, `errorCode`, `resultAvailable`, timestamps, `leaseUntil`. `404 OPERATION_NOT_FOUND` = never arrived (or older than 7 days). |

States as seen by the caller:

- `completed` — work done, result delivered or stored, charged once.
- `failed` — nothing irreversible happened; reservation returned; a retry runs it again.
- `failed_uncertain` — an external step (TSA, model) may have happened, but nothing was
  delivered and the reservation was returned. A retry runs the work again — **the TSA
  may then issue a second token** (open decision, see below).
- `running` — an attempt holds a lease (10 min, `Operations:Lease`), renewed every
  2 min by a heartbeat while the work runs. A lease that runs out (process died) is
  expired by the sweep or the next request: `failed_uncertain`, credits back.

## Credits

Reserved (debited) when the attempt starts, in the same transaction as the claim, so two
requests cannot both pass a balance check and both do the work. Completion records the
charge and the usage record in one transaction; failure returns the reservation. Enterprise
accounts are not decremented, usage is still recorded.

A failure to *store* the result never returns the credits for finished work: the operation
completes without a stored result and the caller still gets it. Only when completion
cannot be recorded at all is the reservation returned — and the result is then not
delivered either (500).

## What is kept

The result is stored (Data Protection, bound to the row and the account) only when somebody
can come back for it — a caller's `Idempotency-Key`, or the presign of `/api/sign` — and only
up to `Operations:MaxStoredResultBytes` (25 MB, env `OPERATION_MAX_STORED_RESULT_BYTES`).
Larger results are delivered but not kept. Without a key nothing is stored. `/api/sign` is
exempt from the size limit (one PDF, and the presign is gone once it completes); its presign
is deleted only after the result is safely stored.

`Operations` options are validated at startup: `HeartbeatInterval` > 0 and at most a third
of `Lease`, `ResultRetention` > 0, `MaxStoredResultBytes` ≥ 0.

## Behaviour changes for existing callers

- **A second `/api/sign` for the same presign** used to get `409 {message}` (presign already
  consumed) or `404`. Now: `200` with the stored result and no new charge once the first
  completed, or `409 OPERATION_IN_PROGRESS` + `Retry-After` while it runs. A failed attempt
  no longer consumes the presign: the retry signs.
- **`402` gained a `code`** (`INSUFFICIENT_CREDITS`); `message` is unchanged in meaning.
- **Order of checks:** the credit check is now the reservation, after input validation. A
  caller without credits and with invalid input gets `400`, not `402`. A replay of a
  completed operation is answered even when the balance has since run out.
- **`499`** when a call without a key is aborted by the caller before or right after the
  work: nothing is charged (no result can be fetched later).
- **TSA timeout** per authority is 10 s (was 30 s), primary then backup, so both fit inside
  the portal's 60 s. It applies to every TSA call: signing, timestamps, `extend-signature`
  (LTV) and the probe. Configurable with env `TSA_TIMEOUT_SECONDS` (`Tsa:TimeoutSeconds`,
  max 120). Qualified/archival authorities that answer slowly may need a higher value —
  the deadline chain portal > dotnet > TSA must still hold.

## Deadlines, cancellation, TSA breaker (#72)

| Operations | Deadline | Env |
|---|---|---|
| sign, sign-pfx, timestamp, visual-sign, seal, attachment | 45 s | `OPERATION_DEADLINE_SIGN_SECONDS` |
| fill-pdf, pdfa | 60 s | `OPERATION_DEADLINE_DOCUMENT_SECONDS` |
| find-codes, extend-signature, ai-detect-fields, ai-extract-data | 75 s | `OPERATION_DEADLINE_HEAVY_SECONDS` |

Valid range 1–95 s (Cloudflare answers 524 after 100 s); anything else fails startup.

**Caution:** the portal waits only 30 s for post-sign (`service.ts`), less than the 45 s sign
deadline, so a slow TSA can make the portal give up before this server answers. The deadlines
here are under Cloudflare, not under the portal; aligning the portal's timeout is tracked on
the portal side (#259). A keyed retry finds the operation either way.

- **Expiry** answers `504 OPERATION_DEADLINE_EXCEEDED` (`operationId`, `state`), cancels the
  work's token and returns the reservation. The state is `failed_uncertain` when a TSA or model
  call had been started, `failed` when nothing external was reached. `errorCode` is `DEADLINE_EXCEEDED`.
- **Cancellation** reaches the TSA request, the CRL/OCSP fetches (`Client.Send` with the token),
  the AI calls, the signature loop of LTV and the row loop of fill-pdf. iText itself cannot be
  interrupted: a call stuck inside it is answered on time and abandoned, and it stops at its next
  outbound call. Light operations (inspect, probe) are not paid operations and have no deadline.
- **Breaker per TSA host** (and a hash of the credentials, so one tenant's wrong password
  does not switch the authority off for the others): `TSA_BREAKER_FAILURE_THRESHOLD`
  (default 5, `0` = off, max 100) consecutive transport failures, timeouts or 5xx within
  2 × the open period (a 4xx, our own cancellation and `/api/tsa-probe` never count) open it for `TSA_BREAKER_OPEN_SECONDS` (default 30, 1–3600);
  calls then fail at once as `503 TSA_UNREACHABLE` with `Retry-After`; a backup authority is
  asked immediately. After that one probe goes through; success closes it, failure re-opens it.
  State is per process. Invalid values fail startup.
- **Abandoned work:** a task still running after its 504 counts against the user
  (`Operations:MaxAbandonedPerUser`, default 2, then `429`) and the process
  (`Operations:MaxAbandonedTotal`, default 16, then `503`) until it ends. Config keys only,
  no env variable. A task that finishes inside the 2 s grace after the deadline is delivered
  and charged normally.
- **TSA errors** carry `code: TSA_UNREACHABLE`, a generic `message` and the caller's `tsaUrl`;
  the exception text goes to the log only (also in `/api/tsa-probe`).

## Open decisions

- Retry after `failed_uncertain` runs the work again (a TSA may issue and bill a second
  token). Alternative: require explicit confirmation. Default kept: retry runs.
- The dotnet 402 code is `INSUFFICIENT_CREDITS`; the portal's tenant quota is
  `QUOTA_EXCEEDED`. Not renamed.

## Páky bez nasazení (#73)

Tabulka `dotnet_signing."OperatorSwitches"`: jeden řádek = jedna zapnutá páka, smazáním řádku se uvolní. Změna platí do 15 s na všech instancích.

| `Name` | Efekt |
|---|---|
| `feature:find-codes` | find-codes vrací 503 `FEATURE_DISABLED` všem |
| `feature:extend-signature` | totéž pro extend-signature |
| `feature:ai` | totéž pro `/api/ai/*` |
| `feature:sign`, `feature:fill`, `feature:pdfa` | podpisové endpointy / fill-pdf / convert pdfa |
| `user:{guid}` | účet dostane na `/api` 403 `ACCOUNT_BLOCKED` |

```sql
insert into dotnet_signing."OperatorSwitches" ("Name", "Reason", "CreatedAt")
values ('feature:find-codes', 'zahlcení 14. 10.', now());
delete from dotnet_signing."OperatorSwitches" where "Name" = 'feature:find-codes';
```

Limity (`RateLimits:*`, env `RateLimits__…`): neautentizované `/api` 30/min na IP (`CF-Connecting-IP`), klíč 600/min, `tsa-probe` 6/min, `inspect-signatures` 30/min, registrace 5/h, přihlášení a reset hesla 20/10 min na IP; blok klíče po `RateLimits:FailuresBeforeBlock` odmítnutých vstupech (400/413) na těžkých cestách za 10 min na 15 min (429 + `Retry-After`) je **ve výchozím stavu vypnutý (0)**: portál volá s jedním účtem za všechny nájemce, takže před zapnutím je třeba jeho účet uvést v `RateLimits:ExemptUserIds`. `CF-Connecting-IP` se věří jen od peeru ze soukromé sítě.

## Role databáze `dotnet_user` (#75)

Dotnet sdílí Supabase projekt s portálem (schéma `dotnet_signing`). Izolace dat je ověřená; chybí provozní limity role. **Provozní krok (ručně, jednou, pod vlastníkem):**

```sql
alter role dotnet_user connection limit 25;
alter role dotnet_user set statement_timeout = '15s';
alter role dotnet_user set lock_timeout = '5s';
alter role dotnet_user set idle_in_transaction_session_timeout = '30s';
alter role dotnet_user set search_path = dotnet_signing;
```

Ověření: `select rolconnlimit, rolconfig from pg_roles where rolname = 'dotnet_user';`

- Connection string runtime služby nese `Maximum Pool Size` (výchozí 20, `DB_MAX_POOL_SIZE`), `Timeout=15` a `Command Timeout` (výchozí 15 s, `DB_COMMAND_TIMEOUT_SECONDS`). Služba `migrate` má vlastní connection string bez těchto limitů – dlouhé migrace nenarazí na `statement_timeout`, pokud migrace běží pod jinou rolí nebo si `SET LOCAL statement_timeout` zvýší.
- Supabase pooler v transakčním režimu (port 6543) vyžaduje navíc `No Reset On Close=true;Max Auto Prepare=0`.
- Delší operace (úklid, migrace dat) si timeout zvýší lokálně: `SET LOCAL statement_timeout = '5min'`.
