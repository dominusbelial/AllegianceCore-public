using HudGaugeProof;

// P395 — offscreen HUD gauge-layout proof.
// Usage: hud-gauge-proof <outdir>
string outDir = args.Length > 0 ? args[0] : ".";
using var game = new GaugeHarness(outDir);
game.Run();
return 0;
