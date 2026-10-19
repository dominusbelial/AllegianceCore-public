using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Allegiance.ClientCore.Session;

namespace Allegiance.ClientCore.Replay;

/// <summary>Versioned gzip stream of complete transport deliveries, including CRC/framing.
/// Timestamps are monotonic microseconds from attachment; equal timestamps preserve arrival order.</summary>
public sealed class WireRecording : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly long _start = Stopwatch.GetTimestamp();
    private bool _disposed;
    public WireRecording(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new(new GZipStream(File.Create(path), CompressionLevel.SmallestSize));
        _writer.Write("P729RPL2");
    }
    public void Write(ReadOnlySpan<byte> packet)
    {
        lock (_writer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Write((long)(Stopwatch.GetElapsedTime(_start).TotalMilliseconds * 1000));
            _writer.Write(packet.Length);
            _writer.Write(packet);
        }
    }
    /// <summary>Local camera metadata alongside the untouched incoming network deliveries.</summary>
    public void WriteCamera(short sector, byte phase)
    {
        lock (_writer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Write((long)(Stopwatch.GetElapsedTime(_start).TotalMilliseconds * 1000));
            _writer.Write(-1); _writer.Write(sector); _writer.Write(phase);
        }
    }
    public void Dispose() { lock (_writer) { if (_disposed) return; _disposed = true; _writer.Dispose(); } }
}

public sealed record RecordedPacket(long Microseconds, byte[] Payload, short? ViewSector = null, byte CameraPhase = 0);

/// <summary>Bounded reader and fixed 60 Hz packet pump. No socket, server, bot or wall-clock pacing.</summary>
public sealed class BattleReplay
{
    public const int FramesPerSecond = 60;
    public IReadOnlyList<RecordedPacket> Packets { get; }
    public long DurationMicroseconds => Packets.Count == 0 ? 0 : Packets[^1].Microseconds;
    private byte[] _streamDigest = new byte[32];
    public short? ViewedSector { get; private set; }
    public byte CameraPhase { get; private set; }
    private int _next;
    public int Frame { get; private set; }
    public bool Finished => Frame * 1_000_000L / FramesPerSecond > DurationMicroseconds && _next == Packets.Count;
    public BattleReplay(string path)
    {
        using var input = new BinaryReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        string version = input.ReadString();
        if (version is not ("P729RPL1" or "P729RPL2")) throw new InvalidDataException("Unsupported battle recording");
        var packets = new List<RecordedPacket>();
        long bytes = 0, previous = 0;
        while (true)
        {
            // Require complete headers: a partial final timestamp is corruption, not EOF.
            var stamp = input.ReadBytes(8);
            if (stamp.Length == 0) break;
            if (stamp.Length != 8) throw new InvalidDataException("Truncated recording timestamp");
            long time = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(stamp);
            int length = input.ReadInt32();
            if (time < previous || time > 600_000_000 || packets.Count >= 1_000_000)
                throw new InvalidDataException("Recording exceeds timestamp/record limits");
            if (version == "P729RPL2" && length == -1)
            {
                short sector = input.ReadInt16(); byte phase = input.ReadByte();
                if (sector < 0 || phase > 2) throw new InvalidDataException("Invalid camera marker");
                packets.Add(new(time, [], sector, phase)); previous = time; continue;
            }
            if (length is < 1 or > 16_777_216 || (bytes += length) > 536_870_912)
                throw new InvalidDataException("Recording exceeds timestamp/size limits");
            var payload = input.ReadBytes(length);
            if (payload.Length != length) throw new InvalidDataException("Truncated recording packet");
            packets.Add(new(time, payload)); previous = time;
        }
        if (packets.Count == 0) throw new InvalidDataException("Empty battle recording");
        Packets = packets;
    }
    public void Advance(ConnectionSession session)
    {
        long us = ++Frame * 1_000_000L / FramesPerSecond;
        while (_next < Packets.Count && Packets[_next].Microseconds <= us)
        {
            var packet = Packets[_next++];
            if (packet.ViewSector is { } sector)
            {
                ViewedSector = sector; CameraPhase = packet.CameraPhase;
                session.World.TelemetrySectorOverride = sector;
                continue;
            }
            session.ReplayPacket(packet.Payload, packet.Microseconds / 1000);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(_streamDigest); hash.AppendData(packet.Payload); _streamDigest = hash.GetHashAndReset();
        }
        session.World.NowSeconds = () => Frame / (double)FramesPerSecond;
    }
    /// <summary>Canonical decoded render state, plus a digest of every consumed wire byte.
    /// Sorted IDs and binary float encoding avoid culture, ordering and wall time differences.</summary>
    public string FrameStateHash(ConnectionSession session)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(Frame); writer.Write(_next); writer.Write(_streamDigest);
        writer.Write(ViewedSector ?? -1); writer.Write(CameraPhase);
        lock (session.Gate)
        {
            var world = session.World;
            writer.Write(world.MyShipId ?? -1); writer.Write(world.MissionGeneration);
            writer.Write(world.TimeScale);
            foreach (var ship in world.Ships.Values.OrderBy(s => s.ShipId))
            {
                writer.Write(ship.ShipId); writer.Write(ship.HullId); writer.Write(ship.SideId);
                writer.Write(ship.SectorId); writer.Write(ship.StationId); writer.Write(ship.IsNotVisible);
                if (ship.Latest is { } pose)
                {
                    writer.Write(pose.Position.X); writer.Write(pose.Position.Y); writer.Write(pose.Position.Z);
                    writer.Write(pose.Velocity.X); writer.Write(pose.Velocity.Y); writer.Write(pose.Velocity.Z);
                    foreach (var axis in new[] { pose.Orientation.GetForward(), pose.Orientation.GetUp() })
                    { writer.Write(axis.X); writer.Write(axis.Y); writer.Write(axis.Z); }
                    writer.Write(ship.LastUpdateTick);
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "benchmarks", "battle.alr");
}

/// <summary>Authored route: short chase, sustained battle, then station flyby.</summary>
public static class BenchmarkRoute
{
    public static byte Phase(double seconds, double duration) => seconds < duration * .12 ? (byte)0 : seconds < duration * .90 ? (byte)1 : (byte)2;
}
