# P83 — the real playable client (WinTrek/Allegiance.exe): first successful build, four real blockers found and fixed, one genuine infrastructure limit remaining

## TL;DR

**WinTrek built successfully for the first time in this project's history.**
Every live-tested milestone before tonight (P77/P78/P80 included) only ever
ran through `Wopr.exe`, a headless bot harness that bypasses the lobby
entirely. This task built the actual playable game client
(`Allegiance.vcxproj` → `Allegiance.exe`, source under `src/WinTrek/`) and
pushed it as far as physically possible in this environment: it now boots,
initializes Direct3D9, and fails only because **this VM's virtual display
adapter has no 3D/DirectX capability at all** — a real, well-understood
infrastructure limitation, not a code or protocol gap. The connection code
that would test AllegianceCore's lobby/protocol support was never reached,
because D3D9 init fails first, every time, regardless of anything on the
networking side.

Four real, fixable blockers were found and fixed along the way, all in the
native `Allegiance` repo (local commits only — this repo's `origin` is the
upstream FreeAllegiance project, not a personal fork, same discipline as
P77's `Miner.cs`):

## 1. Build: v143→v145 toolset retarget, legacy DirectX SDK

`Allegiance.vcxproj` (WinTrek) and its full dependency chain (`Engine`,
`effect`, `Ui`, `SoundEngine`, `training`, plus the already-built `Utility`/
`Igc`/`ClintLib`/`Guids`/`ZLibrary`) all targeted `Win32`/`v143` — retargeted
to `v145` (same recipe as every other native build this session). D3DX9 is
deprecated and needed the legacy DirectX SDK (June 2010) — not installed as
a full SDK (the installer isn't reliably silent-automatable and a full
interactive install isn't something this environment can drive), but
**extracted directly** with 7-Zip (`7z x DXSDK_Jun10.exe`) to pull just
`Include/d3dx9.h` and `Lib/x86/d3dx9.lib` — WinTrek's own D3DX9 usage is a
single, small, contained file (`src/engine/VRAMManager.cpp`, texture
loading only). `DXSDK_DIR` set as a machine env var pointing at the
extraction. All 11 projects built clean (`EXIT=0` each), `Allegiance.exe`
linked, 16.9 MB, two harmless pre-existing linker warnings (LNK4075/LNK4099,
SAFESEH/missing-PDB noise unrelated to this task).

## 2. Fix: WinTrek was hard-wired to DirectPlay8, never TCP

`WinTrekClient` (`src/WinTrek/WinTrek.h:76-77`, implementation
`src/WinTrek/trekigc.cpp:2438`) derives from `BaseClient` but never passed
`fUseTcpTransport` in its constructor's initializer list, leaving it at
`BaseClient`'s own default (`false`, i.e. DirectPlay8-only —
`clintlib.h:841`). DirectPlay8 was removed as a first-class Windows
component starting Windows 8; this Windows 11 VM doesn't have it.
`AllegianceInterop`'s own wrapper for WOPR already threads `true` through
from `Configuration.UseTcpTransport` (the mechanism proven working all
session) — `WinTrekClient` just never got the same treatment. Fixed by
adding `BaseClient(true)` to its initializer list.

## 3. Fix (deployment, not code): two missing runtime DLLs

`Allegiance.exe` exited immediately with `STATUS_DLL_NOT_FOUND`
(`-1073741515` / `0xC0000135`). `dumpbin /dependents` against the built
exe found two DLLs genuinely missing from the output directory:
- **`d3dx9_43.dll`** — the SDK gives headers/import libs for *linking*, but
  the actual runtime DLL is a separate redistributable, normally installed
  via `DXSETUP.exe`. Extracted directly from the SDK's own
  `Redist\Jun2010_d3dx9_43_x86.cab` with `expand.exe` (no need to run the
  full redist installer for one DLL) and copied next to `Allegiance.exe`.
- **`steam_api.dll`** — WinTrek's own post-build step (`Allegiance.vcxproj`)
  does `xcopy ..\src\lib\steam\steam_api.dll $(OutDir)..\..\` for the
  `FZDebug` config — that's **two** directories up from `$(OutDir)`
  (`objsv145\FZDebug\WinTrek\`), landing at `objsv145\`, not next to the
  exe. Pre-existing bug in the vcxproj's own post-build path (not something
  this task's toolset retarget introduced — it's a plain relative-path
  assumption that doesn't match this build layout), not fixed at the
  project-file level (out of scope for this task — noted here for whoever
  picks up a real fix); worked around by copying the DLL directly from
  `src/Lib/Steam/steam_api.dll`.

## 4. Fix: no direct-connect path exists in WinTrek's UI at all

Every real UI path in `introscreen.cpp` either joins a lobby-negotiated
session by GUID (`BaseClient::ConnectToServer`'s `ci.guidSession !=
GUID_NULL` branch, `clintlib.cpp:1309-1313`,
`JoinSessionInstance`) or opens a DirectPlay LAN-broadcast session browser
(`OnButtonLAN` → `m_pfindServerPopup`, tied to DirectPlay's own session
enumeration — moot now that transport is TCP). **Nothing in WinTrek's own
UI ever sets `ci.strServer`/`ci.dwPort` directly** — there is no "connect to
this IP" screen, unlike WOPR's own `Configuration.ServerAddress`/
`ServerPort` direct-connect path. Added a minimal, diagnostic-quality
bypass in `IntroScreen::OnTryLogon` (`introscreen.cpp`): when
`ALLEGIANCE_DIRECT_SERVER`/`ALLEGIANCE_DIRECT_PORT` env vars are set, skip
session-GUID join and call `ConnectToServer` with real
`strServer`/`dwPort` and `bStandalonePrivate=false` instead — verified
against `AllegianceInterop/ClientConnection.cpp:160-180`'s own call shape
line-for-line (same `guidSession=GUID_NULL`, same `bStandalonePrivate`
value) to make sure this takes the exact code path already proven working
for WOPR. Also added an auto-trigger in `IntroScreen`'s constructor (same
env var) that calls `OnTryLogon` directly 3 seconds after construction —
there is no click-automation available into this VM's console (no
graphical SPICE client on this box; screenshots via `virsh screenshot` and
key events via `virsh send-key` are the only interaction available), and
WinTrek's UI has no keyboard-only path to a connect screen either, so
autotriggering was the only way to actually exercise the connect code at
all.

## 5. Fix: SteamAPI_Init() blocks forever with an unanswerable dialog

Live `cdb -pv` attach + `~*kb` (all-thread stack dump of the running,
non-crashed process) found the exact hang: the main thread was blocked
inside a **modal `MessageBoxA`** called from `TrekAppImpl::Initialize`
(`trekapp.cpp:321`, resolved via PDB source-line info, not guessed) —
`SteamAPI_Init()` fails (no real Steam client on this VM) and the game pops
`"Steam Client is not running. Please launch Steam and try again."`, then
blocks forever since the dialog is invisible (`virsh screenshot` never
showed it — most likely created on a non-interactive window station, since
the process was launched via an SSH-spawned PowerShell session, not the
VM's interactive console) and nobody can click it. A second, related
check — `SteamAPI_RestartAppIfNecessary` — exits immediately with `-1`
under the same condition, one line earlier (`trekapp.cpp:312-313`), inside
an `#ifndef _DEBUG` guard that turned out to still be active for this
`FZDebug` build configuration (that config name does not itself define the
`_DEBUG` preprocessor macro — a real, mildly surprising fact worth knowing
for any future native diagnostic in this specific config). Both bypassed
under the same `ALLEGIANCE_DIRECT_SERVER` env var check.

## 6a. DXVK attempted, hit a separate, deeper wall (update, same night)

Investigated giving the VM a 3D-capable display per the "no 3D display"
blocker below. Real hardware constraints ruled out the usual fixes: this
host has only one GPU (integrated Intel Arrow Lake-S, needed for the
host's own desktop — no spare GPU for passthrough), and no SR-IOV graphics
support (`/sys/class/drm/card0/device/sriov_totalvfs` doesn't exist on
this kernel/GPU combo, so no safe shared-iGPU option either). `virtio-gpu`
was ruled out as unlikely to help — its Windows guest support is an
OpenGL/2D-compositing path, not a reliable legacy-D3D9 one.

Tried DXVK (D3D9→Vulkan translation, portable drop-in `d3d9.dll`, no
system-wide install) with Mesa's Windows software-Vulkan builds
(`pal1000/mesa-dist-win`), across **four independent combinations**, all
attempted live, all confirmed via `cdb -o` launch-from-start + `kb`:

| DXVK version | Vulkan ICD | Result |
|---|---|---|
| 3.1 (latest) | lavapipe (`lvp`, pure software) | `d3d9.dll!abort()` → `FAST_FAIL_FATAL_APP_EXIT`, in `CD3DDeviceModeData::CD3DDeviceModeData` ← `PromptUserForVideoSettings` |
| 1.10.3 (known-good legacy-title release) | lavapipe | same `abort()`, same call site, `exit(3)` |
| 1.10.3 | Dozen (`dzn`, Vulkan-over-D3D12/WARP — a completely different backend) | **identical** `abort()`, identical call site, `exit(3)` |

All three hit the exact same place regardless of DXVK version or Vulkan
backend — strong evidence this isn't a backend-specific bug (lavapipe vs.
Dozen are unrelated implementations) but either a genuine DXVK-vs-this-
specific-1.09-era-title incompatibility in device/mode enumeration, or a
more basic Vulkan-loader-level issue that neither ICD swap actually
exercised differently. Not further isolated tonight (would need a
standalone Vulkan init test independent of DXVK/the game to tell those
two apart) — a good, bounded first step for whoever picks this back up.

`d3d9.dll` in the WinTrek output directory is currently DXVK 1.10.3's
build (Dozen ICD last selected via `VK_ICD_FILENAMES`) — **not** the
native Windows one. Delete `objsv145\FZDebug\WinTrek\d3d9.dll` to restore
the plain-native-D3D9 baseline (which reproduces the simpler "0 adapters"
result documented below) before any future attempt, so it's clear which
of the two distinct failure modes is being debugged.

## 6b. The original, plain-native-D3D9 blocker: no 3D-capable display in this VM

With all four fixes above, `Allegiance.exe` now runs past every prior
blocker and reaches real Direct3D9 initialization. `debug.log` (verbatim):

```
2026-09-05 22:28:55 Running ALLEGIANCE 1.09.1040158.00.311412
Artpath: C:/Users/domingo/Allegiance/objsv145/FZDebug/WinTrek/Artwork
Command line:

2026-09-05 22:28:55 Logging into steam.
2026-09-05 22:28:55 Steam login complete.
2026-09-05 22:28:55 Log file VideoSettings.log created - Sep 05, 22:28

2026-09-05 22:28:55 DX did not register the SW Rasterizer!
2026-09-05 22:28:55 Created D3D9 interface.
2026-09-05 22:28:55 Found 0 adapters.

2026-09-05 22:28:55
Log closed.
```

(The "Logging into steam"/"Steam login complete" lines are a separate,
harmless, self-contained log message elsewhere in the startup path — not
gated by this task's Steam bypass and not blocking anything; noted for
whoever reads this log next so it isn't mistaken for the bypass having no
effect.)

**`IDirect3D9::GetAdapterCount()` returns 0** — the D3D9 interface itself
constructs fine, but finds no display adapter to enumerate. Checked the
VM's own display device configuration directly (`virsh dumpxml win11`):

```xml
<video>
  <model type='vga' vram='16384' heads='1' primary='yes'/>
</video>
```

Plain QEMU standard VGA emulation — a basic framebuffer device with no
3D/DirectX capability, almost certainly running under Windows' generic
"Microsoft Basic Display Adapter" driver. This is not a code bug and not
an AllegianceCore protocol gap — it's a VM configuration limitation. Real
next step (infrastructure work, not native-repo code): switch the VM's
video model to something 3D-capable — `virtio-gpu` with GL/VirGL
acceleration, `qxl` (limited 2D/3D), or genuine GPU passthrough (VFIO) —
and confirm a real display adapter with DirectX support enumerates before
attempting this again.

## What this means for "is the server usable by a real client"

The gap this task set out to measure turned out to be **larger on the
client-runnability side than on the server-protocol side** — WinTrek never
got far enough to send AllegianceCore a single byte. Every one of tonight's
four fixes was needed just to get the real client past its own startup
sequence; the actual lobby/protocol question (does `AllegianceCore` handle
what a real client sends, direct-connect or otherwise) remains untested
end-to-end, blocked one level earlier than expected. Once the VM has a
3D-capable display, this task's own fixes (TCP transport, direct-connect
bypass, missing DLLs, Steam bypass) should be sufficient to get a real
connection attempt all the way to `AllegianceCore`'s own wire handling —
that would be the natural next milestone.

## State

- Native `Allegiance` repo: all five fixes above are real source changes to
  `src/WinTrek/trekigc.cpp`, `src/WinTrek/introscreen.cpp`, and
  `src/WinTrek/trekapp.cpp` — **not yet committed** (this session's own
  convention: verify live before committing; the "VM has no 3D display"
  blocker means the direct-connect path itself is unverified against a
  real server, so these are left uncommitted pending that verification).
- `AllegianceCore`: byte-for-byte untouched.
- VM state: `DXSDK_DIR` set as a machine env var; `dxsdk_extracted\`,
  `d3dx9_43_extracted\`, `DXSDK_Jun10.exe` (~571 MB) left on
  `C:\Users\domingo\` — safe to delete, or keep for a future WinTrek
  rebuild without re-downloading.
- `d3dx9_43.dll` and `steam_api.dll` copied directly into
  `objsv145\FZDebug\WinTrek\` — these are build-output-directory artifacts,
  not source changes, and will need re-copying after any clean rebuild of
  that directory until the underlying vcxproj post-build path bug (point 3
  above) is fixed properly.
- `objsv145\FZDebug\WinTrek\d3d9.dll` is currently DXVK 1.10.3's build, not
  the native Windows one (see 6a) — delete it to get back to the plain
  native-D3D9 "0 adapters" baseline.
- DXVK/Mesa artifacts left on the VM, safe to delete or reuse:
  `C:\Users\domingo\dxvk-3.1.tar.gz`, `dxvk_extracted\`,
  `dxvk-1.10.3.tar.gz`, `dxvk_1103_extracted\`, `mesa3d.7z`,
  `mesa3d_extracted\`, `vulkan_sw\` (the portable lavapipe + Dozen ICD
  files actually used for the launches above).
