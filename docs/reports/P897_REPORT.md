# P897 — automatic client transport

Implemented in the Codex worktree, with no commits or pushes. Auto is the game
client's persisted default. The server still needs its existing optional UDP
listener enabled. Bots, MCP and plain sessions retain their existing defaults.

## Policy

`ClientTransportPolicy` is pure: setting, discovery source, advertised port,
client link and host link produce a protocol preference and a readable reason.

| Setting / facts | Protocol preference |
|---|---|
| TCP, any facts | TCP |
| UDP, any facts, including missing port | UDP with existing TCP fallback |
| Auto, missing/invalid UDP port | TCP |
| Auto, Lan / Multicast / Subnet, both ends wired | TCP |
| Auto, Lan / Multicast / Subnet, either end wireless or unknown | UDP |
| Auto, Lobby / Tailscale / Manual / Favorite, valid UDP port | UDP |

`ALLEGIANCE_TRANSPORT_PROTOCOL` overrides Auto; explicit Settings TCP/UDP wins.
A plain session without an explicit protocol continues using the environment,
then TCP. The table test covers all 3 settings × 7 sources × 4 port states ×
3 client links × 3 host links (756 cases), plus environment precedence tests.

The intro join passes the selected discovery record explicitly, including
source, port and host link, rather than inferring the source from an old scan.
Lobby-to-intro conversion retains the UDP port. Discovery v1 adds only optional
`UdpPort` and `HostLink` fields; old fields/version are unchanged. Missing link
and unrecognized future link strings mean unknown. The server classifies its
route back to each querying client and advertises the actual bound game port
and optional bound UDP port. Heartbeat/lobby contracts are unchanged.

The existing three-second UDP handshake/fallback is unchanged. The post-join
`[monogame]` line records actual transport, selected preference and reason.
Settings show the actual transport after fallback. Session reconnect freezes
and reuses the initial preference and UDP endpoint even after property or
environment changes. There is no mid-match switch. Local Training/Skirmish
explicitly use their existing loopback TCP connection.

## Platform detection evidence

Detection connects a datagram socket **without sending a packet**, reads its
local endpoint, and matches that address to a local adapter. DNS follows the
UDP transport's IPv4-first address selection. All detection failures return
unknown. Loopback, VPNs, ambiguous Unix Ethernet and virtual adapters are not
assumed wired. Linux additionally requires a physical sysfs device before
classifying Ethernet as wired; wireless/phy80211 markers take precedence.

| Platform | Evidence and implementation | Real hardware in this lane |
|---|---|---|
| Windows | .NET stores `IP_ADAPTER_ADDRESSES.type` directly as `NetworkInterfaceType`; Wireless80211 means wireless, Ethernet variants mean wired, other types unknown. [Runtime source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/SystemNetworkInterface.cs#L127-L138). | **Not run**; Claude validates Windows. |
| Linux | .NET assigns the native hardware type. `ARPHRD_ETHER` maps to Ethernet, including Wi-Fi; sysfs distinguishes wireless before accepting physical Ethernet. [Managed source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/LinuxNetworkInterface.cs#L85-L89), [native mapping](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_maphardwaretype.c#L26-L33). | **Run**, Ubuntu 26.04.1, .NET 10.0.11. Four physical Ethernet adapters reported Ethernet. The Wi-Fi adapter also reported **Ethernet**, with `wirelessSysfs=True`. Bridges/veth reported Ethernet without a physical device; the selected default route through a bridge correctly stayed unknown. Loopback stayed unknown. |
| macOS | .NET's BSD path tries `SIOCGIFMEDIA` to distinguish Wi-Fi from Ethernet. Accept an explicit Wireless80211; Ethernet remains **unknown** because failure of that native query can leave Wi-Fi labeled Ethernet. No interface-name guessing. [Native source](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_interfaceaddresses.c#L234-L263), [BSD caller](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/BsdNetworkInterface.cs#L103-L119). | **Not run**; Claude validates Mac. Known-wired classification would need reliable additional platform evidence; Auto safely chooses UDP until then. |
| Android | Native enumeration uses getifaddrs/native hardware types; restricted devices can omit type information (including reported type 0). Accept explicit Wireless80211, otherwise **unknown**, with no Linux sysfs or name heuristic. [Runtime source](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_interfaceaddresses.c#L108-L139), [runtime issue](https://github.com/dotnet/runtime/issues/51984). | **Not run**; Claude validates Android. |

Reproduce local observations without sending a probe to another host:

```sh
dotnet run --project tools/p897-transport-proof -- --detect 192.0.2.1
```

The optional address is used only for an OS route lookup; its value and local
addresses are not printed. No rig, mini, Mac or remote game server was contacted.

## Interface evidence

The existing shared Settings panel persists `Transport` in `hud.json`, with
Auto / TCP / UDP cycled in either direction. Up/Down scrolls the panel so the
last row remains readable at 720p. The setting applies to the next connection;
the same row shows current resolved transport. Hidden sliders cannot receive
clicks while the panel is scrolled.

Both captures use real Game1, a local game server, a real discovery query and
a real UDP session. I opened and visually inspected both PNGs. Both show the
yellow `Multiplayer transport: Auto (next connection); active: UDP` row and
an unobstructed footer. The Classic capture shows radar declutter Off; Modern
shows On, confirming their style-specific preferences while sharing the new
transport row.

- [Classic Settings](P897_assets/settings-classic.png)
- [Modern Settings](P897_assets/settings-modern.png)

Reproduce with isolated preference paths and the ordinary artwork configured:

```sh
export ALLEGIANCE_HUD_SETTINGS="$PWD/.scratch/p897/proof-hud.json"
export ALLEGIANCE_GRAPHICS_SETTINGS="$PWD/.scratch/p897/proof-graphics.json"
dotnet run --project tools/p897-transport-proof -- Classic output-classic.png
dotnet run --project tools/p897-transport-proof -- Modern output-modern.png
```

New media totals under 150 KiB and remains for Domingo's review before any
future push. This lane did not commit or push it.

## Validation

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 98 tool projects |
| Server `linux-x64`, self-contained Native AOT publish | PASS, no warnings or IL2026/IL3050 |
| New P897 tests, isolated 3× | PASS, 774/774 each time |
| Existing P895 UDP/metadata/overhead + P813c reconnect suites, 3× | PASS, 46/46 each time; no transport tests changed |
| Affected Settings/HUD tests, 3× | PASS, four cases each time, including the isolated profile test |
| One Fast tier | 12,679 executions: 12,662 pass / 14 fail / 3 optional skips; one timing violation; details below |
| Showcase never-parks 5× | PASS, 5/5 (3m49s / 3m45s / 3m48s / 3m38s / 6m17s) |
| Full training Fast + Live, artwork, 3× | PASS, ClientCore 80/80 and MonoGame 67/67 in every repetition, no skips; MonoGame runs 10m32s / 10m27s / 10m28s |
| Glossary links / parity scorecard / Compose config / diff whitespace | PASS |

The Fast sweep remains recorded as failed; it was run exactly once. Two stale
Settings row-count assertions were updated from 21 to 22 and pass 3/3 afterward.
The other failures are recorded with exact three-run isolation results in
[the flake ledger](../TEST_FLAKES.md#p897-codex-gate--2026-10-30): the profile
write needs a writable config directory; five path fixtures inherit `local.env`
from worktree-local scratch (all nine pass 3/3 via a logical alias to the same
scratch); death/respawn, grace-removal, side-seat and timer boundaries pass 3/3.
The full-session chat test is intermittent (pass/fail/pass). The previously
ledgered wingman-defend null-target failure remains 3/3 reproducible. The wobble
test passed but took 10.597 s in the sweep; isolated repeats take
9.742 / 9.738 / 9.719 s. NPC, chat, path resolver and timing thresholds were
not changed for this task.

[Compact validation results](P897_assets/validation.json) record the suite counts,
failures and three-run isolation outcomes. All five showcase runs and all six
training project runs passed; training includes the Live lessons and Nanite.

[Raw local evidence](../../.scratch/p897/) remains gitignored in the required
worktree scratch directory. The first build needed a writable NuGet HTTP cache;
subsequent builds use the task-local cache. Temporary AOT binaries are removed
after validation; build/publish logs and TRX are retained.

## Native intent

**Design:** native chooses the DirectPlay IP provider at
`_Utility/DirectPlayTransport.cpp:208,270,287` (line numbers re-grepped in the
read-only native tree). Its UDP delivery intent is retained by the existing
transport; P897 adds a modern preference policy, not a new native gameplay
rule. The existing P895 IP-provider parity row now links this executable client
path and test without claiming that native had an Auto/TCP setting.

**Era constraint:** DirectPlay's single IP provider offered no comparable
persisted Auto/TCP/UDP preference or modern route/interface classification.

**Bug versus adaptation:** Unix reporting Wi-Fi as Ethernet is a platform
classification limitation, not an Allegiance gameplay bug. Unknown is explicit
and selects UDP when available. TCP on a known wired LAN and UDP on lossy or
uncertain paths implement the requested modern adaptation; bounded TCP fallback
preserves connectivity. A better future macOS/Android implementation can consume
reliable platform interface facts rather than guessing from adapter names.
