# P287 asset-name manifest: imported Meshy ship + missile models

All 75 assets live here in `Content/Models/*.fbx`, registered in `Content/Content.mgcb` with
`FbxImporter` + `ModelProcessor` (same params as the `bios_scout_upscaled`/P286 precedent),
converted GLB->FBX via headless Blender 5.2.2 (binary FBX, axis_forward=-Z / axis_up=Y,
materials stripped, UVs+normals kept, triangulated) and decimated to a uniform ~20k
triangle cap (P286 precedent: dense Meshy meshes are impractical at full res — some raw
GLBs exceed 600k tris). Each is loaded with `Content.Load<XnaModel>("Models/<asset>")`.

Counts: 77 ships + 3 missiles = 80 models across 9 factions.

| asset | faction | class | kind | tris |
|---|---|---|---|---|
| belters-bomber | Belters | bomber | ship | 19999 |
| belters-fig | Belters | fig | ship | 19999 |
| belters-scout | Belters | scout | ship | 20000 |
| bios-bomber | Bios | bomber | ship | 20000 |
| bios-carrier | Bios | carrier | ship | 20000 |
| bios-constructor | Bios | constructor | ship | 20000 |
| bios-corvette | Bios | corvette | ship | 20000 |
| bios-cruiser | Bios | cruiser | ship | 20000 |
| bios-destroyer | Bios | destroyer | ship | 19999 |
| bios-devastator | Bios | devastator | ship | 20000 |
| bios-ejectpod | Bios | ejectpod | ship | 20000 |
| bios-fighter | Bios | fighter | ship | 20000 |
| bios-interceptor | Bios | interceptor | ship | 20000 |
| bios-miner | Bios | miner | ship | 20000 |
| bios-stealthfighter | Bios | stealthfighter | ship | 20000 |
| bios-trooptransport | Bios | trooptransport | ship | 20000 |
| dreg-bbr2 | Dreghklar Empire | bbr2 | ship | 20000 |
| dreg-carrier | Dreghklar Empire | carrier | ship | 20000 |
| dreg-corvette | Dreghklar Empire | corvette | ship | 20000 |
| dreg-fig2 | Dreghklar Empire | fig2 | ship | 19999 |
| dreg-frigate | Dreghklar Empire | frigate | ship | 20000 |
| dreg-scout2 | Dreghklar Empire | scout2 | ship | 20000 |
| dreg-stealthfighter | Dreghklar Empire | stealthfighter | ship | 19999 |
| dreg-trooptransport | Dreghklar Empire | trooptransport | ship | 19999 |
| gt-ass | Ga'Taraan Federation | ass | ship | 20000 |
| gt-bomber | Ga'Taraan Federation | bomber | ship | 20000 |
| gt-carrier | Ga'Taraan Federation | carrier | ship | 19999 |
| gt-con | Ga'Taraan Federation | con | ship | 19999 |
| gt-cruiser | Ga'Taraan Federation | cruiser | ship | 20000 |
| gt-dev | Ga'Taraan Federation | dev | ship | 19999 |
| gt-fighter | Ga'Taraan Federation | fighter | ship | 19999 |
| gt-hod | Ga'Taraan Federation | hod | ship | 19999 |
| gt-int | Ga'Taraan Federation | int | ship | 20000 |
| gt-miner | Ga'Taraan Federation | miner | ship | 20000 |
| gt-mustang | Ga'Taraan Federation | mustang | ship | 20000 |
| gt-pod | Ga'Taraan Federation | pod | ship | 20000 |
| gt-rd | Ga'Taraan Federation | rd | ship | 20000 |
| gt-scout | Ga'Taraan Federation | scout | ship | 20000 |
| gt-sf | Ga'Taraan Federation | sf | ship | 20000 |
| gt-tt | Ga'Taraan Federation | tt | ship | 20000 |
| giga-bomber | GigaCorp | bomber | ship | 19999 |
| giga-fighter | GigaCorp | fighter | ship | 20000 |
| giga-interceptor | GigaCorp | interceptor | ship | 20000 |
| giga-scout | GigaCorp | scout | ship | 20000 |
| ic-bomber | Iron Coalition | bomber | ship | 20000 |
| ic-carrier | Iron Coalition | carrier | ship | 20000 |
| ic-constructor | Iron Coalition | constructor | ship | 20000 |
| ic-corvette | Iron Coalition | corvette | ship | 20000 |
| ic-cruiser | Iron Coalition | cruiser | ship | 20000 |
| ic-dev | Iron Coalition | dev | ship | 19999 |
| ic-devastator | Iron Coalition | devastator | ship | 20000 |
| ic-ejectpod | Iron Coalition | ejectpod | ship | 20000 |
| ic-fighter | Iron Coalition | fighter | ship | 20000 |
| ic-frigate | Iron Coalition | frigate | ship | 20000 |
| ic-int | Iron Coalition | int | ship | 20000 |
| ic-interceptor | Iron Coalition | interceptor | ship | 20000 |
| ic-miner | Iron Coalition | miner | ship | 19999 |
| ic-scout | Iron Coalition | scout | ship | 20000 |
| ic-stealthfighter | Iron Coalition | stealthfighter | ship | 19999 |
| ic-trooptransport | Iron Coalition | trooptransport | ship | 20000 |
| oh-bomber | Omicron Hive | bomber | ship | 19999 |
| oh-fighter | Omicron Hive | fighter | ship | 20000 |
| oh-scout | Omicron Hive | scout | ship | 19999 |
| rix-ass | Rixian Unity | ass | ship | 20000 |
| rix-bomber | Rixian Unity | bomber | ship | 20000 |
| rix-carrier | Rixian Unity | carrier | ship | 19999 |
| rix-con | Rixian Unity | con | ship | 20000 |
| rix-cruiser | Rixian Unity | cruiser | ship | 20000 |
| rix-fighter | Rixian Unity | fighter | ship | 20000 |
| rix-int | Rixian Unity | int | ship | 20000 |
| rix-miner | Rixian Unity | miner | ship | 20000 |
| rix-pod | Rixian Unity | pod | ship | 20000 |
| rix-scout | Rixian Unity | scout | ship | 20000 |
| rix-sf | Rixian Unity | sf | ship | 20000 |
| rix-tt | Rixian Unity | tt | ship | 20000 |
| tf-bomber | Technoflux | bomber | ship | 20000 |
| tf-fig | Technoflux | fig | ship | 19999 |
| tf-scout | Technoflux | scout | ship | 20000 |
| artifact | native .mdl | artifact | missile | 20000 |
| mis05 | native .mdl | mis05 | missile | 20000 |
| mis400 | native .mdl | mis400 | missile | 20000 |
