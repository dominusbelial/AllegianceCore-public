# P1013 reviewed debug rectangle captures

Actual DesktopGL window backbuffers, captured with Mesa software GL + Xvfb.
Both styles use the production layout and the existing HUD font; the proof
applies blue Classic / teal Modern debug colours. Orange outlines mark the
inline offers. Blank preview/stats/pricing placeholders are intentional: the
shipping renderer is P1014. All 18 PNGs were viewed by the P1013 coding agent.
No external art, personal information or private media appears in these images.

| Viewport / scale | Classic | Modern |
|---|---|---|
| 1280×720 / 1 | [capture](classic-1280x720-scale1.png) | [capture](modern-1280x720-scale1.png) |
| 1600×900 / 1 | [capture](classic-1600x900-scale1.png) | [capture](modern-1600x900-scale1.png) |
| 1920×1080 / 1 | [capture](classic-1920x1080-scale1.png) | [capture](modern-1920x1080-scale1.png) |
| 2560×1440 / 1 | [capture](classic-2560x1440-scale1.png) | [capture](modern-2560x1440-scale1.png) |
| 3840×2160 / 1 | [capture](classic-3840x2160-scale1.png) | [capture](modern-3840x2160-scale1.png) |
| 3440×1440 / 1 | [capture](classic-3440x1440-scale1.png) | [capture](modern-3440x1440-scale1.png) |
| 1280×800 / 1 | [capture](classic-1280x800-scale1.png) | [capture](modern-1280x800-scale1.png) |
| 1280×720 / requested 2, scrolled | [capture](classic-1280x720-scale2-scrolled.png) | [capture](modern-1280x720-scale2-scrolled.png) |
| 3840×2160 / 2, scrolled | [capture](classic-3840x2160-scale2-scrolled.png) | [capture](modern-3840x2160-scale2-scrolled.png) |

At 1280×720 the requested scale of 2 caps to 1.06 to keep all major regions
visible. The 84-pixel footer never scales. Scrolled captures show the final
model hull and the empty-slot offer without hiding the open slot header.

Reproduce with `tools/p1013-layout-proof/README.md`. Each invocation has a
60-second watchdog and exits after three drawn frames.
