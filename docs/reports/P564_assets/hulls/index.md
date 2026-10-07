# P564 — per-hull shape-readability audit (opaque + cloaked)

Every player-flyable native hull, rendered from its original `.mdl` mesh + native texture 
under the real native sector light, 4 views (front/side/back/top) x rows (opaque / cloaked at 
opacity 0.45). One sheet per hull. Columns of the table: model, near-black %, normal-brightness correlation, note.

| model | sheet | nearBlack% | normalCorr | note |
|---|---|---|---|---|
| acs01 | [`acs01.png`](acs01.png) | 51% | 0.83 | near-black crush (dark art) |
| acs02 | [`acs02.png`](acs02.png) | 39% | 0.89 | ok |
| acs41 | [`acs41.png`](acs41.png) | 15% | 0.85 | ok |
| acs63 | [`acs63.png`](acs63.png) | 54% | 0.86 | near-black crush (dark art) |
| bom01b | [`bom01b.png`](bom01b.png) | 6% | 0.82 | ok |
| bom03 | [`bom03.png`](bom03.png) | 58% | 0.87 | near-black crush (dark art) |
| bom04 | [`bom04.png`](bom04.png) | 58% | 0.71 | near-black crush (dark art) |
| bom05b | [`bom05b.png`](bom05b.png) | 27% | 0.87 | ok |
| bom07b | [`bom07b.png`](bom07b.png) | 16% | 0.82 | ok |
| bom20 | [`bom20.png`](bom20.png) | 37% | 0.87 | ok |
| bom30 | [`bom30.png`](bom30.png) | 77% | 0.85 | near-black crush (dark art) |
| bom400 | [`bom400.png`](bom400.png) | 33% | 0.83 | ok |
| cap01 | [`cap01.png`](cap01.png) | 14% | 0.86 | ok |
| cap02a | [`cap02a.png`](cap02a.png) | 21% | 0.87 | ok |
| cap03 | [`cap03.png`](cap03.png) | 39% | 0.88 | ok |
| cap04 | [`cap04.png`](cap04.png) | 24% | 0.83 | ok |
| cap06a | [`cap06a.png`](cap06a.png) | 63% | 0.83 | near-black crush (dark art) |
| cap07 | [`cap07.png`](cap07.png) | 21% | 0.84 | ok |
| cap08 | [`cap08.png`](cap08.png) | 31% | 0.77 | ok |
| cap09 | [`cap09.png`](cap09.png) | 49% | 0.84 | ok |
| cap100 | [`cap100.png`](cap100.png) | 43% | 0.86 | ok |
| cap101 | [`cap101.png`](cap101.png) | 33% | 0.84 | ok |
| cap102 | [`cap102.png`](cap102.png) | 46% | 0.78 | ok |
| cap103 | [`cap103.png`](cap103.png) | 27% | 0.80 | ok |
| cap20 | [`cap20.png`](cap20.png) | 8% | 0.85 | ok |
| cap21 | [`cap21.png`](cap21.png) | 50% | 0.89 | near-black crush (dark art) |
| cap300 | [`cap300.png`](cap300.png) | 35% | 0.84 | ok |
| cap301 | [`cap301.png`](cap301.png) | 30% | 0.83 | ok |
| cap302 | [`cap302.png`](cap302.png) | 41% | 0.85 | ok |
| cap303 | [`cap303.png`](cap303.png) | 50% | 0.83 | ok |
| cap400 | [`cap400.png`](cap400.png) | 55% | 0.82 | near-black crush (dark art) |
| cap401 | [`cap401.png`](cap401.png) | 36% | 0.82 | ok |
| cap403 | [`cap403.png`](cap403.png) | 62% | 0.83 | near-black crush (dark art) |
| cap404 | [`cap404.png`](cap404.png) | 66% | 0.87 | near-black crush (dark art) |
| cap405 | [`cap405.png`](cap405.png) | 20% | 0.89 | ok |
| cap406 | [`cap406.png`](cap406.png) | 30% | 0.84 | ok |
| cap415 | [`cap415.png`](cap415.png) | 21% | 0.84 | ok |
| cap500 | [`cap500.png`](cap500.png) | 26% | 0.88 | ok |
| cap501 | [`cap501.png`](cap501.png) | 68% | 0.84 | near-black crush (dark art) |
| cap502 | [`cap502.png`](cap502.png) | 31% | 0.84 | ok |
| cap503 | [`cap503.png`](cap503.png) | 40% | 0.83 | ok |
| cap504 | [`cap504.png`](cap504.png) | 31% | 0.81 | ok |
| crus2 | [`crus2.png`](crus2.png) | 77% | 0.87 | near-black crush (dark art) |
| dcrus | [`dcrus.png`](dcrus.png) | 55% | 0.85 | near-black crush (dark art) |
| dminr | [`dminr.png`](dminr.png) | 35% | 0.87 | ok |
| dn_fadrshcon | [`dn_fadrshcon.png`](dn_fadrshcon.png) | 8% | 0.76 | ok |
| dn_fadrshfig | [`dn_fadrshfig.png`](dn_fadrshfig.png) | 4% | 0.68 | ok |
| dn_fadrshstl | [`dn_fadrshstl.png`](dn_fadrshstl.png) | 2% | 0.84 | ok |
| dn_faphshbmb | [`dn_faphshbmb.png`](dn_faphshbmb.png) | 7% | 0.86 | ok |
| dn_faphshbmbs | [`dn_faphshbmbs.png`](dn_faphshbmbs.png) | 9% | 0.87 | ok |
| dn_faphshcar | [`dn_faphshcar.png`](dn_faphshcar.png) | 7% | 0.86 | ok |
| dn_faphshcrs | [`dn_faphshcrs.png`](dn_faphshcrs.png) | 6% | 0.84 | ok |
| dn_faphshdst | [`dn_faphshdst.png`](dn_faphshdst.png) | 6% | 0.84 | ok |
| dn_faphshfig | [`dn_faphshfig.png`](dn_faphshfig.png) | 6% | 0.85 | ok |
| dn_faphshfrg | [`dn_faphshfrg.png`](dn_faphshfrg.png) | 6% | 0.83 | ok |
| dn_faphshint | [`dn_faphshint.png`](dn_faphshint.png) | 9% | 0.86 | ok |
| dn_faphshmnr | [`dn_faphshmnr.png`](dn_faphshmnr.png) | 22% | 0.85 | ok |
| dn_faphshpod | [`dn_faphshpod.png`](dn_faphshpod.png) | 1% | 0.86 | ok |
| dn_faphshrpb | [`dn_faphshrpb.png`](dn_faphshrpb.png) | 38% | 0.83 | ok |
| dn_faphshsct | [`dn_faphshsct.png`](dn_faphshsct.png) | 6% | 0.86 | ok |
| dn_faphshstl | [`dn_faphshstl.png`](dn_faphshstl.png) | 1% | 0.88 | ok |
| dn_faphshvng | [`dn_faphshvng.png`](dn_faphshvng.png) | 4% | 0.84 | ok |
| dn_fateshbmb | [`dn_fateshbmb.png`](dn_fateshbmb.png) | 62% | 0.85 | near-black crush (dark art) |
| dregcar | [`dregcar.png`](dregcar.png) | 58% | 0.86 | near-black crush (dark art) |
| dreghpod | [`dreghpod.png`](dreghpod.png) | 54% | 0.85 | near-black crush (dark art) |
| dregtt | [`dregtt.png`](dregtt.png) | 36% | 0.88 | ok |
| drgas | [`drgas.png`](drgas.png) | 82% | 0.85 | near-black crush (dark art) |
| drgmf | [`drgmf.png`](drgmf.png) | 56% | 0.82 | near-black crush (dark art) |
| drgtow | [`drgtow.png`](drgtow.png) | 54% | 0.90 | near-black crush (dark art) |
| faohshafig | [`faohshafig.png`](faohshafig.png) | 17% | 0.86 | ok |
| faohshass | [`faohshass.png`](faohshass.png) | 22% | 0.85 | ok |
| faohshbmb | [`faohshbmb.png`](faohshbmb.png) | 23% | 0.82 | ok |
| faohshcar | [`faohshcar.png`](faohshcar.png) | 28% | 0.86 | ok |
| faohshcon | [`faohshcon.png`](faohshcon.png) | 11% | 0.85 | ok |
| faohshcrs | [`faohshcrs.png`](faohshcrs.png) | 30% | 0.84 | ok |
| faohshcrv | [`faohshcrv.png`](faohshcrv.png) | 40% | 0.82 | ok |
| faohshdev | [`faohshdev.png`](faohshdev.png) | 17% | 0.82 | ok |
| faohshfig | [`faohshfig.png`](faohshfig.png) | 16% | 0.86 | ok |
| faohshhint | [`faohshhint.png`](faohshhint.png) | 24% | 0.84 | ok |
| faohshint | [`faohshint.png`](faohshint.png) | 25% | 0.84 | ok |
| faohshmnr | [`faohshmnr.png`](faohshmnr.png) | 15% | 0.85 | ok |
| faohshpod | [`faohshpod.png`](faohshpod.png) | 1% | 0.85 | ok |
| faohshsctb | [`faohshsctb.png`](faohshsctb.png) | 26% | 0.85 | ok |
| faohshstl | [`faohshstl.png`](faohshstl.png) | 20% | 0.88 | ok |
| fig02ops1 | [`fig02ops1.png`](fig02ops1.png) | 25% | 0.84 | ok |
| fig03 | [`fig03.png`](fig03.png) | 50% | 0.87 | ok |
| fig04 | [`fig04.png`](fig04.png) | 57% | 0.85 | near-black crush (dark art) |
| fig05 | [`fig05.png`](fig05.png) | 24% | 0.85 | ok |
| fig05ops1 | [`fig05ops1.png`](fig05ops1.png) | 35% | 0.85 | ok |
| fig07 | [`fig07.png`](fig07.png) | 18% | 0.80 | ok |
| fig12 | [`fig12.png`](fig12.png) | 19% | 0.87 | ok |
| fig13 | [`fig13.png`](fig13.png) | 27% | 0.82 | ok |
| fig15 | [`fig15.png`](fig15.png) | 0% | 0.82 | ok |
| fig17 | [`fig17.png`](fig17.png) | 27% | 0.82 | ok |
| fig21 | [`fig21.png`](fig21.png) | 35% | 0.83 | ok |
| fig22 | [`fig22.png`](fig22.png) | 32% | 0.81 | ok |
| fig23 | [`fig23.png`](fig23.png) | 21% | 0.86 | ok |
| fig25 | [`fig25.png`](fig25.png) | 50% | 0.81 | near-black crush (dark art) |
| fig30 | [`fig30.png`](fig30.png) | 43% | 0.88 | ok |
| fig31 | [`fig31.png`](fig31.png) | 40% | 0.85 | ok |
| fig32 | [`fig32.png`](fig32.png) | 50% | 0.89 | ok |
| fig34 | [`fig34.png`](fig34.png) | 45% | 0.86 | ok |
| quizbbrv2 | [`quizbbrv2.png`](quizbbrv2.png) | 12% | 0.85 | ok |
| quizfigv2 | [`quizfigv2.png`](quizfigv2.png) | 25% | 0.85 | ok |
| quizintv2 | [`quizintv2.png`](quizintv2.png) | 12% | 0.87 | ok |
| quizscoutv2 | [`quizscoutv2.png`](quizscoutv2.png) | 17% | 0.84 | ok |
| quizsfv2 | [`quizsfv2.png`](quizsfv2.png) | 14% | 0.85 | ok |
| raincarr | [`raincarr.png`](raincarr.png) | 30% | 0.89 | ok |
| rainfigv2 | [`rainfigv2.png`](rainfigv2.png) | 65% | 0.86 | near-black crush (dark art) |
| rainintv2 | [`rainintv2.png`](rainintv2.png) | 73% | 0.83 | near-black crush (dark art) |
| rainscoutv2 | [`rainscoutv2.png`](rainscoutv2.png) | 29% | 0.84 | ok |
| rainsfv2 | [`rainsfv2.png`](rainsfv2.png) | 63% | 0.84 | near-black crush (dark art) |
| rescuedrone | [`rescuedrone.png`](rescuedrone.png) | 31% | 0.81 | ok |
| ss20 | [`ss20.png`](ss20.png) | 30% | 0.86 | ok |
| tecdev | [`tecdev.png`](tecdev.png) | 47% | 0.84 | ok |
| tfcon | [`tfcon.png`](tfcon.png) | 67% | 0.86 | near-black crush (dark art) |
| tfdron | [`tfdron.png`](tfdron.png) | 30% | 0.82 | ok |
| tffig | [`tffig.png`](tffig.png) | 52% | 0.85 | near-black crush (dark art) |
| tfflagship | [`tfflagship.png`](tfflagship.png) | 67% | 0.85 | near-black crush (dark art) |
| tfmnr | [`tfmnr.png`](tfmnr.png) | 61% | 0.85 | near-black crush (dark art) |
| tfpod | [`tfpod.png`](tfpod.png) | 40% | 0.82 | ok |
| tfsb2 | [`tfsb2.png`](tfsb2.png) | 50% | 0.87 | near-black crush (dark art) |
| tftransport | [`tftransport.png`](tftransport.png) | 95% | 0.86 | near-black crush (dark art) |
| utl01b | [`utl01b.png`](utl01b.png) | 22% | 0.80 | ok |
| utl05 | [`utl05.png`](utl05.png) | 13% | 0.89 | ok |
| utl100 | [`utl100.png`](utl100.png) | 46% | 0.88 | ok |
| utl101 | [`utl101.png`](utl101.png) | 31% | 0.83 | ok |
| utl102 | [`utl102.png`](utl102.png) | 49% | 0.83 | ok |
| utl103 | [`utl103.png`](utl103.png) | 30% | 0.83 | ok |
| utl105 | [`utl105.png`](utl105.png) | 20% | 0.84 | ok |
| utl11 | [`utl11.png`](utl11.png) | 9% | 0.85 | ok |
| utl14a | [`utl14a.png`](utl14a.png) | 27% | 0.86 | ok |
| utl17a | [`utl17a.png`](utl17a.png) | 13% | 0.85 | ok |
| utl18a | [`utl18a.png`](utl18a.png) | 9% | 0.86 | ok |
| utl19 | [`utl19.png`](utl19.png) | 19% | 0.87 | ok |
| utl200 | [`utl200.png`](utl200.png) | 51% | 0.85 | near-black crush (dark art) |
| utl201 | [`utl201.png`](utl201.png) | 53% | 0.84 | near-black crush (dark art) |
| utl202 | [`utl202.png`](utl202.png) | 45% | 0.86 | ok |
| utl203 | [`utl203.png`](utl203.png) | 31% | 0.84 | ok |
| utl205 | [`utl205.png`](utl205.png) | 55% | 0.90 | near-black crush (dark art) |
| utl22 | [`utl22.png`](utl22.png) | 0% | 0.80 | ok |
| utl25 | [`utl25.png`](utl25.png) | 24% | 0.87 | ok |
| utl27b | [`utl27b.png`](utl27b.png) | 22% | 0.86 | ok |
| utl400 | [`utl400.png`](utl400.png) | 24% | 0.89 | ok |
| utl403 | [`utl403.png`](utl403.png) | 61% | 0.81 | near-black crush (dark art) |
| utl666 | [`utl666.png`](utl666.png) | 4% | 0.85 | ok |
| utl90 | [`utl90.png`](utl90.png) | 27% | 0.86 | ok |
| utl91 | [`utl91.png`](utl91.png) | 34% | 0.84 | ok |
| utl92a | [`utl92a.png`](utl92a.png) | 56% | 0.73 | near-black crush (dark art) |
| utl93b | [`utl93b.png`](utl93b.png) | 29% | 0.87 | ok |
| utl94 | [`utl94.png`](utl94.png) | 46% | 0.87 | ok |
| utl95 | [`utl95.png`](utl95.png) | 71% | 0.87 | near-black crush (dark art) |
| utl99 | [`utl99.png`](utl99.png) | 20% | 0.85 | ok |
| wc_beomni | [`wc_beomni.png`](wc_beomni.png) | 41% | 0.88 | ok |
| wc_bifig | [`wc_bifig.png`](wc_bifig.png) | 13% | 0.88 | ok |
| wc_gcbmbx | [`wc_gcbmbx.png`](wc_gcbmbx.png) | 17% | 0.86 | ok |
| wc_gcfig | [`wc_gcfig.png`](wc_gcfig.png) | 20% | 0.86 | ok |
| wc_gcfigx | [`wc_gcfigx.png`](wc_gcfigx.png) | 10% | 0.86 | ok |
| wc_gchttx | [`wc_gchttx.png`](wc_gchttx.png) | 19% | 0.86 | ok |
| wc_gchvy | [`wc_gchvy.png`](wc_gchvy.png) | 42% | 0.77 | ok |
| wc_gchvyx | [`wc_gchvyx.png`](wc_gchvyx.png) | 48% | 0.77 | ok |
| wc_gcint | [`wc_gcint.png`](wc_gcint.png) | 42% | 0.77 | ok |
| wc_gtadv | [`wc_gtadv.png`](wc_gtadv.png) | 48% | 0.84 | ok |
| wc_gtcon | [`wc_gtcon.png`](wc_gtcon.png) | 28% | 0.86 | ok |
| wc_gtfig | [`wc_gtfig.png`](wc_gtfig.png) | 48% | 0.84 | ok |
| wc_gthvy | [`wc_gthvy.png`](wc_gthvy.png) | 25% | 0.84 | ok |
| wc_gtint | [`wc_gtint.png`](wc_gtint.png) | 26% | 0.84 | ok |
| wc_gtmnr | [`wc_gtmnr.png`](wc_gtmnr.png) | 19% | 0.85 | ok |
| wc_gtpod | [`wc_gtpod.png`](wc_gtpod.png) | 37% | 0.75 | ok |
| wc_gtpt | [`wc_gtpt.png`](wc_gtpt.png) | 19% | 0.89 | ok |
| wc_gtsct | [`wc_gtsct.png`](wc_gtsct.png) | 28% | 0.85 | ok |
| wc_gtsf | [`wc_gtsf.png`](wc_gtsf.png) | 18% | 0.79 | ok |
| wc_gtstang | [`wc_gtstang.png`](wc_gtstang.png) | 48% | 0.82 | ok |
| wc_icadv | [`wc_icadv.png`](wc_icadv.png) | 17% | 0.87 | ok |
| wc_icbmb | [`wc_icbmb.png`](wc_icbmb.png) | 19% | 0.86 | ok |
| wc_icfig | [`wc_icfig.png`](wc_icfig.png) | 10% | 0.87 | ok |
| wc_ichvy | [`wc_ichvy.png`](wc_ichvy.png) | 36% | 0.86 | ok |
| wc_icint | [`wc_icint.png`](wc_icint.png) | 34% | 0.86 | ok |
| wc_icsf | [`wc_icsf.png`](wc_icsf.png) | 10% | 0.86 | ok |

Affected hulls (decode-fail or near-black ≥50%): **36**. The rest render solid + shaded.
