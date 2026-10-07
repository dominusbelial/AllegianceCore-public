# P567 — native asteroid rocks (old vs new, winding)

Each native rock model from 4 angles (front/side/back/top), perspective projection.
- `<model>.png` — the NEW native `.mdl` + native texture, raw winding + 
  `CullCounterClockwiseFace` (solid, per P570/P567).
- `<model>_winding.png` — the imported-variant winding demo: old (buggy) winding 
  with culling ON = INSIDE-OUT; old winding with culling OFF = hollow shell; 
  fixed native winding with culling ON = SOLID.

| sheet |
| [`bgrnd03`](bgrnd03.png) |
| [`bgrnd05`](bgrnd05.png) |
| [`bgrnd51`](bgrnd51.png) |
| [`bgrnd52`](bgrnd52.png) |
| [`bgrnd53`](bgrnd53.png) |
| [`bgrnd55`](bgrnd55.png) |
| [`bgrnd56`](bgrnd56.png) |
| [`bgrnd57`](bgrnd57.png) |
| [`drgrock_winding`](drgrock_winding.png) |
| [`tacrock_winding`](tacrock_winding.png) |
| [`bgrnd05_winding`](bgrnd05_winding.png) |
