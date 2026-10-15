# Interní binární kanál portál → dotnet (#74)

> **Interní.** Kanál je pro portál (v0) volající dotnet ze serveru. Veřejné API pro SPFx
> a Power Automate konektor zůstává beze změny — JSON s PDF v base64. Proto kanál není
> v OpenAPI (`[ApiExplorerSettings(IgnoreApi = true)]`) ani v `ApiDocsCatalog` a změnit
> se může bez ohlášení. Rozhodnutí vlastníka 15. 10. 2026 (komentář v #74).

## Proč

PDF v base64 uvnitř JSONu stojí na serveru několikanásobek velikosti souboru: JSON tělo
(1,33 × N), řetězec v UTF-16 (2,67 × N), dekódované bajty (N) — a dokud kód dekódoval
v každém kroku znovu, ještě jednou. Binární kanál čte dokument rovnou do pole bajtů.

Měření (`LoadProfileTests.BinaryChannelProfile`, PDF 18 MB, viz PR):

| | JSON base64 | multipart |
|---|---|---|
| jen čtení vstupu, alokováno | 131 MB | 36 MB |
| celý presign, alokováno | 511 MB (main) / 492 MB (#74) | 398 MB |

Zbytek presignu je iText (~360 MB alokací na 18 MB PDF) — to je další páka, ne tato.

## Požadavek

Stejná URL, stejný `Authorization: Bearer`, stejné hlavičky (`Idempotency-Key`, `Origin`).
O kanálu rozhoduje jen `Content-Type`:

```
POST /api/presign
Authorization: Bearer <token>
Content-Type: multipart/form-data; boundary=…

--…
Content-Disposition: form-data; name="metadata"
Content-Type: application/json

{"certificatePem":"…","signRect":{"x":50,"y":50,"width":200,"height":60}, …}
--…
Content-Disposition: form-data; name="pdf"; filename="document.pdf"
Content-Type: application/pdf

%PDF-1.7 …bajty…
--…--
```

- **`metadata`**: JSON přesně tvaru dnešního těla endpointu, **bez `pdfContent`**. Ostatní
  base64 pole (obrázek podpisu, razítko…) zůstávají v metadatech, jak jsou.
- **`pdf`**: bajty dokumentu, `Content-Type: application/pdf` (snese i
  `application/octet-stream` nebo žádný).
- Právě tyto dvě části, každá jednou, v libovolném pořadí. Cokoli jiného — chybějící nebo
  zdvojená část, neznámá část, metadata, která nejsou JSON, `pdfContent` v metadatech, část
  `pdf` jiného typu — je `400` s `code: MULTIPART_INVALID`.

V Node (undici, v portálu):

```ts
const form = new FormData();
form.append("metadata", new Blob([JSON.stringify(meta)], { type: "application/json" }));
form.append("pdf", new Blob([pdfBytes], { type: "application/pdf" }), "document.pdf");
await fetch(`${SIGN_API_BASE}presign`, { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: form });
```

## Odpověď

Odpověď se kanálem požadavku neřídí. PDF jako binární tělo vrací endpoint, kterému klient
pošle `Accept: application/pdf` (`Content-Type: application/pdf`, ostatní údaje v hlavičkách
— `X-Operation-Id`, `X-Operation-State`, `Idempotent-Replayed`, u PDF/A `X-PDF-Conformance`).
Bez té hlavičky je odpověď JSON jako dosud. Platí i pro opakování z uloženého výsledku.

## Endpointy

| endpoint | PDF dovnitř binárně | PDF ven binárně |
|---|---|---|
| `POST /api/presign` | ✅ multipart | — (odpověď je `{ id, hashToSign }`) |
| `POST /api/sign` | — (nebere PDF) | ✅ `Accept: application/pdf` |
| `POST /api/find-codes` | ✅ multipart | — (odpověď jsou nalezené kódy) |

Ostatní endpointy berou zatím jen JSON (multipart na nich skončí `415`). `Accept:
application/pdf` umí všechny, které PDF vracejí, už dnes.

## Co platí v obou kanálech stejně

Oba kanály končí ve stejném kódu (`PreSignCoreAsync`, `FindCodesCoreAsync`) a projdou:

- ověřením tokenu a kontrolou kreditů (401/402) — binární tělo se navíc čte až **po** ověření;
- limitem velikosti PDF (`PDF_SIZE_EXCEEDED`; v binárním kanálu se hlídá už při čtení, soubor
  nad limitem se celý do paměti nenačte) a `PdfSafetyGuard` (`PDF_INVALID`,
  `PDF_ENCRYPTED`, `PDF_TOO_MANY_PAGES`, `PDF_TOO_COMPLEX`, `PDF_ACTIVE_CONTENT`);
- limity obrázků v metadatech (`IMAGE_SIZE_EXCEEDED`…), limitem rozpracovaných presignů (429);
- rezervací a účtováním operace, idempotencí a lhůtou (viz [operations.md](operations.md));
- bránou, rate limity a souběhem (#73) — ty se na tělo nedívají vůbec.

Testy: `tests/…/Security/BinaryChannelTests.cs` pouští každý scénář oběma kanály.

## Idempotence: klíč je vázaný na kanál

Otisk požadavku (`ApiOperationService.Fingerprint`) se v JSON kanálu počítá z těla včetně
base64 — beze změny, aby opakování s týmž klíčem přes nasazení dál sedělo. V binárním kanálu
z metadat a SHA-256 dokumentu. **Tentýž `Idempotency-Key` poslaný jednou binárně a podruhé
jako JSON proto skončí `422 IDEMPOTENCY_KEY_REUSED`.** Portál má opakovat tímtéž kanálem.
(`/api/sign` se tím netýká: jeho identita je presign, ne tělo.)

## Limity

- Celé tělo: `Limits:RequestBodyLimitBytes` (40 MB) jako u JSONu → `413`.
- Část `pdf`: `Limits:PdfMaxBytes` (20 MB) → `400 PDF_SIZE_EXCEEDED`.
