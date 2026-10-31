using System.Buffers.Binary;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Immutable decoded 16-bit PCM, shared by every instance of a template.</summary>
public sealed record SoundPcm(byte[] Data, int SampleRate, int Channels)
{
    public int FrameBytes => Channels * 2;
    public int Frames => Data.Length / FrameBytes;

    // The shipping pipeline uses uncompressed XNB + SoundEffectProcessor Best
    // (Content.mgcb). Read the PCM before uploading it, without reflection into
    // MonoGame's private buffers or requiring original Ogg files at runtime.
    public static SoundPcm ReadContent(string root, string asset)
    {
        string path = Path.Combine(root, asset + ".xnb");
        using var stream = Path.IsPathRooted(path) ? File.OpenRead(path) : TitleContainer.OpenStream(path);
        return ReadXnb(stream);
    }

    public static SoundPcm ReadXnb(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (new string(reader.ReadChars(3)) != "XNB") throw new InvalidDataException("Expected XNB sound");
        reader.ReadByte(); // platform
        if (reader.ReadByte() != 5 || (reader.ReadByte() & 0xC0) != 0)
            throw new InvalidDataException("ASR content requires uncompressed XNB v5");
        int length = reader.ReadInt32();
        if (reader.Read7BitEncodedInt() != 1 || !reader.ReadString().StartsWith("Microsoft.Xna.Framework.Content.SoundEffectReader", StringComparison.Ordinal))
            throw new InvalidDataException("Expected SoundEffectReader");
        reader.ReadInt32(); // reader version
        if (reader.Read7BitEncodedInt() != 0 || reader.Read7BitEncodedInt() != 1)
            throw new InvalidDataException("Unexpected XNB resources");
        int formatSize = reader.ReadInt32();
        if (formatSize < 16 || formatSize > 64) throw new InvalidDataException("Invalid PCM format");
        byte[] format = reader.ReadBytes(formatSize);
        if (format.Length != formatSize) throw new EndOfStreamException();
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
        int rate = BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 || channels is < 1 or > 2
            || rate < 8000 || rate > 48000 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) != 16)
            throw new InvalidDataException("Expected mono/stereo 16-bit PCM at 8–48 kHz");
        int bytes = reader.ReadInt32();
        if (bytes <= 0 || bytes > length || bytes % (channels * 2) != 0)
            throw new InvalidDataException("Invalid PCM length");
        byte[] data = reader.ReadBytes(bytes);
        if (data.Length != bytes) throw new EndOfStreamException();
        return new SoundPcm(data, rate, channels);
    }
}

/// <summary>Sample-aligned native ASR region. soundtemplates.cpp:638-659.</summary>
public readonly record struct AsrRegion(int Start, int Length)
{
    public static AsrRegion Whole(SoundPcm pcm) => new(0, pcm.Frames);
    public static AsrRegion FromSeconds(SoundPcm pcm, float start, float length)
    {
        if (!float.IsFinite(start) || !float.IsFinite(length) || start < 0 || length <= 0
            || start + length > (float)pcm.Frames / pcm.SampleRate)
            throw new ArgumentOutOfRangeException(nameof(length));
        // Native truncates byte counts, then aligns to a complete sample frame.
        return new((int)(start * (pcm.SampleRate * pcm.FrameBytes)) / pcm.FrameBytes,
            (int)(length * (pcm.SampleRate * pcm.FrameBytes)) / pcm.FrameBytes);
    }
}

/// <summary>Generic attack/sustain/release cursor; no device and no allocations
/// in Read/Stop. Native BufferPositionTracker, ds3dvirtualbuffer.cpp:41-133.</summary>
public sealed class AsrCursor
{
    private readonly SoundPcm _pcm;
    private readonly AsrRegion _region;
    private int _position;
    private bool _ending;
    public bool Finished => _position >= _pcm.Frames;
    public int Position => _position;
    public AsrCursor(SoundPcm pcm, AsrRegion region)
    {
        if (region.Start < 0 || region.Length <= 0 || (long)region.Start + region.Length > pcm.Frames)
            throw new ArgumentOutOfRangeException(nameof(region));
        _pcm = pcm; _region = region;
    }
    public void Stop(bool immediate = false)
    {
        _ending = true;
        if (immediate) _position = _pcm.Frames;
    }
    public int Read(Span<byte> output)
    {
        int written = 0, frameBytes = _pcm.FrameBytes;
        int available = output.Length / frameBytes;
        while (available > 0 && !Finished)
        {
            int end = _ending ? (_position < _region.Start ? _region.Start : _pcm.Frames)
                : _region.Start + _region.Length;
            int count = Math.Min(available, end - _position);
            _pcm.Data.AsSpan(_position * frameBytes, count * frameBytes).CopyTo(output[written..]);
            _position += count; available -= count; written += count * frameBytes;
            if (_ending && _position == _region.Start) _position += _region.Length;
            else if (!_ending && _position == end) _position = _region.Start;
        }
        return written;
    }
}

public sealed record AsrSoundTemplate(SoundPcm Pcm, AsrRegion Region);

/// <summary>Cache by source identity and aligned repeat interval, as native
/// RepeatFirePCMData::Create, soundtemplates.cpp:986-1103. Released with the manager.</summary>
public sealed class RepeatFirePcmCache
{
    private readonly Dictionary<(SoundPcm Source, int Delay), AsrSoundTemplate> _cache = new();
    public AsrSoundTemplate Get(SoundPcm pcm, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        int bytes = (int)(seconds * (pcm.SampleRate * pcm.FrameBytes));
        bytes -= bytes % pcm.FrameBytes;
        if (bytes <= 0 || bytes > 0x1000000) throw new ArgumentOutOfRangeException(nameof(seconds));
        var key = (pcm, bytes);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (pcm.Data.Length < bytes * 2) throw new InvalidDataException("Repeat-fire wave must contain two repeat intervals");
        byte[] output = new byte[checked(pcm.Data.Length + 2 * bytes)];
        pcm.Data.CopyTo(output, 2 * bytes);
        pcm.Data.AsSpan(0, bytes).CopyTo(output);
        // Native's signed-16 first loop is empty (1205), leaving this region
        // uninitialized. Deterministically fill it with the intended two-shot
        // sum; do not reproduce reads of uninitialized memory.
        int firstEnd = pcm.Data.Length;
        for (int i = bytes; i < firstEnd; i += 2)
        {
            int sum = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i))
                + BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i + bytes))
                + BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i + 2 * bytes));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i), (short)Math.Clamp(sum, short.MinValue, short.MaxValue));
        }
        for (int i = Math.Max(bytes, firstEnd); i < pcm.Data.Length + bytes; i += 2)
        {
            int sum = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i))
                + BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i + bytes));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i), (short)Math.Clamp(sum, short.MinValue, short.MaxValue));
        }
        var result = new AsrSoundTemplate(new SoundPcm(output, pcm.SampleRate, pcm.Channels),
            new AsrRegion(2 * bytes / pcm.FrameBytes, bytes / pcm.FrameBytes));
        _cache.Add(key, result);
        return result;
    }
    public void Clear() => _cache.Clear();
}
