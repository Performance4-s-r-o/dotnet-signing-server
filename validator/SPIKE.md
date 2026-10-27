# Spike: validátor PDF podpisů nad EU DSS

Rozhodovací brána pro validaci podpisů (dotnet #81, fáze 4B). Otázka zněla: unese
tenký sidecar nad EU DSS naše dokumenty, a za jakou cenu (image, RAM, latence,
provoz bez sítě, SSRF)? **Není to produkční služba.** Co chybí do produkce, je
v posledním oddílu.

Měřeno 27. 10. 2026 v Dockeru na vývojovém stroji (Linux VM, arm64), limit
kontejneru 1 GiB. Čísla jsou orientační, ne benchmark.

## Co je postaveno

`validator/` je samostatný Maven projekt (Spring Boot 3.5.16, Java 21, EU DSS 6.5.1):

| Soubor | Účel |
|---|---|
| `net/GuardedDataLoader.java` | Jediná cesta do sítě. Jen http/https, žádné přesměrování, žádná proxy z prostředí, DNS se přeloží jednou a spojení jde přesně na vrácenou adresu (bez rebindingu), neveřejné adresy se odmítnou, timeouty 3 s / 5 s / 15 s celkem, strop 20 MiB |
| `net/AddressPolicy.java` | Co je „veřejná adresa“: mimo loopback, privátní, link-local (včetně 169.254.169.254), CGNAT, ULA, multicast, testovací sítě; IPv4 zabalené v IPv6 (`::ffff:`, NAT64, 6to4) se kontroluje jako IPv4 |
| `trust/TrustedListService.java` | EU LOTL s pivoty a certifikáty z Úředního věstníku, národní TL. Start vždy z cache ve volume, online obnova jen při `VALIDATOR_TL_MODE=online`. Neplatný ani prošlý seznam se nesynchronizuje |
| `validation/ValidationService.java` | Validace podle pevné politiky (výchozí `constraint.xml` z DSS, SHA-256 politiky v každé odpovědi), mapování na kontrakt z návrhu 4B §3 |
| `web/ValidateController.java` | `POST /validate` (tělo = PDF binárně, nic se neukládá), `GET /health` |
| `Dockerfile` | multi-stage: Maven build → `jlink` JRE → `debian:bookworm-slim`, neprivilegovaný uživatel |

Odpověď `/validate` obsahuje `validator.{engine,version,policyId,policySha256,tlLoadedAt,tlStale}`,
`documentSha256`, `byteLength` a pro každý podpis `integrity`, `coversWholeDocument`,
`byteRangeSane`, `modifiedAfterSigning`, `indication`/`subIndication`, `qualification`,
`padesLevel`, `certificate.{subject,issuer,serial,trustSource,chainTrusted,revocation,qualified}`,
`timestamp.{present,valid,messageImprintOk,tsaSubject,tsaTrustSource,tsaQualified,genTime}`,
předběžný `trustStatus` a texty chyb/varování DSS. Razítka dokumentu jsou zvlášť v `documentTimestamps`.

Konfigurace (proměnné prostředí): `VALIDATOR_TL_MODE` (`offline`|`online`),
`VALIDATOR_TL_CACHE_DIR`, `VALIDATOR_TL_MAX_AGE_HOURS` (24), `VALIDATOR_TL_REFRESH_HOURS` (6),
`VALIDATOR_REVOCATION_ONLINE` (true), `VALIDATOR_MAX_DOCUMENT_BYTES` (25 MiB),
`VALIDATOR_MAX_CONCURRENCY` (2), `VALIDATOR_TEST_TRUST_ANCHORS` (jen testy).

## Licence

| Komponenta | Licence | Poznámka |
|---|---|---|
| EU DSS 6.5.1 (`dss-*`, `specs-*`) | LGPL-2.1 | Knihovny v samostatném procesu; fat jar je drží jako oddělené JARy, takže jdou vyměnit (podmínka LGPL) |
| Apache PDFBox 3.0.8 | Apache-2.0 | Modul `dss-pades-pdfbox`, ne OpenPDF |
| BouncyCastle 1.86 | Bouncy Castle (MIT) | |
| Apache HttpClient 5.6, Santuario xmlsec, commons-* | Apache-2.0 | |
| JAXB / Jakarta API | EDL-1.0 (BSD-3) / EPL-2.0 | |
| Spring Boot, Tomcat, Jackson | Apache-2.0 | |
| Logback | EPL-1.0 / LGPL-2.1 | |
| Certifikáty LOTL z Úředního věstníku | z `esig/dss-demonstrations` 6.5.1 (LGPL-2.1) | Stahuje se při buildu image se SHA-256, v repu není |
| Testovací PDF z `esig/dss` | LGPL-2.1 | Jen odkaz, do repa se nekopírují |

Kód wrapperu je AGPL jako zbytek repa. S engine se nelinkuje, volá se po HTTP.

## Naměřená čísla

| Co | Výsledek | Odhad v návrhu |
|---|---|---|
| Image (pull, komprimovaný) | **~118 MB** | 350–600 MB |
| Image rozbalený | ~240 MB: debian-slim 108 MB, fontconfig + DejaVu 17 MB, jlink JRE 66 MB, fat jar 48 MB | |
| Start bez TL | 1,1 s Spring, připraven za 1,5 s | |
| Start s LOTL z cache | připraven za 7,6–8,5 s, z toho načtení a ověření TL 6,5–6,9 s (LOTL + 31 TL, 4 541 důvěryhodných certifikátů) | |
| RAM bez TL (RSS) | 162 MiB po startu, 258 MiB po 40 validacích | |
| RAM s LOTL (RSS, G1, `MaxRAMPercentage=75`) | 490–525 MiB po startu, 520–570 MiB po validacích | heap 0,75–1,5 GB |
| RAM s LOTL, `-XX:+UseSerialGC -Xmx192m` | 404 MiB po startu, 441 MiB po validacích, bez OOM, ale pomalejší | |
| Latence, studená (první požadavek) | 0,25–0,54 s | |
| Latence, teplá, naše PDF (~70–140 kB) | 11–35 ms | 0,3–1 s |
| Latence, teplá, reálné EU vzorky DSS | 16–160 ms, největší (527 kB, 2 podpisy) 0,27–0,34 s; se `Xmx192m` 0,5–0,8 s | |
| První online načtení LOTL | ~77 s od startu kontejneru, 27 MB cache, 31 TL (29 použitelných) | 1–3 min |
| Bez sítě (`--network none`) | z cache vše funguje; prázdná cache → `tlStale: true`, `lotlValid: false`, integrita a změny se hlásí dál | |

Validace bez sítě znamená bez OCSP/CRL. Online revokace přidá RTT každého responderu
(neměřeno; strop dávají timeouty guardu).

**Výhrada k času načtení LOTL:** síť byla povolena jednou. V tom běhu se stáhlo všech
27 MB, ale parsování padalo na konflikt verzí (Spring Boot stáhl `commons-lang3`
3.17, DSS 6.5.1 potřebuje 3.18+; opraveno v `pom.xml`). Číslo ~77 s tedy obsahuje
stažení, ne čisté zpracování. Zpracování z cache trvá 6,5–6,9 s, takže první
načtení vychází kolem 1–1,5 min. Druhý běh po síti se nedělal.

## Výsledky na fixtures

### (a) PDF z našeho enginu

Generuje je `tests/DotNetSigningServer.Tests/Spike/DssValidatorFixtures.cs` produkčními
cestami (presign → podpis atributů „u klienta“ → sign; `SignWithPfx`; pečeť; `PdfLtvService`)
s vlastní testovací PKI (kořenová CA → podepisující a TSA). CRL DP v certifikátech míří na
`http://127.0.0.1:9/…`, AIA a OCSP na `http://169.254.169.254/…`.

Bez testovací kotvy (jak to uvidí produkce, pokud CA není na EU TL):

| Fixture | `padesLevel` | indication / subIndication | změny | `trustStatus` |
|---|---|---|---|---|
| external B-B | PAdES_BASELINE_B | INDETERMINATE / NO_CERTIFICATE_CHAIN_FOUND | none | integrity-only |
| external B-T | PAdES_BASELINE_T | totéž, razítko `messageImprintOk` | none | integrity-only |
| external B-LT | PAdES_BASELINE_LT | totéž, revokace `good` z vložené CRL | allowed | integrity-only |
| external B-LTA | PAdES_BASELINE_LTA | totéž, razítko dokumentu se otiskem v pořádku | allowed | integrity-only |
| B-T s QR stránkou | PAdES_BASELINE_T | totéž | none | integrity-only |
| dva podpisy | PAdES_BASELINE_T ×2 | totéž | 1. allowed, 2. none | integrity-only |
| pečeť platformy | PAdES_BASELINE_B | totéž | none | integrity-only |
| `sign-pfx` samopodepsaný | PAdES_BASELINE_B | totéž | none | integrity-only |

S testovací kořenovou CA jako kotvou (`VALIDATOR_TEST_TRUST_ANCHORS`, online revokace zapnutá):

| Fixture | indication / subIndication | proč |
|---|---|---|
| B-LT, B-LTA | **TOTAL_PASSED** (razítko dokumentu PASSED) | revokační data jsou v dokumentu |
| B-B, B-T, QR, dva podpisy, pečeť | INDETERMINATE / CERTIFICATE_CHAIN_GENERAL_FAILURE | „No revocation data found“: CRL a OCSP v certifikátu míří na zakázané adresy, guard je zablokoval |

**Odpověď na klíčovou otázku:** DSS naše dokumenty čte jako regulérní PAdES baseline
(B/T/LT/LTA), ne jako „non-PAdES“. `ETSI.CAdES.detached` se `signing-certificate-v2`
projde. Chybějící VRI nevadí: B-LT z iTextu (DSS slovník bez VRI) dává `revocation: good`
a s důvěryhodnou kotvou `TOTAL_PASSED`. Jediné varování DSS u našich PDF je
`BBB_XCV_IAKIP_ANS` (chybí Authority Key Identifier). To je vlastnost testovací PKI,
ne enginu.

Pro štítky z §3 z toho plyne:

- Certifikát z CA mimo EU TL (Fortify s interní CA, naše pečeť) vychází `integrity-only`.
  AES/QES z toho nikdy nevznikne. Odpovídá návrhu.
- B-B a B-T bez vložené revokace potřebují online OCSP/CRL. Bez sítě (air-gapped on-prem)
  vyjdou `indeterminate`, i když řetězec vede k EU TL. Jen B-LT/B-LTA se dají ověřit offline.
- Naše pečeť nenese QC statement, takže ji DSS nikdy nevyhodnotí jako `QESeal`/`AdESeal-QC`.
  `kind` ji proto hlásí jako `signature`. Pečeť se dnes pozná jen podle toho, kdo ji vydal, ne z certifikátu.

### (b) Testovací PDF z repozitáře DSS

`dss-pades/src/test/resources/validation` na tagu 6.5.1 (LGPL-2.1), validováno offline
(TL z cache, bez OCSP/CRL ze sítě). Soubory nejsou v repu.

| Soubor | Výsledek DSS | `trustStatus` |
|---|---|---|
| `Signature-P-SK-6.pdf` (SK, QES) | INDETERMINATE / NO_CERTIFICATE_CHAIN_FOUND_NO_POE, *Indeterminate QESig*, TSA `TSA`, řetězec z EU TL | indeterminate |
| `Signature-P-DE_SCI-4.pdf`, `Test.signed_Certipost…-LTA.pdf`, `Signature-P-BG_BOR-1.pdf` | INDETERMINATE / NO_CERTIFICATE_CHAIN_FOUND_NO_POE, *Indeterminate AdESig*, řetězec z EU TL | indeterminate |
| `belgian_pki_multiple_ocsps_lt.pdf` | INDETERMINATE / REVOCATION_OUT_OF_BOUNDS_NO_POE, *Indeterminate QESig*, TSA `QTSA` | indeterminate |
| `Signature-P-FR_CS-5`, `-HU_MIC-1`, `doc-firmado-LT`, `PAdES-LT`, `pkcs7` | INDETERMINATE / NO_CERTIFICATE_CHAIN_FOUND (řetězec k načtenému EU TL nevede) | integrity-only |
| `self-issued-qesig.pdf` | INDETERMINATE / CERTIFICATE_CHAIN_GENERAL_FAILURE | indeterminate |
| `pdf-spoofing-attack.pdf` | TOTAL_FAILED / FORMAT_FAILURE, *Not AdES but QC with QSCD*, nepovolené změny | modified |
| `pades-spoofing-replaced-reason`, `pdf-signed-added-page`, `pdf-removed-pages`, `modified_after_signature`, `pades-signed-filled-form` | nepovolené změny (FORMAT_FAILURE nebo vizuální/stránkové rozdíly) | modified |
| `pades-signed-annot-added.pdf` | povolená změna (anotace) | integrity-only |
| `pdf-signed-corrupted`, `pdf-signed-out-of-byteRange`, `pdf-byterange-overlap` (2. podpis) | TOTAL_FAILED / FORMAT_FAILURE, nesmyslný ByteRange | invalid |

Reálné EU vzorky jsou staré (např. DE_SCI-4 je z roku 2016). Certifikáty jsou prošlé a stav
služeb na TL se od té doby mohl změnit, takže DSS bez důkazu existence (POE) z doby platnosti nerozhodne. Je to
konzervativní a správné chování: **žádný starý vzorek nedal AES ani QES**. První verze
mapování brala `*_NO_POE` jako `integrity-only`, ale u řetězce z EU TL to je
`indeterminate`. Opraveno, pravidlo patří do kontraktních testů PR 13.

**Čerstvý kvalifikovaný podpis (TOTAL_PASSED + QESig) spike neověřil**: veřejný vzorek
s platným QES certifikátem není. To je hlavní nejistota, viz Rozhodnutí.

### (c) Poškozené a změněné PDF z našeho enginu

| Fixture | Výsledek DSS | `trustStatus` |
|---|---|---|
| změněný bajt v podepsaném rozsahu | TOTAL_FAILED / HASH_FAILURE | invalid |
| useknutý konec souboru | TOTAL_FAILED / FORMAT_FAILURE, ByteRange mimo soubor | invalid |
| inkrementálně přidaný text na stránku | nepovolená změna (vizuální rozdíl) | modified |
| inkrementálně přidaná anotace | povolená změna | integrity-only |
| smetí za `%%EOF` | DSS **nehlásí nic**, jen `coversWholeDocument: false` z wrapperu | integrity-only, `modifiedAfterSigning: unknown` |

Smetí za koncem souboru DSS za změnu nepovažuje. Zachytí ho jen vlastní kontrola
pokrytí, proto wrapper vrací `modifiedAfterSigning: unknown` a `trusted` se pak nedá.

## SSRF

- Jednotkové testy (`GuardedDataLoaderTest`, 30 případů): `127.0.0.1`, `localhost`,
  `2130706433` (desítkový zápis), `[::1]`, `::ffff:127.0.0.1`, `::ffff:169.254.169.254`,
  NAT64 `64:ff9b::a9fe:a9fe`, 6to4, `169.254.169.254`, RFC 1918, CGNAT, `ldap://`,
  `file://`, URL s přihlašovacími údaji. Vše odmítnuto, veřejné adresy projdou.
- Integračně: 13 fixtures × 2 s CRL na `127.0.0.1` a AIA/OCSP na `169.254.169.254`,
  s důvěryhodnou kotvou, aby DSS revokaci opravdu hledal. **190 zablokovaných pokusů,
  žádné spojení.**
- Bez kotvy DSS revokaci u nedůvěryhodného řetězce vůbec nehledá
  (`checkRevocationForUntrustedChains=false`). AIA ale stahuje, když chybí mezilehlý
  certifikát, a ten může útočník z CMS vynechat schválně. Guard je proto nutný vždy.
- DSS stahuje znovu u každého požadavku (wrapper nemá revokační cache). Bez cache a
  rozpočtu na počet stažení je to zesilovač provozu: jeden upload = N požadavků ven.

## Bez sítě a `tlStale`

- `--network none` + cache ve volume: start za ~8 s, `lotlValid: true`, `tlStale: false`
  do `VALIDATOR_TL_MAX_AGE_HOURS` od stažení LOTL.
- `VALIDATOR_TL_MAX_AGE_HOURS=0` → `tlStale: true` a `trusted` se mapuje na `indeterminate`.
- Prázdná cache: `tlStale: true`, 0 certifikátů; integrita, `HASH_FAILURE` a změny se
  vyhodnotí dál, všechny podpisy jsou nanejvýš `integrity-only`.
- `/health` nesmí čekat na zámek obnovy TL. První verze čekala, `job.getSummary()` je
  synchronizované s `onlineRefresh()` a health 77 s neodpovídal. Opraveno čtením souhrnu
  z `TrustedListsCertificateSource`.

## Nejistoty

1. Čerstvý QES/QSeal (TOTAL_PASSED + QESig/QESeal) neověřen. Bez něj nevíme, že cesta
   ke štítku QES funguje end-to-end.
2. Online revokace proti reálným OCSP/CRL neměřena (latence, chování při výpadku responderu).
3. Čas prvního online načtení LOTL je jen odhad (výhrada výše).
4. Velká PDF a PDF bomby: DSS vykresluje stránky kvůli vizuálnímu porovnání revizí (PDFBox).
   CPU a RAM u velkých dokumentů neměřeny.
5. Měřeno na arm64. Na amd64 bude image podobný, latence se může lišit.
6. Policy je výchozí `constraint.xml` z DSS, nikdo ji nerevidoval proti našemu účelu.

## Co musí udělat PR 12 (produkční služba)

- **DataLoader**: rozpočet na validaci (počet stažení, celkový čas), cache OCSP/CRL/AIA
  s respektem k `nextUpdate`, metriky zablokovaných pokusů. Volitelný egress přes proxy
  jen explicitní konfigurací.
- **TL**: plánovaná online obnova, cache ve volume, `/health` (liveness) a `/ready`
  (LOTL platný), alert, když se TL nepodaří obnovit déle než 24 h, alerty DSS na změnu
  OJ URL a umístění LOTL (`OJUrlChangeDetection`, `LOTLLocationChangeDetection`).
  Certifikáty z OJ v image se SHA-256 a postup jejich aktualizace.
- **Politika**: vlastní kopie `constraint.xml` verzovaná v repu, revidovaná,
  `policyId` = naše jméno + SHA-256.
- **Mapování `trustStatus`** přesunout do dotnetu (PR 13, kontrakt). Validátor vrací jen
  fakta DSS. Pravidla z tohoto spiku: `*_NO_POE` → `indeterminate`, `coversWholeDocument=false`
  bez rozpoznané revize → `unknown`, `FORMAT_FAILURE` s nepovolenou změnou → `modified`.
- **Limity**: deadline validace, velikost a počet stránek (stejné jako dotnet #70), souběžnost,
  paměť PDFBoxu, neukládat dokument, nelogovat obsah.
- **Testovací kotvy** odstranit, nebo s nimi odmítnout start mimo testovací prostředí.
- **Compose profil `validator`, výchozí vypnutý**: jen interní síť, žádný publikovaný port,
  volume pro cache TL, limit paměti 768 MiB (heap ~384 MiB), healthcheck. Bez validátoru
  dotnet vrací `unavailable` (on-prem volitelný).
- **Proměnné prostředí** podle pravidla tří míst (`.env`, `docker-compose.yml`, dokumentace),
  v dotnetu adresa validátoru (prázdná = vypnuto).
- **Verze závislostí**: Spring Boot přebíjí verze, které DSS potřebuje (`commons-lang3`,
  `httpclient5`, JAXB). Pevné verze plus test, který načte LOTL z cache.
- **CI**: Maven build a testy, build image, hlídání nových verzí DSS (zhruba 2× ročně).
- **LGPL**: seznam třetích stran a zdroje v image.

## Reprodukce

```bash
# fixtures z enginu
DSS_SPIKE_FIXTURES_DIR=/tmp/fixtures dotnet test tests/DotNetSigningServer.Tests \
  --filter "FullyQualifiedName~DssValidatorFixtures"

# image a běh (online jednou kvůli cache TL, pak offline)
docker build -t pdf-validator validator
docker run -d -p 127.0.0.1:8090:8090 -e VALIDATOR_TL_MODE=online \
  -v tl-cache:/var/cache/validator/tl pdf-validator
curl -s -H 'Content-Type: application/pdf' --data-binary @/tmp/fixtures/02-ours-external-B-T.pdf \
  http://127.0.0.1:8090/validate
```
