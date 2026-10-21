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
