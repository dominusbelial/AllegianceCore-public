using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Allegiance.Core.Tests")]
[assembly: InternalsVisibleTo("Allegiance.ClientCore.Tests")]
[assembly: InternalsVisibleTo("Allegiance.SinglePlayer")]   // P775 — TrainingHost.CreateDrone announces its drones (trainingmission.cpp:620-623)
[assembly: InternalsVisibleTo("livecombat")]
[assembly: InternalsVisibleTo("chatdemo")]
[assembly: InternalsVisibleTo("vizfight")]
[assembly: InternalsVisibleTo("hullhit-proof")]
[assembly: InternalsVisibleTo("hitsound-proof")]
[assembly: InternalsVisibleTo("shieldflare-proof")]

[assembly: InternalsVisibleTo("receive-proof")]
// P813c — the fault-injection tool reads the reconnect-grace counters and uses
// ShipSpawner to place a deterministic arena (same seam the test suites use).
[assembly: InternalsVisibleTo("reconnect-fault")]
[assembly: InternalsVisibleTo("p817-gotoplan-proof")]
// P853b — the host-editing proof reads the authoritative mission state (the
// second vantage point for the rename + params echo).
[assembly: InternalsVisibleTo("p853b-hostedit-proof")]

// P895 bounded local transport A/B uses the existing deterministic spawn seam.
[assembly: InternalsVisibleTo("p895-transport-proof")]
