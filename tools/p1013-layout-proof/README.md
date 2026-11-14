# P1013 layout geometry proof

This tool opens a real DesktopGL window and draws debug rectangles from the
production `LoadoutV2Layout`, using real PCore001 hull/slot/offer counts and
the existing fonts. It checks touch round trips and measured text widths before
saving its actual backbuffer. It is a geometry proof, not the P1014 renderer.
No server, network connections, purchase rules or production flags are added.

Build the project, then from the repository root:

```sh
LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null xvfb-run -a -s '-screen 0 1920x1080x24' \
  timeout 60s dotnet tools/p1013-layout-proof/bin/Debug/net10.0/p1013-layout-proof.dll \
  .scratch/p1013/classic-1280x720.png classic 1280 720 1
```

Repeat with `modern`. Optional final argument `scrolled` opens the last slot
and scrolls hulls and offers to the end. The numeric scale is the resolved UI
scale supplied by the future renderer; the tool passes it through unchanged.
Set `ALLEGIANCE_ARTWORK_PATH` to the local artwork corpus. The default production
screen is unaffected. The window exits after three drawn frames; use the timeout
above to bound graphics startup failures. Review PNGs before adding them to a
public change set.
