using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Allegiance.Lobby.Identity;
using Allegiance.Lobby.Squad;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

/// <summary>
/// P820a — the lobby squad store and HTTP API. Exercises every native squad
/// operation (create/edit/join/cancel/accept/reject/boot/quit/promote/demote/
/// transfer) and the permission rules that gate them
/// (<c>WinTrek/SquadsScreen.cpp:1623-1670</c>), plus the lists (All/My/player/
/// find) and character squads. Run 3× alone (rules light gate).
/// </summary>
public sealed class P820aSquadTests
{
    private static (IdentityStore store, SquadStore squads, string dir) Stores(TimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "squad-" + Guid.NewGuid().ToString("N"));
        var settings = new IdentitySettings { ConnectionString = "Data Source=" + Path.Combine(dir, "accounts.db") };
        var store = new IdentityStore(settings, clock); store.Migrate();
        return (store, new SquadStore(settings, clock), dir);
    }

    private static SquadContracts.CreateSquadRequest Req(string name, string tag = "", string desc = "", string url = "", short civ = -1)
        => new(name, tag, desc, url, civ);

    [Fact]
    public void Create_validates_name_length_and_uniqueness()
    {
        var (store, squads, _) = Stores();
        var a = store.CreateAccount("Alpha");
        long now = 1000;

        Assert.False(squads.CreateSquad(a.Id, Req("   "), now).Ok);                    // empty name
        Assert.False(squads.CreateSquad(a.Id, Req(new string('x', 25)), now).Ok);      // > 24 chars
        Assert.True(squads.CreateSquad(a.Id, Req("Avengers"), now).Ok);
        var dup = squads.CreateSquad(a.Id, Req("avengers"), now);                       // case-insensitive-unique rejection path
        Assert.False(dup.Ok);
    }

    [Fact]
    public void Create_makes_the_creator_leader_and_logs_it()
    {
        var (store, squads, _) = Stores();
        var a = store.CreateAccount("Founder");
        long now = 5000;
        Assert.True(squads.CreateSquad(a.Id, Req("Belters", "BELT", "We haul rock", "https://x.example", 2), now).Ok);

        var all = squads.All(SquadContracts.SquadSortColumn.Name, null, 50);
        var s = Assert.Single(all);
        Assert.Equal("Belters", s.Name);
        Assert.Equal(2, s.Civ);
        Assert.Equal("Founder", s.LeaderCallsign);
        Assert.Equal(1, s.MemberCount);

        var d = squads.Details(s.Id)!;
        Assert.Equal("BELT", d.Tag);
        Assert.Equal("We haul rock", d.Description);
        var leader = Assert.Single(d.Members);
        Assert.Equal(SquadContracts.SquadRole.Leader, leader.Role);
        Assert.NotEmpty(squads.Log(s.Id));   // creation logged (ownership/audit log)
    }

    [Fact]
    public void Join_and_cancel_join_respect_pending_state()
    {
        var (store, squads, _) = Stores();
        long now = 100;
        var leader = store.CreateAccount("Leader");
        var joiner = store.CreateAccount("Joiner");
        Assert.True(squads.CreateSquad(leader.Id, Req("Guild"), now).Ok);
        var id = squads.All(SquadContracts.SquadSortColumn.Name, null, 10)[0].Id;

        // A stranger can request to join.
        Assert.True(squads.Join(joiner.Id, id, now).Ok);
        // ...but not twice.
        Assert.False(squads.Join(joiner.Id, id, now).Ok);
        // ...and the leader (already a member) cannot join.
        Assert.False(squads.Join(leader.Id, id, now).Ok);
        // Pending shows on the joiner's My list and as pending in details.
        var mine = squads.ByPlayer(joiner.Id);
        Assert.Equal(SquadContracts.SquadRole.Pending, Assert.Single(mine).ViewerRole);
        Assert.Contains(squads.Details(id)!.Members, m => m.Role == SquadContracts.SquadRole.Pending);

        // A pending player can cancel.
        Assert.True(squads.CancelJoin(joiner.Id, id, now).Ok);
        Assert.Empty(squads.ByPlayer(joiner.Id));
    }

    [Fact]
    public void Accept_promote_demote_are_owner_only_with_role_gates()
    {
        var (store, squads, _) = Stores();
        long now = 100;
        var owner = store.CreateAccount("Owner");
        var officer = store.CreateAccount("Officer");
        var recruit = store.CreateAccount("Recruit");
        Assert.True(squads.CreateSquad(owner.Id, Req("Raptors"), now).Ok);
        var id = squads.All(SquadContracts.SquadSortColumn.Name, null, 10)[0].Id;
        squads.Join(recruit.Id, id, now);

        // Non-owner cannot accept/promote.
        Assert.False(squads.Accept(officer.Id, id, recruit.Id, now).Ok);
        // Owner accepts the pending recruit.
        Assert.True(squads.Accept(owner.Id, id, recruit.Id, now).Ok);
        Assert.Equal(SquadContracts.SquadRole.Member, squads.Details(id)!.Members.Single(m => m.AccountId == recruit.Id).Role);

        // Promote requires target be a member (officer is not a member yet).
        Assert.False(squads.Promote(owner.Id, id, officer.Id, now).Ok);
        // Owner promotes the (now) member recruit.
        Assert.True(squads.Promote(owner.Id, id, recruit.Id, now).Ok);
        Assert.Equal(SquadContracts.SquadRole.AssistantLeader, squads.Details(id)!.Members.Single(m => m.AccountId == recruit.Id).Role);
        // Promote an ASL → refuse; demote an ASL → member.
        Assert.False(squads.Promote(owner.Id, id, recruit.Id, now).Ok);
        Assert.True(squads.Demote(owner.Id, id, recruit.Id, now).Ok);
        Assert.Equal(SquadContracts.SquadRole.Member, squads.Details(id)!.Members.Single(m => m.AccountId == recruit.Id).Role);
    }

    [Fact]
    public void Ownership_log_records_only_owners_newest_first_as_native()
    {
        // Native's ownership log is "name          date" pairs of successive owners, newest first
        // (ZoneSquad.cpp:290-332, PushFront); joins, promotions and edits are not in it.
        var (store, squads, _) = Stores();
        var owner = store.CreateAccount("Owner");
        var heir = store.CreateAccount("Heir");
        Assert.True(squads.CreateSquad(owner.Id, Req("Wardens"), 100).Ok);
        var id = squads.All(SquadContracts.SquadSortColumn.Name, null, 10)[0].Id;
        Assert.True(squads.Join(heir.Id, id, 200).Ok);
        Assert.True(squads.Accept(owner.Id, id, heir.Id, 300).Ok);
        Assert.True(squads.Promote(owner.Id, id, heir.Id, 400).Ok);
        Assert.Single(squads.Log(id));
        Assert.StartsWith("Owner          ", squads.Log(id)[0].Line);

        Assert.True(squads.Transfer(owner.Id, id, heir.Id, 500).Ok);
        var log = squads.Log(id);
        Assert.Equal(2, log.Count);
        Assert.StartsWith("Heir          ", log[0].Line);
        Assert.StartsWith("Owner          ", log[1].Line);
    }

    [Fact]
    public void Reject_refuses_a_pending_request_and_boots_a_member()
    {
        var (store, squads, _) = Stores();
        long now = 1;
        var owner = store.CreateAccount("Boss");
        var r1 = store.CreateAccount("PendingGuy");
        var r2 = store.CreateAccount("MemberGuy");
        Assert.True(squads.CreateSquad(owner.Id, Req("Shadow"), now).Ok);
        var id = squads.All(SquadContracts.SquadSortColumn.Name, null, 10)[0].Id;
        squads.Join(r1.Id, id, now);
        squads.Join(r2.Id, id, now); squads.Accept(owner.Id, id, r2.Id, now);

        Assert.True(squads.RejectOrBoot(owner.Id, id, r1.Id, now).Ok);   // reject pending
        Assert.True(squads.RejectOrBoot(owner.Id, id, r2.Id, now).Ok);   // boot member
        Assert.Single(squads.Details(id)!.Members);                      // only the owner remains
    }

    [Fact]
    public void Transfer_moves_ownership_and_demotes_the_former_leader()
    {
        var (store, squads, _) = Stores();
        long now = 1;
        var a = store.CreateAccount("OldLead");
        var b = store.CreateAccount("NewLead");
        Assert.True(squads.CreateSquad(a.Id, Req("Knights"), now).Ok);
        var id = squads.All(SquadContracts.SquadSortColumn.Name, null, 10)[0].Id;
        squads.Join(b.Id, id, now); squads.Accept(a.Id, id, b.Id, now);

        Assert.True(squads.Transfer(a.Id, id, b.Id, now).Ok);
        var d = squads.Details(id)!;
        Assert.Equal(b.Id, d.LeaderAccountId);
        Assert.Equal(SquadContracts.SquadRole.Leader, d.Members.Single(m => m.AccountId == b.Id).Role);
        // Previous leader becomes an ASL (ClubMessages.h:249-253).
        Assert.Equal(SquadContracts.SquadRole.AssistantLeader, d.Members.Single(m => m.AccountId == a.Id).Role);
    }

    [Fact]
    public void Quitting_leader_transfers_to_asl_else_member_else_deletes()
    {
        var (store, squads, _) = Stores();
        long now = 1;
        // Case 1: leader + ASL + member → ASL becomes leader.
        var (lead, asl, mem) = (store.CreateAccount("L"), store.CreateAccount("A"), store.CreateAccount("M"));
        Assert.True(squads.CreateSquad(lead.Id, Req("House1"), now).Ok);
        var id1 = IdByName(squads, "House1");
        squads.Join(asl.Id, id1, now); squads.Accept(lead.Id, id1, asl.Id, now); squads.Promote(lead.Id, id1, asl.Id, now);
        squads.Join(mem.Id, id1, now); squads.Accept(lead.Id, id1, mem.Id, now);
        Assert.True(squads.Quit(lead.Id, id1, now).Ok);
        var d1 = squads.Details(id1)!;
        Assert.Equal(asl.Id, d1.LeaderAccountId);
        Assert.Equal(SquadContracts.SquadRole.Leader, d1.Members.Single(m => m.AccountId == asl.Id).Role);

        // Case 2: leader + only a member → member becomes leader.
        var (lead2, mem2) = (store.CreateAccount("L2"), store.CreateAccount("M2"));
        Assert.True(squads.CreateSquad(lead2.Id, Req("House2"), now).Ok);
        var id2 = IdByName(squads, "House2");
        squads.Join(mem2.Id, id2, now); squads.Accept(lead2.Id, id2, mem2.Id, now);
        Assert.True(squads.Quit(lead2.Id, id2, now).Ok);
        Assert.Equal(mem2.Id, squads.Details(id2)!.LeaderAccountId);

        // Case 3: solo leader quitting dissolves the squad (SQUAD_DELETED).
        var solo = store.CreateAccount("Solo");
        Assert.True(squads.CreateSquad(solo.Id, Req("House3"), now).Ok);
        var id3 = IdByName(squads, "House3");
        Assert.True(squads.Quit(solo.Id, id3, now).Ok);
        Assert.Null(squads.Details(id3));
    }

    private static string IdByName(SquadStore squads, string name)
        => squads.All(SquadContracts.SquadSortColumn.Name, null, 100).First(s => s.Name == name).Id;

    [Fact]
    public void All_sorts_by_name_and_centers_on_basis()
    {
        var (store, squads, _) = Stores();
        long now = 1;
        foreach (var n in new[] { "Charlie", "Alpha", "Bravo", "Delta", "Echo" })
        {
            var a = store.CreateAccount("Lead_" + n);
            Assert.True(squads.CreateSquad(a.Id, Req(n), now).Ok);
        }
        var all = squads.All(SquadContracts.SquadSortColumn.Name, null, 50);
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta", "Echo"], all.Select(s => s.Name).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], all.Select(s => s.Ranking).ToArray());

        // A window centered on Delta returns Delta plus neighbours.
        var delta = all.First(s => s.Name == "Delta");
        var win = squads.All(SquadContracts.SquadSortColumn.Name, delta.Id, 3);
        Assert.Equal("Delta", win[1].Name);          // centered
        Assert.Equal(4, win[1].Ranking);
    }

    [Fact]
    public void Find_matches_partial_names()
    {
        var (store, squads, _) = Stores();
        long now = 1;
        store.CreateAccount("x");
        Assert.True(squads.CreateSquad(store.CreateAccount("lx").Id, Req("Thunderbolts"), now).Ok);
        Assert.True(squads.CreateSquad(store.CreateAccount("ly").Id, Req("Thundercats"), now).Ok);
        Assert.True(squads.CreateSquad(store.CreateAccount("lz").Id, Req("Justice League"), now).Ok);
        var hits = squads.Find("Thunder");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.StartsWith("Thunder", h.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CharacterSquads_lists_active_memberships_not_pending()
    {
        var (store, squads, _) = Stores();
        long now = 1;
        var lead = store.CreateAccount("L");
        var active = store.CreateAccount("Active");
        var pending = store.CreateAccount("Pending");
        Assert.True(squads.CreateSquad(lead.Id, Req("Sigil"), now).Ok);
        var id = squads.All(SquadContracts.SquadSortColumn.Name, null, 10)[0].Id;
        squads.Join(active.Id, id, now); squads.Accept(lead.Id, id, active.Id, now);
        squads.Join(pending.Id, id, now);

        var names = squads.CharacterSquads(lead.Id).Select(t => t.Item2).ToHashSet();
        Assert.Contains("Sigil", names);
        Assert.Equal(["Sigil"], squads.CharacterSquads(active.Id).Select(t => t.Item2).ToArray());
        Assert.Empty(squads.CharacterSquads(pending.Id));   // pending excluded
    }

    // ---- HTTP surface ----

    [Fact]
    public async Task Squad_writes_require_a_session_and_enforce_roles_over_http()
    {
        using var app = new LobbyTestApp(); using var http = app.CreateClient();
        var identities = app.Services.GetRequiredService<IdentityStore>();
        var squads = app.Services.GetRequiredService<SquadStore>();
        long now = identities.Now;
        var owner = identities.CreateAccount("Owner");
        var intruder = identities.CreateAccount("Intruder");
        var recruit = identities.CreateAccount("Recruit");

        // Anonymous create → 401.
        using var anon = await http.PostAsJsonAsync("/api/squads/", new { name = "Anon", tag = "", description = "", url = "", civ = -1 });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Owner signs in (session bearer) and creates.
        string ownerBearer = identities.Session(owner.Id);
        using (var signed = App(app, ownerBearer))
        using (var create = await signed.PostAsJsonAsync("/api/squads/", new { name = "Hydra", tag = "HYD", description = "Cut off one head", url = "", civ = 0 }))
        {
            create.EnsureSuccessStatusCode();
            var result = (await create.Content.ReadFromJsonAsync<SquadContracts.SquadActionResult>())!;
            Assert.True(result.Ok);
        }
        var squadId = squads.All(SquadContracts.SquadSortColumn.Name, null, 10).Single().Id;
        squads.Join(recruit.Id, squadId, now);

        // Intruder (not owner) trying to accept → 409 Conflict.
        string intruderBearer = identities.Session(intruder.Id);
        using (var bad = App(app, intruderBearer))
        using (var accept = await bad.PostAsync($"/api/squads/{squadId}/accept/{recruit.Id}", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        }

        // Owner accepts over HTTP.
        using (var good = App(app, ownerBearer))
        using (var accept = await good.PostAsync($"/api/squads/{squadId}/accept/{recruit.Id}", null))
        {
            accept.EnsureSuccessStatusCode();
        }

        // Public reads are unauthenticated.
        using var pub = await http.GetAsync($"/api/squads/{squadId}");
        pub.EnsureSuccessStatusCode();
        var details = (await pub.Content.ReadFromJsonAsync<SquadContracts.SquadDetails>())!;
        Assert.Equal("Hydra", details.Name);
        Assert.Contains(details.Members, m => m.AccountId == recruit.Id && m.Role == SquadContracts.SquadRole.Member);
    }
    private static HttpClient App(LobbyTestApp app, string bearer)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }
}
