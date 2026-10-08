# P577b — HD sky tier: asset credits & licences

Every HD sky asset is derived from real telescope public data. Two licence
families are used — both permit commercial redistribution with attribution.
The per-asset mapping lives in `../sky-hd.json`; this folder archives the
licence texts and the full credit table, matching the P546 packaging pattern.

P577b changes vs P577: **HD planets keep the native alien planet art**
(no external imagery — the NASA Solar-System discs were removed) and the two
weak backgrounds were swapped (**globe1 → the Veil Nebula, heic0712a**;
**globe3 → Westerhout 5 / the Soul Nebula, potw2251a**). Every environment
credit below was verified against its ESA/Hubble release page on 2026-10-08.

## Credit table

| Asset | Source | Licence | Credit (verified from release page) |
|---|---|---|---|
| `hdsky/stars/*` (6 faces, desktop + Android) | NASA SVS Deep Star Maps 2020 (4851) | Public Domain (NASA) | NASA/Goddard Space Flight Center Scientific Visualization Studio |
| `hdsky/nebula/env1.png` | ESA/Hubble heic0712a (Veil Nebula) | CC BY 4.0 | NASA, ESA, and the Hubble Heritage (STScI/AURA)-ESA/Hubble Collaboration. Acknowledgment: J. Hester (Arizona State University) |
| `hdsky/nebula/env2.png` | ESA/Hubble heic1608a (Bubble Nebula) | CC BY 4.0 | NASA, ESA, Hubble Heritage Team |
| `hdsky/nebula/env3.png` | ESA/Hubble potw2251a (Soul Nebula / Westerhout 5) | CC BY 4.0 | ESA/Hubble & NASA, R. Sahai |
| `hdsky/nebula/env4.png` | ESA/Hubble heic1501a (Pillars of Creation) | CC BY 4.0 | NASA, ESA/Hubble and the Hubble Heritage Team |
| `hdsky/nebula/env5.png` | ESA/Hubble heic0619a (NGC 6357 / Pismis 24) | CC BY 4.0 | NASA, ESA and Jesús Maíz Apellániz (Instituto de Astrofísica de Andalucía, Spain). Acknowledgement: Davide De Martin (ESA/Hubble) |
| `hdsky/nebula/env6.png` | ESA/Hubble heic1707a (SN remnant N103B) | CC BY 4.0 | ESA/Hubble, NASA |
| `hdsky/nebula/env7.png` | ESA/Hubble heic2007a (Tapestry of Blazing Starbirth) | CC BY 4.0 | NASA, ESA, and STScI |
| `hdsky/nebula/env8.png` | ESA/Hubble heic1007a (Mystic Mountain) | CC BY 4.0 | NASA, ESA, M. Livio and the Hubble 20th Anniversary Team (STScI) |
| `hdsky/nebula/env9.png` | ESA/Hubble heic1307a (Horsehead IR) | CC BY 4.0 | NASA, ESA, and the Hubble Heritage Team (AURA/STScI) |
| `hdsky/nebula/env10.png` | ESA/Hubble heic1509a (Westerlund 2) | CC BY 4.0 | NASA, ESA, the Hubble Heritage Team (STScI/AURA), A. Nota (ESA/STScI), and the Westerlund 2 Science Team |
| `hdsky/planet/*` (native) | Native Allegiance planet art `nebplntNNbmp.mdl` / `nebplntNN.png` | N/A (native game art) | — (P577b: HD planets use native art; no external credit) |

The AI-generated/composited texture files are derivative works of the sources
listed; the credit lines above are carried into the in-game Credits/Licences
screen (see `docs/reports/P577_HD_SKY_TIER.md` §Credits and
`docs/reports/P577b_HD_SKY_CORRECTIONS.md`).

Archived licence texts:
- `esa_hubble_CC_BY_4.0.txt` — Creative Commons Attribution 4.0 International
  (the ESA/Hubble/ESA-Webb image licence). Full legal text:
  https://creativecommons.org/licenses/by/4.0/legalcode
- `nasa_public_domain.txt` — note on NASA imagery being US Government works in
  the public domain.

No image contains third-party copyrighted content beyond the telescope
observatories' own releases, which are themselves CC BY / public domain.
