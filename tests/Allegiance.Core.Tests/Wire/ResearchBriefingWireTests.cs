using System.Buffers.Binary;
using System.IO.Compression;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// P1073 — the port-only research briefing (fmid 992). Native has no counterpart
/// (native reads its own captured research objects), so this pins the port wire
/// contract: exact header, Brotli body, desktop round-trip, and the ushort frame
/// bound for the largest authored showcase plan.
/// </summary>
public sealed class ResearchBriefingWireTests
{
    [Fact]
    public void Header_and_body_round_trip_the_authored_plan()
    {
        var plan = new ResearchPlan
        {
            Core = "PCore001", CoreVersion = 12, CivId = 3, Name = "round-trip",
            Steps =
            [
                new ResearchStep
                {
                    Action = "develop", Id = 42, Name = "Shipyard",
                    RequiredTechs = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray(),
                    ExpectedUnlocks = [new TechIdentity("develop", 7, "Cruiser", "unlock")],
                },
                new ResearchStep { Action = "wait", WaitSeconds = 12.5 },
            ],
        };
        var message = new ResearchBriefing
        {
            Epoch = "epoch-1", SideId = 1, Plan = plan,
            Settings = new ResearchSettings(), IncomeMultiplier = 1.25,
            TechMode = "research", ResearchTimeScale = 2, Loop = true,
            CompletedDevelopments = [1, 2, 3],
        };

        var bytes = message.Encode();
        Assert.Equal(ResearchBriefing.Fmid, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(bytes.Length, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        Assert.True(bytes.Length <= ushort.MaxValue);

        var decoded = ResearchBriefing.Decode(bytes);
        Assert.Equal("epoch-1", decoded.Epoch);
        Assert.Equal((short)1, decoded.SideId);
        Assert.Equal("research", decoded.TechMode);
        Assert.Equal(1.25, decoded.IncomeMultiplier);
        Assert.True(decoded.Loop);
        Assert.Equal([1, 2, 3], decoded.CompletedDevelopments);
        Assert.Equal("round-trip", decoded.Plan.Name);
        Assert.Equal(2, decoded.Plan.Steps.Count);
        Assert.Equal("Shipyard", decoded.Plan.Steps[0].Name);
        Assert.Equal("Cruiser", decoded.Plan.Steps[0].ExpectedUnlocks[0].Name);
    }

    [Fact]
    public void Largest_authored_plan_fits_one_ushort_frame()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string directory = Path.Combine(root.FullName, "data/showcase-plans/PCore001");
        Assert.True(Directory.Exists(directory), "the authored showcase plans are part of the repo");
        var plan = Directory.GetFiles(directory, "*.json")
            .Select(ResearchPlanCatalog.Read)
            .OrderByDescending(p => p.Steps.Count).ThenByDescending(p => p.Steps.Sum(s => s.Name.Length + s.ExpectedUnlocks.Count))
            .First();
        Assert.NotEmpty(plan.Steps);

        // Encode a briefing wrapping the largest authored plan exactly as the
        // server sends it with the side join sync.
        var message = new ResearchBriefing { Epoch = "2026-11-22T00:00:00Z", Plan = plan, TechMode = "research" };
        var bytes = message.Encode();
        Assert.True(bytes.Length <= ushort.MaxValue,
            $"the largest authored plan ({plan.Steps.Count} steps) compressed to {bytes.Length} bytes");
        Assert.Equal(plan.Steps.Count, ResearchBriefing.Decode(bytes).Plan.Steps.Count);
    }

    [Fact]
    public void Rejects_bad_header_length_and_fmid()
    {
        var bytes = new ResearchBriefing().Encode();
        Assert.Throws<ArgumentException>(() => ResearchBriefing.Decode(bytes[..^1]));
        var wrongFmid = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongFmid.AsSpan(2), 996);
        Assert.Throws<ArgumentException>(() => ResearchBriefing.Decode(wrongFmid));
        Assert.Throws<ArgumentException>(() => ResearchBriefing.Decode([]));
    }

    [Fact]
    public void A_truncated_or_invalid_body_is_rejected()
    {
        var valid = new ResearchBriefing { Plan = new ResearchPlan { Name = "x" } }.Encode();
        foreach (byte[] body in new[] { new byte[] { 0x00 }, new byte[] { 0xff, 0xff, 0xff, 0xff }, [] })
        {
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), ResearchBriefing.Fmid);
            body.CopyTo(frame, 4);
            Assert.ThrowsAny<Exception>(() => ResearchBriefing.Decode(frame));
        }
        Assert.True(valid.Length <= ushort.MaxValue);
    }
}
