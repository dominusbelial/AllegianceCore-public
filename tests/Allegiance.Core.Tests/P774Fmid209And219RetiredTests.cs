using System.Reflection;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P774 — registry guard for the retired port-only score-roster fmids
/// 209 (<c>ScoreRosterUpdate</c>) and 219 (<c>ScoreRosterDelta</c>). Native has
/// no such message: its <c>DEFINE_FEDMSG</c> table ends at CLUSTERINFO = 200
/// (<c>Inc/Messages.h:1039</c>), and the client builds the in-mission roster
/// from PLAYERINFO(10) + KILL_SHIP(105) (<c>clintlib.h:346-361</c>;
/// <c>clintlib/appmsg.cpp:2766-2773,2805-2808</c>). The wire types, the
/// server's broadcast step and the bot's accessor must all be gone.
/// </summary>
public sealed class P774Fmid209And219RetiredTests
{
    [Fact]
    public void No_wire_type_declares_fmid_209_or_219()
    {
        var offenders = new List<string>();
        foreach (var t in typeof(IFedMessage).Assembly.GetTypes())
        {
            const string fieldName = "Fmid";
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var f in fields)
            {
                if (f.Name != fieldName || !f.IsLiteral || f.FieldType != typeof(ushort) && f.FieldType != typeof(int)) continue;
                var value = Convert.ToInt32(f.GetRawConstantValue());
                if (value is 209 or 219) offenders.Add($"{t.FullName}.{f.Name} = {value}");
            }
        }
        Assert.True(offenders.Count == 0,
            "fmids 209/219 are retired (P774): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Server_has_no_score_roster_broadcast_step()
    {
        var server = typeof(Allegiance.Server.Server);
        Assert.Null(server.GetMethod("BroadcastScoreRoster", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.Null(server.GetMethod("ScoreRosterWorkDue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [Fact]
    public void Bot_has_no_score_roster_accessor()
    {
        Assert.Empty(typeof(Allegiance.Bot.Bot).GetMember("ScoreRosterBroadcasts",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
        Assert.Empty(typeof(Allegiance.Bot.Bot).GetMember("ResetScoreRosterBroadcasts",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
    }
}
