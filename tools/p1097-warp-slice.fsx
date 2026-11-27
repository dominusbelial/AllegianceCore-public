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
open Allegiance.SinglePlayer.Benchmark

// Linux thread CPU clock and DesktopGL replay fixture. Run from the checkout with
// the matching desktop binary directory on the FSI search path (see the P1097 report).
[<System.Runtime.InteropServices.DllImport("libc")>]
extern int clock_gettime(int clock, int64[] time)
let stamp = Array.zeroCreate<int64> 2
let threadMs () =
    if clock_gettime(3, stamp) <> 0 then failwith "thread clock unavailable"
    float stamp.[0]*1000. + float stamp.[1]/1e6

let run skin =
    let bin = Environment.GetEnvironmentVariable("CLIENTBIN")
    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", bin + "/")
    let flags = BindingFlags.Public ||| BindingFlags.NonPublic
    // FSI is the entry assembly. Resolve title-relative art against the client output.
    let title = typeof<Microsoft.Xna.Framework.TitleContainer>
    for f in title.GetFields(flags ||| BindingFlags.Static) do
        if f.FieldType = typeof<string> && f.Name.Contains("Location") then f.SetValue(null, bin)
    let command = BenchmarkCommand.Parse([|"--benchmark"; "replay:data/benchmarks/battle.alr"|])
    let options = Game1.LaunchOptions(Intro=true, WindowWidth=Nullable 960, WindowHeight=Nullable 540, Benchmark=command)
    let visibility = fsi.CommandLineArgs.Length = 3
    let options =
        if visibility then
            let layer = Assets.DirectoryAssetSource(fsi.CommandLineArgs.[2])
            let source = Assets.LayeredAssetSource(layer, Assets.AssetSources.Game)
            Game1.LaunchOptions(Intro=true, WindowWidth=Nullable 960, WindowHeight=Nullable 540,
                                Benchmark=command, AssetSource=source)
        else options
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
    let inspect (owner: obj) name = owner.GetType().GetField(name, flags ||| BindingFlags.Instance).GetValue(owner)
    let cache (owner:obj) = inspect owner "_textures" :?> System.Collections.Generic.Dictionary<string,Microsoft.Xna.Framework.Graphics.Texture2D>
    let rocks = cache (field "_nativeAsteroidRenderer")
    let planets = cache (inspect (field "_blackSky") "_planet")
    let gpuBytes () =
        let size (t: Microsoft.Xna.Framework.Graphics.Texture2D) = 4L * int64 t.Width * int64 t.Height
        let resident = (rocks.Values |> Seq.sumBy size) + (planets.Values |> Seq.sumBy size)
        let prep = typeof<Game1>.GetField("_sectorTextures", flags ||| BindingFlags.Instance)
        if isNull prep || isNull (prep.GetValue(g)) then resident else
        let jobs = inspect (prep.GetValue(g)) "_jobs" :?> System.Collections.IEnumerable
        resident + (jobs |> Seq.cast<obj> |> Seq.sumBy(fun job ->
            match inspect job "Partial" with
            | :? Microsoft.Xna.Framework.Graphics.Texture2D as t -> size t
            | _ -> 0L))

    let set name value = typeof<Game1>.GetField(name, flags ||| BindingFlags.Instance).SetValue(g,value)
    let seed sector =
        typeof<Game1>.GetMethod("SeedSectorWarmup", flags ||| BindingFlags.Instance).Invoke(g,[|box sector;box true|]) |> ignore
        (field "_warmQueue" :?> Allegiance.ClientCore.MonoGame.Rendering.SectorWarmupQueue).Promote(sector) |> ignore
    let view sector = typeof<BattleReplay>.GetProperty("ViewedSector").SetValue(replay,box sector)
    // Isolate the destination assets. Drive the same approach admission point as the aleph scan.
    set "_alephGlimpse" false
    let frame sector =
        set "_warmNextApproachScan" Int64.MaxValue
        view sector
        g.RunOneFrame()
        if (unbox<int16> (field "_benchViewSector")) <> sector then failwith "Replay changed sector"
    for i in 1..40 do frame 1050s
    let capture scenario index =
        let pixels = Array.zeroCreate<byte> (960*540*4)
        g.GraphicsDevice.GetBackBufferData(pixels)
        if pixels |> Array.forall ((=) pixels.[0]) then failwith "Empty backbuffer"
        let hash = Convert.ToHexString(Security.Cryptography.SHA256.HashData(pixels))
        printfn "PIXELS %s %s %d %s" skin scenario index hash
    let window name dest interrupt =
        let times = Array.zeroCreate<float> 120
        let bytes = Array.zeroCreate<int64> times.Length
        let cpu = Array.zeroCreate<float> times.Length
        use process = Process.GetCurrentProcess()
        let mutable rss = 0L
        let mutable peak = gpuBytes()
        for i in 0..119 do
            let b = GC.GetAllocatedBytesForCurrentThread()
            let c = threadMs()
            let t = Stopwatch.GetTimestamp()
            if i = 0 then seed (if interrupt then 1054s else dest)
            if interrupt && i = 1 then seed dest
            let arrival = if interrupt then 2 else 80
            let sector = if i < arrival then 1050s else dest
            frame sector
            times.[i] <- Stopwatch.GetElapsedTime(t).TotalMilliseconds
            cpu.[i] <- threadMs() - c
            bytes.[i] <- GC.GetAllocatedBytesForCurrentThread()-b
            peak <- max peak (gpuBytes())
            process.Refresh()
            rss <- max rss process.WorkingSet64
            if i = arrival then printfn "ARRIVAL %s %.3f" name times.[i]
            if visibility || i = arrival || i = arrival+1 then capture name (i-arrival)
        let sorted = times |> Array.sort
        printfn "WINDOW %s %s p99=%.3f max=%.3f cpu=%.1f bytes=%d peak=%d rss=%d worstIndex=%d" skin name sorted.[118] sorted.[119] (Array.sum cpu) (Array.sum bytes) peak rss (times |> Array.findIndex ((=) sorted.[119]))
    window "cold" 1052s false
    for i in 1..8 do frame 1050s
    window "revisit" 1052s false
    for i in 1..8 do frame 1050s
    window "interrupted" 1055s true
    let hashes = field "_benchHashes" :?> System.Collections.Generic.List<string>
    printfn "STATE %d %s" hashes.Count (Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.ASCII.GetBytes(String.Concat(hashes)))))

run fsi.CommandLineArgs.[1]
