# Bundled fonts

Every font shipped in this directory is licensed under the
**SIL Open Font License, Version 1.1** (OFL-1.1). The OFL permits bundling,
redistribution and embedding in documents — including commercially — as long
as the license text and copyright notice travel with the fonts. The full text
for each family lives in [`LICENSES/`](LICENSES).

| Family | Version | Copyright | License |
|---|---|---|---|
| Inter | 4.001 | Copyright 2016 The Inter Project Authors — <https://github.com/rsms/inter> | [OFL-1.1](LICENSES/Inter-OFL.txt) |
| JetBrains Mono | 2.211 | Copyright 2020 The JetBrains Mono Project Authors — <https://github.com/JetBrains/JetBrainsMono> | [OFL-1.1](LICENSES/JetBrainsMono-OFL.txt) |
| Liberation Mono / Sans / Serif | 2.1.5 | Digitized data copyright (c) 2010 Google Corporation; Copyright (c) 2012 Red Hat, Inc. | [OFL-1.1](LICENSES/Liberation-OFL.txt) |
| Lora | 3.008 | Copyright 2011 The Lora Project Authors — <https://github.com/cyrealtype/Lora-Cyrillic> | [OFL-1.1](LICENSES/Lora-OFL.txt) |
| Merriweather | 2.100 | Copyright 2024 The Merriweather Project Authors — <https://github.com/EbenSorkin/Merriweather4> | [OFL-1.1](LICENSES/Merriweather-OFL.txt) |
| Source Sans 3 | 3.052 | © 2023 Adobe — <http://www.adobe.com/> | [OFL-1.1](LICENSES/SourceSans3-OFL.txt) |

## Reserved Font Names

Lora, Merriweather and Source Sans 3 carry a Reserved Font Name (`Lora`,
`Merriweather`, `Source`). Under OFL-1.1 §3 a **modified** version of those
files may not keep the reserved name — rename it before redistributing.
The files here are unmodified upstream releases, so the names stand.

## Adding a font

1. Confirm the license permits redistribution and document embedding.
   OFL-1.1 and Apache-2.0 are fine; most commercial EULAs are not.
2. Drop the `.ttf` into this directory — `dotnet-signing-server.csproj`
   copies `Fonts/*.ttf` to the output directory automatically.
3. Add the upstream license file to `LICENSES/` and a row to the table above.
4. Register the family in `Services/AppFonts.cs`.
