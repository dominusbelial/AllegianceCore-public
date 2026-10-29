#!/usr/bin/env python3
"""C/C++ comment stripping for the native-table generators (P871b).

`Igc/sounds.h` is `#include`d inside an enum (`Igc/igc.h:456-464`), so the
native preprocessor decides which `DEFSOUND` names exist.  A naive
`re.sub(r'//[^\n]*', '', text)` is NOT that preprocessor:

    /* NYI no turrets            <- Igc/sounds.h:443-459
    // Turrets
    DEFSOUND(turretReady)
    ...
    */

The block is dead in native, but stripping only `//` lines leaves its
`DEFSOUND` names live and shifts every later id by +13 (native
`salRepairedAtCarrierSound` is 1386, not 1399).  Block comments must therefore
go first — and the block itself contains `//` lines, so a single-pass scanner is
the only correct tool.

Both generators (generate-sound-catalog.py, generate-voice-catalog.py) use this
one implementation so the two tables can never disagree again.
"""
from __future__ import annotations


def strip_cpp_comments(text: str) -> str:
    """Remove `//` and `/* */` comments, leaving string/char literals intact."""
    out = []
    i, n = 0, len(text)
    while i < n:
        ch = text[i]
        if ch == '/' and text.startswith('//', i):
            end = text.find('\n', i)
            i = n if end < 0 else end
        elif ch == '/' and text.startswith('/*', i):
            end = text.find('*/', i + 2)
            i = n if end < 0 else end + 2
        elif ch in '"\'':
            start, quote = i, ch
            i += 1
            while i < n and text[i] != quote:
                i += 2 if text[i] == '\\' else 1
            i = min(i + 1, n)
            out.append(text[start:i])
        else:
            out.append(ch)
            i += 1
    return ''.join(out)
