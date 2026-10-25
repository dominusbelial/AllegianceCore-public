using Allegiance.Core.Model;

namespace Allegiance.Server;

/// <summary>Creation-time NA fill: Igc/igc.h:1280-1281 initializes rgCivID to NA;
/// FedSrv/fsmission.cpp:175-198 draws by swap-remove and refills on exhaustion.
/// P825 uses the lobby's legal pool (WinTrek/teamscreen.cpp:897-916) and excludes
/// configured fixed sides before each refill, as required by the match config.</summary>
internal static class MatchCivilizations
{
    internal static void Validate(GameConfig game, StaticCore core, bool research = false)
    {
        if (game.Teams < 1 || game.Teams > short.MaxValue)
            throw new InvalidDataException($"game '{game.Name}': teams must be between 1 and {short.MaxValue}");
        if (game.CivIds is null || game.CivIds.Length < game.Teams)
            throw new InvalidDataException($"game '{game.Name}': civIds requires at least one entry per team (null or -1 = random)");
        for (int side = 0; side < game.Teams; side++)
        {
            int id = game.CivIds[side];
            if (id == -1) continue;
            if (id < 0 || id > short.MaxValue)
                throw new InvalidDataException($"game '{game.Name}' side {side}: civilization ID {id} is out of range; use null or -1 for random");
            if (!core.Civilizations.ContainsKey((short)id))
            {
                string context = $"game '{game.Name}' side {side} civ {id}: ";
                string detail = $"unknown civilization ID {id}; use null or -1 for random";
                if (research) throw new InvalidDataException(context + "no valid research plan: " + detail);
                // Preserve the fixed, non-research Mission.Assemble error contract.
                throw new InvalidOperationException(context + detail);
            }
        }
        if (game.CivIds.Take(game.Teams).Contains(-1) && Pool(game, core).Length == 0)
            throw new InvalidDataException($"game '{game.Name}': no legal random civilizations remain after excluding fixed sides");
    }

    private static int[] Pool(GameConfig game, StaticCore core) => Server.LegalLobbyCivs(core)
        .Select(c => (int)c.CivilizationId).Except(game.CivIds.Take(game.Teams).Where(id => id >= 0)).ToArray();

    internal static int[] Resolve(GameConfig game, StaticCore core, int seed)
    {
        Validate(game, core);
        var result = game.CivIds.Take(game.Teams).ToArray();
        var pool = Pool(game, core);
        var remaining = (int[])pool.Clone();
        var random = new Random(seed);
        int count = remaining.Length;
        for (int side = 0; side < result.Length; side++)
        {
            if (result[side] != -1) continue;
            int index = count == 1 ? 0 : random.Next(count);
            result[side] = remaining[index];
            remaining[index] = remaining[--count];
            if (count == 0) { pool.CopyTo(remaining, 0); count = pool.Length; }
        }
        return result;
    }
}
