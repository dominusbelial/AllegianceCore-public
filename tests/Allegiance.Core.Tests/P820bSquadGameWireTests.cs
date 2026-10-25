using System.Security.Cryptography;
using Allegiance.Core.Identity;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P820b/P820c — squads-in-games wire primitives: the join-token squad claim
/// (the lobby→server identity path), the native SET_TEAM_INFO(15) relay (the
/// server→client squad path, fsmission.cpp:1580-1590) and the token length cap
/// (P820c fix 5). Pure unit tests, no server harness. Run 3× alone (rules light
/// gate).
/// </summary>
public sealed class P820bSquadGameWireTests
{
    [Fact]
    public void JoinToken_squad_claim_round_trips_and_legacy_token_still_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string issuer = "https://lobby.test", aud = "game-server", acct = "00000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;

        var squads = new List<SquadClaim>
        {
            new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Iron Harbor", true),
            new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Red Dawn", false),
        };
        string withSquads = JoinTokens.Issue(key, issuer, aud, acct, "Pilot", now, squads);
        var parsed = JoinTokens.Verify(withSquads, key, issuer, aud, now);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Squads!.Count);
        Assert.Equal("Iron Harbor", parsed.Squads[0].Name);
        Assert.True(parsed.Squads[0].CanLead);
        Assert.False(parsed.Squads[1].CanLead);

        // A token minted without squads (the pre-P820b shape) still verifies.
        string legacy = JoinTokens.Issue(key, issuer, aud, acct, "Pilot", now);
        var legacyParsed = JoinTokens.Verify(legacy, key, issuer, aud, now);
        Assert.NotNull(legacyParsed);
        Assert.Null(legacyParsed!.Squads);
    }

    [Fact]
    public void SetTeamInfo_round_trips_native_layout()
    {
        var msg = new SetTeamInfo { SquadId = 123456789, SideId = 3, SideName = "Iron Harbor" };
        var bytes = msg.Encode();
        Assert.Equal(SetTeamInfo.FixedSize, bytes.Length);   // native sizeof(FMD_CS_SET_TEAM_INFO) = 36

        var back = SetTeamInfo.Decode(bytes);
        Assert.Equal(123456789, back.SquadId);
        Assert.Equal(3, back.SideId);
        Assert.Equal("Iron Harbor", back.SideName);
    }

    [Fact]
    public void SetTeamInfo_sideName_truncates_to_25_bytes()
    {
        var msg = new SetTeamInfo { SquadId = 5, SideId = 0, SideName = new string('N', 60) };
        var back = SetTeamInfo.Decode(msg.Encode());
        Assert.Equal(25, back.SideName.Length);
        Assert.Equal(new string('N', 25), back.SideName);
    }

    [Fact]
    public void SideRoster_old_format_is_exactly_master()
    {
        // A plain (pre-P820b) roster: no squad fields. The encoded frame must be
        // exactly header + 46B/record — the master wire format — and round-trip.
        var roster = new SideRoster
        {
            Sides =
            [
                new SideRoster.SideRecord(0, 2, 3, "Humans", 0.2f, 0.4f, 0.6f, 1f),
                new SideRoster.SideRecord(1, 3, 0, "Aliens"),
            ],
        };
        var bytes = roster.Encode();
        Assert.Equal(SideRoster.HeaderSize + SideRoster.RecordSize * 2, bytes.Length);

        var decoded = SideRoster.Decode(bytes);
        Assert.Equal(2, decoded.Sides.Length);
        Assert.Equal("Humans", decoded.Sides[0].Name);
        Assert.Equal(0.2f, decoded.Sides[0].R);
        Assert.Equal(0.6f, decoded.Sides[0].B);
        Assert.Equal("Aliens", decoded.Sides[1].Name);
    }

    [Fact]
    public void Maximal_token_with_16_squads_verifies_under_the_cap()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string issuer = "https://lobby.test", aud = "game-server", acct = "00000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;

        // The documented per-token bound: 16 squads, each with the max-length
        // (24-char) name — the worst case P820c caps for. It must fit the cap
        // and verify.
        var squads = Enumerable.Range(0, JoinTokens.MaxSquadsPerToken)
            .Select(i => new SquadClaim($"{(i + 1):x32}", new string((char)('A' + i % 26), 24), i % 2 == 0))
            .ToList();
        Assert.Equal(JoinTokens.MaxSquadsPerToken, squads.Count);

        string token = JoinTokens.Issue(key, issuer, aud, acct, "Pilot", now, squads);
        Assert.True(token.Length <= JoinTokens.MaxTokenLength,
            $"maximal {JoinTokens.MaxSquadsPerToken}-claim token is {token.Length}B, over cap {JoinTokens.MaxTokenLength}");
        Assert.NotNull(JoinTokens.Verify(token, key, issuer, aud, now));
    }

    [Fact]
    public void Oversized_token_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string issuer = "https://lobby.test", aud = "game-server", acct = "00000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;

        // Far more squads than the bound allows => a token that exceeds the cap.
        var tooMany = Enumerable.Range(0, 200)
            .Select(i => new SquadClaim($"{(i + 1):x32}", new string((char)('A' + i % 26), 24), true))
            .ToList();
        string big = JoinTokens.Issue(key, issuer, aud, acct, "Pilot", now, tooMany);
        Assert.True(big.Length > JoinTokens.MaxTokenLength,
            $"expected an over-cap token, got {big.Length}B (cap {JoinTokens.MaxTokenLength})");
        Assert.Null(JoinTokens.Verify(big, key, issuer, aud, now));
    }
}
