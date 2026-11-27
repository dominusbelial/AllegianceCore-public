#r "Allegiance.Core.dll"
#r "Allegiance.ClientCore.dll"
#r "MonoGame.Framework.dll"
#r "Allegiance.SinglePlayer.dll"
#r "Allegiance.ClientCore.Game.dll"
open System
open System.Reflection
open System.Diagnostics
open Allegiance.ClientCore.MonoGame
open Allegiance.ClientCore.MonoGame.Rendering.Skins
open Allegiance.ClientCore.Replay
open Allegiance.ClientCore.Session
open Allegiance.Core.Wire
open Allegiance.Core.Collision
open Allegiance.SinglePlayer.Benchmark

// CLIENT-R1 rendered slice. Invoke with the SDK FSI host and the desktop
// Debug output on its assembly search path (see the P1092 report).
let run skin busy =
    let bin = Environment.GetEnvironmentVariable("CLIENTBIN")
    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", bin + "/")
    let flags = BindingFlags.Public ||| BindingFlags.NonPublic
    // FSI is the entry assembly. Resolve title-relative art against the client output.
    let title = typeof<Microsoft.Xna.Framework.TitleContainer>
    for f in title.GetFields(flags ||| BindingFlags.Static) do
        if f.FieldType = typeof<string> && f.Name.Contains("Location") then f.SetValue(null, bin)
    let command = BenchmarkCommand.Parse([|"--benchmark"; "replay:data/benchmarks/battle.alr"|])
    let options = Game1.LaunchOptions(Intro=true, WindowWidth=Nullable 960, WindowHeight=Nullable 540, Benchmark=command)
    use g = new Game1(options)
    g.Content.RootDirectory <- IO.Path.Combine(bin, "Content")
    for i in 1..4 do g.RunOneFrame()
    let field name = typeof<Game1>.GetField(name, flags ||| BindingFlags.Instance).GetValue(g)
    let skins = field "_hudSkins" :?> HudSkinSwitcher
    skins.Request(skin) |> ignore
    skins.ApplyPending() |> ignore
    let session = field "_session" :?> ConnectionSession
    let replay = field "_benchReplay" :?> BattleReplay
    while replay.Frame < 6500 do replay.Advance(session)
    let world = session.World
    let own = world.Ships.[world.MyShipId.Value]
    let pose = own.Latest
    let sector = replay.ViewedSector.GetValueOrDefault(own.SectorId)
    let position i =
        pose.Position + Vector3(float32 (60 * (i % 8 - 4)), float32 (45 * (i / 8 % 6 - 3)),
                                float32 (-300 - 45 * (i % 5)))
    if busy then
        for i in 0..47 do
            let id = int16 (19000 + i)
            world.Apply(ExportShip(ShipId=id, SideId=int16 (i % 2), HullId=own.HullId, CharacterName=sprintf "Ship%02d" i))
            world.Apply(ShipStatusMessage(ShipId=id, HullId=own.HullId, SectorId=sector, StationId= -1s, ParentId= -1s))
            let update = ServerShipUpdate.FromValues(id, pose.StateM, pose.Controls, pose.Time,
                                                     position i, pose.Orientation, pose.Velocity, pose.TurnRates,
                                                     0.9f, 0.9f, 1f, 1f, 1f, 0f, 0.9f)
            world.Apply(SingleShipUpdate(Ship=update, OidTarget=id))
            world.Ships.[id].GetType().GetProperty("PoseSectorId").SetValue(world.Ships.[id], sector)
    for i in 1..60 do g.RunOneFrame()
    if busy then
        for i in 0..47 do
            for j in 0..1 do
                world.Apply(ProjectileInstance(Position=position i, LauncherId=int16 (19000 + i),
                                               Velocity=Vector3(0f, 0f, -12f), Forward=Vector3(0f, 0f, -1f),
                                               Lifespan=12f))
        for i in [0;12;25;42] do
            world.Apply(ExplosionEvent(Type=int Allegiance.Core.Sim.ExplosionType.SmallShip,ClusterId=sector,Position=position i,Scale=1f))
    let bolts = field "_drawBolts" :?> System.Collections.ICollection
    let blasts = field "_sceneExplosions" :?> System.Collections.ICollection
    let mutable maxBolts, maxBlasts = 0, 0
    let times = Array.zeroCreate<float> 180
    let telemetry = field "_frameTelemetry" :?> Allegiance.ClientCore.MonoGame.Diagnostics.ClientFrameTelemetry
    let first = telemetry.FrameId + 1L
    // The timed loop only runs frames and observes the gathered counts.
    let gc = GC.CollectionCount 0
    let before = GC.GetAllocatedBytesForCurrentThread()
    for i in 0..179 do
        let t = Stopwatch.GetTimestamp()
        g.RunOneFrame()
        times.[i] <- Stopwatch.GetElapsedTime(t).TotalMilliseconds
        maxBolts <- max maxBolts bolts.Count
        maxBlasts <- max maxBlasts blasts.Count
    let bytes = GC.GetAllocatedBytesForCurrentThread() - before
    let collections = GC.CollectionCount(0) - gc
    let count name = (field name :?> System.Collections.ICollection).Count
    let samples = telemetry.GetType().GetField("_samples",flags ||| BindingFlags.Instance).GetValue(telemetry) :?> Array
    let mutable draws = 0L
    for i in first..telemetry.FrameId do
        let s = samples.GetValue(int i % samples.Length)
        draws <- draws + (s.GetType().GetField("Draws").GetValue(s) :?> int64)
    Array.sortInPlace times
    let poses = count "_poses"
    let contacts = count "_drawRadarContacts"
    let observed = world.Ships.Count, poses, contacts, maxBolts, maxBlasts
    let expected = if busy then (66, 54, 68, 96, 4) else (18, 6, 20, 0, 0)
    if skins.Active.Definition.Id <> skin || observed <> expected || replay.Frame <> 6740 then
        failwithf "Replay fixture mismatch: skin=%s entities=%A frame=%d" skins.Active.Definition.Id observed replay.Frame
    printfn "RESULT %s %b %.1f %d %.3f %.3f %.3f ships=%d poses=%d contacts=%d bolts=%d blasts=%d draws=%.2f frame=%d"
        skin busy (float bytes / 180.) collections (Array.average times) times.[170] times.[178]
        world.Ships.Count poses contacts maxBolts maxBlasts (float draws / 180.) replay.Frame

match fsi.CommandLineArgs |> Array.tail with
| [| ("modern" | "classic") as skin; ("modest" | "busy") as scene |] -> run skin (scene = "busy")
| _ -> failwith "Expected: <modern|classic> <modest|busy>"
