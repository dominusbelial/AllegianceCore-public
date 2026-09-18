// P256 — internal test seams. The in-repo MonoGame test project and the /tmp
// live-verify harness both reach the headless candidate-ordering core
// (Game1.SelectCandidates + its support types) to prove the autopilot-target
// priority tiers against a real server/client session. Mirrors the existing
// InternalsVisibleTo usage on Allegiance.Server (Properties/AssemblyInfo.cs).
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Allegiance.ClientCore.MonoGame.Tests")]
[assembly: InternalsVisibleTo("P256Verify")]
