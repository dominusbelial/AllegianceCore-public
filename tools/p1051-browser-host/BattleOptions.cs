// CLI surface belongs only to this loopback proof tool, never ServerConfig.
internal sealed record BattleOptions(string Artwork, string Assets, string Scratch, int Bots, int Seconds, bool Guests = false,
    string? MapGen = null, int MapSeed = 1073)
{
    internal static BattleOptions Parse(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("p1051-browser-host ARTWORK PUBLISHED_WWWROOT SCRATCH [--bots 0..10] [--seconds N] [--guests 0|1] [--map-gen SingleRing|PinWheel|DoubleRing] [--map-seed N]");
        int bots = 0, seconds = 0, mapSeed = 1073; bool guests = false; string? mapGen = null;
        var seen = new HashSet<string>();
        for (int i = 3; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i])) throw new ArgumentException("Invalid battle option.");
            if (args[i] is "--map-gen")
            {
                mapGen = args[i + 1];
                if (mapGen is not ("SingleRing" or "PinWheel" or "DoubleRing"))
                    throw new ArgumentException("--map-gen must be SingleRing, PinWheel or DoubleRing.");
                continue;
            }
            if (!int.TryParse(args[i + 1], out int value)) throw new ArgumentException("Invalid battle option.");
            switch (args[i])
            {
                case "--guests" when value is 0 or 1: guests = value == 1; break;
                case "--bots" when value is >= 0 and <= 10: bots = value; break;
                case "--seconds" when value is >= 1 and <= 21600: seconds = value; break;
                case "--map-seed": mapSeed = value; break;
                default: throw new ArgumentException("--bots must be 0..10; --seconds must be 1..21600; --guests must be 0 or 1; --map-gen SingleRing|PinWheel|DoubleRing; --map-seed N.");
            }
        }
        return new(args[0], args[1], args[2], bots, seconds, guests, mapGen, mapSeed);
    }
}
