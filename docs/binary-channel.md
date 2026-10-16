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
| jen čtení vstupu, alokováno | 131 MB | 68 MB |
| celý presign, alokováno | 511 MB (main) / 492 MB (#74) | 430 MB |

(68 MB = zdvojování bufferu od 256 KB a kopie na přesnou délku. Předalokace podle
`Content-Length` by dala 36 MB, ale velikost alokace by pak určovala hlavička volajícího.)

Timestamp a pečeť (`LoadProfileTests.BinaryChannelProfile_TimestampAndSeal`, PDF 18,1 MB, medián ze tří
běhů, 14 jader, .NET 8; spouští se s `P4_LOAD_PROFILE=1`, test ověřuje jen „multipart alokuje méně“):

| | JSON base64 | multipart |
|---|---|---|
| timestamp, alokováno | 557 MB | 477 MB |
| timestamp, špička procesu | 237 MB | 161 MB |
| seal, alokováno | 762 MB | 681 MB |
| seal, špička procesu | 172 MB | 170 MB |

Úspora je zhruba 80 MB na požadavek (vstup); zbytek je iText a výstup v base64, který zůstává.
Špičky se mezi běhy hýbou podle toho, kdy proběhne GC.

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
| `POST /api/timestamp` | ✅ multipart | ✅ `Accept: application/pdf` |
| `POST /api/sign-pfx` | ✅ multipart (PFX a heslo zůstávají v `metadata`) | ✅ |
| `POST /api/extend-signature` | ✅ multipart | ✅ |
| `POST /api/inspect-signatures` | ✅ multipart | — (odpověď je zpráva o podpisech) |
| `POST /api/visual-sign` | ✅ multipart | ✅ |
| `POST /api/seal` | ✅ multipart (oprávnění k pečeti se ověřuje před čtením těla) | ✅ |
| `POST /api/attachment` | ✅ multipart (`attachmentContent` zůstává v `metadata` v base64; strop metadat roste o `Limits:AttachmentMaxBytes`) | ✅ |
| `POST /api/convert/pdfa` | ✅ multipart | ✅ (+ `X-PDF-Conformance`) |

Ostatní endpointy berou zatím jen JSON (multipart na nich skončí `415`). Záměrně mimo:
`/api/fill-pdf` (PDF na vstupu je jen volitelný přímý režim, hlavní cesta je šablona a
výstupem je pole souborů; `FillAsync` čte `input.PdfContent` uvnitř a jeho podpis mockují testy),
`/api/pdf-template*` a `/api/ai/*` (ukládají base64 do databáze nebo ho posílají dál).
`Accept: application/pdf` umí všechny, které PDF vracejí, už dnes.

## Co platí v obou kanálech stejně

Oba kanály končí ve stejném kódu (`PreSignCoreAsync`, `FindCodesCoreAsync`, `TimestampCoreAsync`,
`SealCoreAsync`…; služby mají přetížení přijímající `byte[]`, verze s base64 volá totéž s dekódovanými
bajty) a projdou:

- ověřením tokenu a kontrolou kreditů (401/402) — binární tělo se navíc čte až **po** ověření;
- limitem velikosti PDF (`PDF_SIZE_EXCEEDED`; v binárním kanálu se hlídá už při čtení, soubor
  nad limitem se celý do paměti nenačte) a `PdfSafetyGuard` (`PDF_INVALID`,
  `PDF_ENCRYPTED`, `PDF_TOO_MANY_PAGES`, `PDF_TOO_COMPLEX`, `PDF_ACTIVE_CONTENT`);
- limity obrázků v metadatech (`IMAGE_SIZE_EXCEEDED`…), limitem rozpracovaných presignů (429);
- rezervací a účtováním operace, idempotencí a lhůtou (viz [operations.md](operations.md));
- bránou, rate limity a souběhem (#73) — ty se na tělo nedívají vůbec.

Testy: `tests/…/Security/BinaryChannelTests.cs` (s databází) pouští každý scénář oběma kanály;
`BinaryChannelEndpointsTests.cs` a `Services/BinaryOverloadServiceTests.cs` běží bez Dockeru
(kontrolery přímo, `RunOperationAsync` zachytává testovací potomek) a pro každý z osmi
endpointů ověřují stejný výsledek jako JSON, stejné odmítnutí, `MULTIPART_INVALID` a otisk.

## Idempotence: klíč je vázaný na kanál

Otisk požadavku (`ApiOperationService.Fingerprint`) se v JSON kanálu počítá z těla včetně
base64 — beze změny, aby opakování s týmž klíčem přes nasazení dál sedělo. V binárním kanálu
z operace, metadat (s prázdným `PdfContent`) a SHA-256 dokumentu (`ApiControllerBase.BinaryFingerprint`):
stejná operace nad stejným dokumentem = replay, jiný dokument = jiná operace (`IDEMPOTENCY_KEY_REUSED`). **Tentýž `Idempotency-Key` poslaný jednou binárně a podruhé
jako JSON proto skončí `422 IDEMPOTENCY_KEY_REUSED`.** Portál má opakovat tímtéž kanálem.
(`/api/sign` se tím netýká: jeho identita je presign, ne tělo.)

## Limity

- Celé tělo: `Limits:RequestBodyLimitBytes` (40 MB) jako u JSONu → `413`. Hlídá se i při
  čtení multipartu, nejen serverem, takže platí i pro tělo bez `Content-Length` (chunked).
- Část `pdf`: `Limits:PdfMaxBytes` (20 MB) → `400 PDF_SIZE_EXCEEDED`, hned jak čtení limit
  přesáhne. Buffer začíná na 256 KB a zdvojuje se; `Content-Length` je jen horní odhad,
  podle hlavičky se předem nic velkého nealokuje.
- Část `metadata`: 4 obrázky v base64 po `Limits:ImageMaxBytes` + 1 MB (výchozí ≈ 6,3 MB)
  → `400 MULTIPART_INVALID`, ještě před deserializací.
- Preambule a každá část také nejvýš tolik (`MultipartReader.BodyLengthLimit`).
- Bez platného tokenu odpoví brána `401` a tělo aplikace vůbec nečte (test to měří).

## Pro vývojáře

Akce binárního kanálu **nesmí číst `Request.Form` / `ReadFormAsync`** (ani přes
`[FromForm]`/`IFormFile`): ASP.NET by celé tělo načetl a nabufferoval (nad 64 KB na disk)
bez limitu části, dřív než by proběhla kterákoli kontrola. Tělo čte jen
`ApiControllerBase.ReadBinaryPdfAsync<T>`, proudem a s limitem každé části.

## Výsledek operace se ukládá dál jako JSON

Uložený výsledek (`ApiOperationService`, `ResultProtected`) zůstává šifrovaný JSON s base64 a příznak
`ResultIsPdf`. Uložit surové bajty by chtělo rozlišení formátu řádku (nový sloupec, tedy migrace, nebo
čichání obsahu), protože staré řádky JSON nesou a replay je z něj skládá, včetně dalších polí těla
(`conformance`). Řádek zapsaný novým kódem by navíc při návratu na starší verzi nešel přečíst.
Bez migrace to bezpečně nejde, proto se nedělalo.

## Idempotence a přepínání kanálů

Otisk operace (`Idempotency-Key`) se v binárním kanálu počítá z metadat a SHA-256 dokumentu, v JSON kanálu z těla s base64.
Stejná operace tedy dává v obou kanálech **různý otisk**. Volající, který opakuje operaci po chybě nebo timeoutu, musí
opakovat **stejným kanálem** a se **stejným klíčem**; přepnutí kanálu s týmž klíčem skončí `422 IDEMPOTENCY_KEY_REUSED`
a s novým klíčem by se účtovalo podruhé. Automatický „fallback na JSON" proto nesmí existovat.

