"""Advance widths (1/1000 em) of the signature fonts for the shared layout engine."""
import json, sys, os
from fontTools.ttLib import TTFont

FONTS_DIR = sys.argv[1]
EXTRA = [0x2013, 0x2014, 0x2018, 0x2019, 0x201A, 0x201C, 0x201D, 0x201E, 0x2022, 0x2026, 0x20AC]
CODEPOINTS = list(range(0x20, 0x7F)) + list(range(0xA0, 0x180)) + EXTRA
FAMILIES = {"Helvetica": "LiberationSans", "Times": "LiberationSerif", "Courier": "LiberationMono"}

out = {"unitsPerEm": 1000, "codepoints": "".join(chr(c) for c in CODEPOINTS), "fonts": {}}
for family, stem in FAMILIES.items():
    for weight in ("Regular", "Bold"):
        f = TTFont(os.path.join(FONTS_DIR, f"{stem}-{weight}.ttf"))
        cmap = f.getBestCmap(); hmtx = f["hmtx"]; upm = f["head"].unitsPerEm
        def w(cp):
            g = cmap.get(cp)
            return None if g is None else round(hmtx[g][0] * 1000 / upm)
        widths = [w(c) for c in CODEPOINTS]
        fallback = w(ord("n"))
        out["fonts"][f"{family}-{weight}"] = {"fallback": fallback, "widths": [x if x is not None else fallback for x in widths]}
json.dump(out, sys.stdout, ensure_ascii=False, separators=(",", ":"))
