using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// P330 stage-4 Server.cs partial split: move ship-command/docking/combat methods
// verbatim (Roslyn FullSpan) into three new partial-class files.
// Usage: serversplit <src/Allegiance.Server dir>
class Program
{
    static readonly string[] ShipCommands = {
        "HandleViewCluster","HandleBoardShip","HandleActiveTurretUpdate","HandlePromote",
        "HandleSwapPart","HandleReload","HandleDropPart","HandleFireExpendable","HandleFireMissile",
        "HandleSetWingId","HandleReqPingData","HandleOrderChange","ApplyCommandSlot","HandleTreasureAck",
        "ApplyLoadout","SetBuilderTargetOnShip","CanMount","MountCombatWeapon"
    };
    static readonly string[] Docking = {
        "PerformBoard","PerformLeave","HandleRipcordRequest","RequestRipcord","RequestRipcordCore",
        "OnNpcRequestRipcord","SendSectorWide","PerformRipcordTeleport","PerformRescue","PerformStationRescue",
        "PerformTreasurePickup","BroadcastTreasureDestroyed","BroadcastGainFlag","BroadcastBuildingEffectDestroyed",
        "BroadcastBuildingEffectCreated","PerformRelaunch","ShipRadius","MoveShipToSector","UpdatePeerSectorSnapshot",
        "ChooseTurretSlot","ShipValue","StationSellsHull","FindSameSideShip","FindShipAnySide","AllocateShipId",
        "IsShipIdLive","SendBoardNack","TryResolveViewTarget","SeenByRequesterSide","RepositionForLaunch",
        "UndockShipState","PerformUndock","PerformTeleport"
    };
    static readonly string[] Combat = {
        "BroadcastProjectileInstance","BroadcastFireMissile","BroadcastMissileDestroyed","BroadcastLoadoutChange",
        "BroadcastExportMine","BroadcastMineDestroyed","SendExplosionEvent","BroadcastExportProbe",
        "BroadcastProbeDestroyed","BroadcastWarpBomb","BroadcastAsteroidDestroyed","BroadcastStationDestroyed",
        "BroadcastStationCapture","OnVictoryByStationsComputed","OnProsperityArtifactComputed","OnFlagsKillsComputed",
        "BroadcastKillShip","FinalizeDeadShip","FindFriendlyStation","EjectSurvivor","OnMine","OnShipWarped","OnShipDocked"
    };

    const string HEADER_COMMON =
        "using System.Diagnostics;\n" +
        "using System.Runtime.InteropServices;\n" +
        "using Allegiance.Core;\n" +
        "using Allegiance.Core.Collision;\n" +
        "using Allegiance.Core.Model;\n" +
        "using Allegiance.Core.Sim;\n" +
        "using Allegiance.Core.Transport;\n" +
        "using Allegiance.Core.Wire;\n";

    const string LOADOUT_ALIAS =
        "// Disambiguate the LoadoutChange KIND enum (Sim) from the Allegiance.Core.Wire.LoadoutChange\n" +
        "// codec — both are in scope here; the enum is the natural meaning in the\n" +
        "// gameplay broadcast handler, the wire codec is reached as Allegiance.Core.Wire.LoadoutChange.\n" +
        "using LoadoutChange = Allegiance.Core.Sim.LoadoutChange;\n";

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "verify")
        {
            Verify(args);
            return;
        }
        string dir = args[0]; // src/Allegiance.Server dir
        string serverPath = Path.Combine(dir, "Server.cs");
        string text = File.ReadAllText(serverPath);
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetRoot();

        var decls = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var byName = decls.GroupBy(m => m.Identifier.Text).ToDictionary(g => g.Key, g => g.ToList());

        var targets = new (string Group, string File, string[] Names)[] {
            ("ShipCommands", "Server.ShipCommands.cs", ShipCommands),
            ("Docking", "Server.Docking.cs", Docking),
            ("Combat", "Server.Combat.cs", Combat),
        };

        var assignments = new List<(string Group, string File, MethodDeclarationSyntax Method, TextSpan Span)>();
        foreach (var t in targets)
        {
            foreach (var name in t.Names)
            {
                if (!byName.TryGetValue(name, out var list))
                    throw new Exception($"METHOD NOT FOUND in Server.cs: {name}");
                if (list.Count != 1)
                    throw new Exception($"METHOD NOT UNIQUE in Server.cs: {name} ({list.Count} decls)");
                var m = list[0];
                assignments.Add((t.Group, t.File, m, m.FullSpan));
            }
        }

        // sort by source position; check disjoint
        assignments = assignments.OrderBy(a => a.Span.Start).ToList();
        for (int i = 1; i < assignments.Count; i++)
            if (assignments[i-1].Span.End > assignments[i].Span.Start)
                throw new Exception($"OVERLAPPING spans: {assignments[i-1].Method.Identifier.Text} & {assignments[i].Method.Identifier.Text}");

        // ---- rebuild Server.cs without the moved spans ----
        var sbServer = new StringBuilder();
        int pos = 0;
        foreach (var a in assignments)
        {
            sbServer.Append(text, pos, a.Span.Start - pos);
            pos = a.Span.End;
        }
        sbServer.Append(text, pos, text.Length - pos);

        File.WriteAllText(serverPath, sbServer.ToString());

        // ---- write the three partial files ----
        foreach (var t in targets)
        {
            var members = assignments.Where(a => a.Group == t.Group).OrderBy(a => a.Span.Start).ToList();
            var sw = new StringBuilder();
            sw.Append(HEADER_COMMON);
            if (t.Group == "Combat")
                sw.Append(LOADOUT_ALIAS);
            sw.Append("\nnamespace Allegiance.Server;\n\n");
            sw.Append(SummaryComment(t.Group));
            sw.Append("public sealed partial class Server\n{\n");
            foreach (var m in members)
            {
                string methodText = text.Substring(m.Span.Start, m.Span.Length);
                // strip leading blank lines only (trivia) so the first method sits
                // flush under the class brace; body bytes stay identical.
                string trimmed = methodText.TrimStart('\r','\n');
                sw.Append("\n");
                sw.Append(trimmed);
            }
            sw.Append("\n}\n");
            File.WriteAllText(Path.Combine(dir, t.File), sw.ToString());
        }

        // ---- census report ----
        Console.WriteLine("MOVED: " + assignments.Count);
        foreach (var t in targets)
            Console.WriteLine($"  {t.File}: {assignments.Count(a => a.Group == t.Group)} methods");
        var manifest = assignments.Select(a => $"{a.Group}\t{a.Method.Identifier.Text}\t{a.Span.Start}\t{a.Span.Length}").ToList();
        File.WriteAllLines(Path.Combine(dir, ".p330_manifest.tsv"), manifest);
        Console.WriteLine("DONE. Server.cs shrunk by " + assignments.Sum(a=>a.Span.Length) + " chars.");
    }

    // verify <originalServer.cs> <newDir>
    // Compares each target method's declaration text (leading/trailing ws trimmed)
    // between the git-HEAD original Server.cs and the new partial files.
    static void Verify(string[] args)
    {
        string origPath = args[1];
        string newDir = args[2];
        var origText = File.ReadAllText(origPath);
        var origRoot = CSharpSyntaxTree.ParseText(origText).GetRoot();
        var origByName = origRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .GroupBy(m => m.Identifier.Text).ToDictionary(g => g.Key, g => g.ToList());

        var targets = new (string File, string[] Names)[] {
            ("Server.ShipCommands.cs", ShipCommands),
            ("Server.Docking.cs", Docking),
            ("Server.Combat.cs", Combat),
        };

        int checked_ = 0, bad = 0;
        var missing = new List<string>();
        foreach (var t in targets)
        {
            var newText = File.ReadAllText(Path.Combine(newDir, t.File));
            var newRoot = CSharpSyntaxTree.ParseText(newText).GetRoot();
            var newByName = newRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .GroupBy(m => m.Identifier.Text).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var name in t.Names)
            {
                if (!newByName.TryGetValue(name, out var nl) || nl.Count != 1)
                { missing.Add($"{t.File}:{name}"); continue; }
                if (!origByName.TryGetValue(name, out var ol) || ol.Count != 1)
                { missing.Add($"ORIG-MISSING:{name}"); continue; }
                string o = origText.Substring(ol[0].FullSpan.Start, ol[0].FullSpan.Length).Trim();
                string n = newText.Substring(nl[0].FullSpan.Start, nl[0].FullSpan.Length).Trim();
                checked_++;
                if (o != n)
                { bad++; Console.WriteLine($"BYTE-DIFF: {t.File}:{name}"); }
            }
        }
        // also confirm no target method lingers in the new Server.cs
        var newServer = File.ReadAllText(Path.Combine(newDir, "Server.cs"));
        var nsRoot = CSharpSyntaxTree.ParseText(newServer).GetRoot();
        var nsByName = nsRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.Text).ToHashSet();
        var strays = targets.SelectMany(t => t.Names).Where(n => nsByName.Contains(n)).ToList();

        Console.WriteLine($"VERIFY: {checked_} methods compared, {bad} byte-diffs");
        if (missing.Count > 0) Console.WriteLine("MISSING/DUPE: " + string.Join(", ", missing));
        if (strays.Count > 0) Console.WriteLine("STRAY IN SERVER.CS: " + string.Join(", ", strays));
        Console.WriteLine(bad == 0 && missing.Count == 0 && strays.Count == 0 ? "VERIFY PASS" : "VERIFY FAIL");
    }

    static string SummaryComment(string group)
    {
        switch (group)
        {
            case "ShipCommands":
                return "/// <summary>\n/// Inbound ship-command handlers of <see cref=\"Server\"/>\n/// (P330 stage-4 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>\n/// type, so private field/method access across the split files is unaffected.\n/// </summary>\n";
            case "Docking":
                return "/// <summary>\n/// Board/leave/dock/undock/teleport/rescue/ripcord/relaunch members of <see cref=\"Server\"/>\n/// (P330 stage-4 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>\n/// type, so private field/method access across the split files is unaffected.\n/// </summary>\n";
            default:
                return "/// <summary>\n/// Ordnance/kill/death/eject broadcast and win-condition members of <see cref=\"Server\"/>\n/// (P330 stage-4 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>\n/// type, so private field/method access across the split files is unaffected.\n/// </summary>\n";
        }
    }
}
