using HudPolishProof;

// P409 — offscreen HUD polish-batch live proof (Q1/Q2/Q3).
// Usage: hud-polish-proof <outdir>
string outDir = args.Length > 0 ? args[0] : ".";
using var game = new HUDPolishHarness(outDir);
game.Run();
return game.Failures == 0 ? 0 : 1;
