using System.Buffers.Binary;

namespace Allegiance.Core;

/// <summary>
/// P945 — the <c>FrameImage</c> half of the <c>.mdl</c> binary format: native's
/// per-frame RLE animation (<c>engine/frameimage.cpp:18-260</c>). A
/// <c>FrameImage</c> is a background <c>ImportImage</c> surface plus
/// <c>nFrame</c> XOR-delta frames: frame 0 is the background itself, frame
/// <c>k</c> is the previous frame with delta <c>k-1</c> replayed on top.
///
/// <para><b>Native layout</b> (<c>FrameImageFactory::Read</c>,
/// <c>engine/frameimage.cpp:280-311</c>): a DWORD <c>nFrame</c>, then
/// <c>nFrame</c> DWORD payload offsets relative to the <c>nFrame</c> DWORD's
/// end, then that payload. The native writer stores
/// <c>m_pdwOffsets[nFrame-1]</c> bytes of RLE
/// (<c>engine/frameimage.cpp:259-267</c>) — i.e. the last offset is the total
/// payload length, so <c>offsets[0] == 0</c>, delta <c>i</c> is
/// <c>[offsets[i], offsets[i+1])</c> and frames <c>1..nFrame-1</c> consume
/// deltas <c>0..nFrame-2</c>. <c>ExtractRLEFrame</c>
/// (<c>engine/frameimage.cpp:124-180</c>) replays deltas <c>0..iFrame</c>
/// onto the packed background copy, so its extra pass at
/// <c>iFrame == nFrame-1</c> reads <c>offsets[nFrame]</c> — one past the
/// array, into the RLE payload's first DWORD — and is a no-op whenever that
/// value is below <c>offsets[nFrame-1]</c> (true for every frame image in the
/// artwork tree, e.g. <c>animlaunch.mdl</c>: 32771 vs 332321). This reader
/// reproduces that: the final pass's end is the payload end, so it is a no-op.
/// </para>
///
/// <para>The delta stream is native's <c>PlayRLE</c>
/// (<c>engine/frameimage.cpp:182-232</c>): a WORD per run, low 14 bits = count,
/// top 2 bits = run kind — <c>00</c> byte-per-pixel XOR from the stream,
/// <c>01</c> one byte XORed into every byte, <c>10</c> one WORD XORed into every
/// WORD, <c>11</c> one DWORD XORed into every DWORD. Runs are sequential and
/// cover the whole packed surface, so the decoder is pure byte arithmetic — no
/// GPU, no D3D types, unit-testable headless.</para>
/// </summary>
public static class MdlFrameImage
{
    private const uint ObjectEnd = 0;
    private const uint ObjectFloat = 1;
    private const uint ObjectString = 2;
    private const uint ObjectTrue = 3;
    private const uint ObjectFalse = 4;
    private const uint ObjectList = 5;
    private const uint ObjectApply = 6;
    private const uint ObjectBinary = 7;
    private const uint ObjectReference = 8;
    private const uint ObjectImport = 9;
    private const uint ObjectPair = 10;

    // PlayRLE run kinds (engine/frameimage.h:20-26).
    private const ushort RleMask = 0xc000;
    private const ushort RleFill = 0x0000;
    private const ushort RleByte = 0x4000;
    private const ushort RleWord = 0x8000;
    private const ushort RleDword = 0xc000;
    private const ushort RleLengthMask = 0x3fff;

    /// <summary>
    /// Parse a <c>FrameImage</c> <c>.mdl</c> — the namespace the
    /// <c>FrameImageButtonPane</c> factory loads, resolving the export named
    /// <c>"image"</c> (<c>engine/modeler.cpp:136-175</c>). Returns the decoded
    /// frames in play order plus the namespace's initial <c>frame</c> value.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a 16-bit RGB565 frame image.</exception>
    public static MdlFrameAnimation Decode(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        var reader = new Reader(data);
        var exports = reader.ParseHeader();

        int imageIndex = exports.IndexOf("image");
        if (imageIndex < 0)
            throw new InvalidDataException("frame image namespace has no 'image' export");
        if (!reader.Objects.TryGetValue((uint)imageIndex, out var image) || image is not FrameNode frame)
            throw new InvalidDataException("frame image namespace's 'image' export is not a FrameImage");

        var frames = DecodeFrames(frame);
        int frameIndex = exports.IndexOf("frame");
        float initial = frameIndex >= 0 && reader.Objects.TryGetValue((uint)frameIndex, out var value)
            && value is NumberNode number ? number.Value : 0f;
        return new MdlFrameAnimation(frame.Width, frame.Height, frames, initial);
    }

    private static List<MdlTexture> DecodeFrames(FrameNode frame)
    {
        var background = Pack(frame);                          // native packs the rows (frameimage.cpp:147-150)
        var frames = new List<MdlTexture>(frame.FrameCount) { ToTexture(frame, background) };

        var buffer = (byte[])background.Clone();
        for (int delta = 0; delta < frame.FrameCount - 1; delta++)
        {
            int start = frame.PayloadStart + frame.Offsets[delta];
            int end = frame.PayloadStart
                + (delta + 1 < frame.FrameCount ? frame.Offsets[delta + 1] : frame.PayloadLength);
            PlayRle(buffer, frame.Data, start, end);           // engine/frameimage.cpp:182-232
            frames.Add(ToTexture(frame, buffer));
        }
        return frames;
    }

    /// <summary>The background surface as a packed (no pitch padding) RGB565
    /// buffer — native copies the texture into a packed buffer before replaying
    /// deltas (<c>engine/frameimage.cpp:147-150</c>).</summary>
    private static byte[] Pack(FrameNode frame)
    {
        var packed = new byte[frame.Width * frame.Height * 2];
        for (int y = 0; y < frame.Height; y++)
            Buffer.BlockCopy(frame.Data, frame.PixelStart + y * frame.Pitch,
                packed, y * frame.Width * 2, frame.Width * 2);
        return packed;
    }

    /// <summary>Replay one XOR delta in place (native <c>PlayRLE</c>).</summary>
    public static void PlayRle(byte[] pixels, byte[] rle, int start, int end)
    {
        int p = start;
        int pd = 0;
        while (p + 2 <= end)
        {
            ushort word = BinaryPrimitives.ReadUInt16LittleEndian(rle.AsSpan(p));
            p += 2;
            int length = word & RleLengthMask;
            switch (word & RleMask)
            {
                case RleFill:
                    for (int i = 0; i < length && p < rle.Length && pd < pixels.Length; i++, p++, pd++)
                        pixels[pd] ^= rle[p];
                    break;
                case RleByte:
                    if (p >= rle.Length) return;
                    byte b = rle[p++];
                    for (int i = 0; i < length && pd < pixels.Length; i++, pd++)
                        pixels[pd] ^= b;
                    break;
                case RleWord:
                    if (p + 2 > rle.Length) return;
                    ushort wordXor = BinaryPrimitives.ReadUInt16LittleEndian(rle.AsSpan(p));
                    p += 2;
                    for (int i = 0; i < length && pd + 2 <= pixels.Length; i++, pd += 2)
                        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(pd),
                            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(pd)) ^ wordXor));
                    break;
                case RleDword:
                default:
                    if (p + 4 > rle.Length) return;
                    uint dwordXor = BinaryPrimitives.ReadUInt32LittleEndian(rle.AsSpan(p));
                    p += 4;
                    for (int i = 0; i < length && pd + 4 <= pixels.Length; i++, pd += 4)
                        BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(pd),
                            BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(pd)) ^ dwordXor);
                    break;
            }
        }
    }

    /// <summary>Convert one packed RGB565 frame to the decoder's RGBA form,
    /// using the same channel rounding as <see cref="MdlTextureDecoder"/>.</summary>
    private static MdlTexture ToTexture(FrameNode frame, byte[] rgb565)
    {
        var rgba = new byte[frame.Width * frame.Height * 4];
        for (int i = 0; i < frame.Width * frame.Height; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(rgb565.AsSpan(i * 2));
            int di = i * 4;
            rgba[di] = (byte)MathF.Round(((v & frame.RedMask) >> 11) / 31f * 255f);
            rgba[di + 1] = (byte)MathF.Round(((v & frame.GreenMask) >> 5) / 63f * 255f);
            rgba[di + 2] = (byte)MathF.Round((v & frame.BlueMask) / 31f * 255f);
            rgba[di + 3] = 255;
        }
        return new MdlTexture(frame.Width, frame.Height, rgba);
    }

    // ── object graph ───────────────────────────────────────────────────────

    private abstract record Node;
    private sealed record NumberNode(float Value) : Node;
    private sealed record StringNode(string Value) : Node;
    private sealed record ImportNode(string Name) : Node;
    private sealed record ApplyNode(string Function, IReadOnlyList<Node> Args) : Node;
    private sealed record ListNode(IReadOnlyList<Node> Items) : Node;
    private sealed record PairNode(Node First, Node Second) : Node;

    private sealed record TextureNode(
        int Width, int Height, int Pitch, int PixelStart, byte[] Data,
        uint RedMask, uint GreenMask, uint BlueMask) : Node;

    /// <summary>A parsed <c>FrameImage</c>: the background texture plus the
    /// <c>nFrame</c> DWORD and per-delta offsets native reads before the RLE
    /// payload (<c>engine/frameimage.cpp:290-300</c>).</summary>
    private sealed record FrameNode(
        int Width, int Height, int Pitch, int PixelStart, byte[] Data,
        uint RedMask, uint GreenMask, uint BlueMask,
        int FrameCount, int[] Offsets, int PayloadStart, int PayloadLength) : Node;

    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly List<string> _importNames = [];
        private TextureNode? _lastTexture;

        public Dictionary<uint, Node> Objects { get; } = [];

        public Reader(byte[] data) => _data = data;

        public List<string> ParseHeader()
        {
            uint magic = ReadU32();
            uint version = ReadU32();
            if (magic != MdlTextureDecoder.MdlMagic)
                throw new InvalidDataException($"bad .mdl magic 0x{magic:x8}");
            if (version != 0x00010000u)
                throw new InvalidDataException($"bad .mdl version 0x{version:x8}");

            int countNamespaces = (int)ReadU32();
            int countImports = (int)ReadU32();
            int countExports = (int)ReadU32();
            int countDefinitions = (int)ReadU32();

            for (int i = 0; i < countNamespaces; i++) _ = ReadCString();
            for (int i = 0; i < countImports; i++)
            {
                _pos += 4;                                     // library index
                _importNames.Add(ReadCString());
            }

            var exports = new List<string>(countExports);
            for (int i = 0; i < countExports; i++) exports.Add(ReadCString());

            for (int i = 0; i < countExports + countDefinitions; i++)
            {
                uint index = ReadU32();
                Objects[index] = ReadObject();
            }
            return exports;
        }

        private Node ReadObject()
        {
            var stack = new Stack<Node>();
            while (true)
            {
                uint token = ReadU32();
                switch (token)
                {
                    case ObjectEnd:
                        if (stack.Count != 1)
                            throw new InvalidDataException($"ObjectEnd with stack count {stack.Count}");
                        return stack.Pop();
                    case ObjectFloat:
                        stack.Push(new NumberNode(ReadSingle()));
                        break;
                    case ObjectString:
                        stack.Push(new StringNode(ReadCString()));
                        break;
                    case ObjectTrue:
                        stack.Push(new NumberNode(1f));
                        break;
                    case ObjectFalse:
                        stack.Push(new NumberNode(0f));
                        break;
                    case ObjectList:
                    {
                        int count = (int)ReadU32();
                        var items = new List<Node>(count);
                        for (int i = 0; i < count; i++) items.Add(ReadObject());
                        stack.Push(new ListNode(items));
                        break;
                    }
                    case ObjectApply:
                    {
                        var fn = stack.Pop();
                        string name = fn is ImportNode import ? import.Name : "";
                        int arity = Arity(name);
                        var args = new List<Node>(arity);
                        for (int i = 0; i < arity && stack.Count > 0; i++) args.Insert(0, stack.Pop());
                        stack.Push(new ApplyNode(name, args));
                        break;
                    }
                    case ObjectBinary:
                    {
                        var fn = stack.Pop();
                        stack.Push(ReadBinary(fn, stack));
                        break;
                    }
                    case ObjectReference:
                    {
                        uint index = ReadU32();
                        stack.Push(Objects.TryGetValue(index, out var known) ? known : new NumberNode(0f));
                        break;
                    }
                    case ObjectImport:
                    {
                        int index = (int)ReadU32();
                        stack.Push(new ImportNode(index < _importNames.Count ? _importNames[index] : ""));
                        break;
                    }
                    case ObjectPair:
                    {
                        var second = stack.Pop();
                        var first = stack.Pop();
                        stack.Push(new PairNode(first, second));
                        break;
                    }
                    default:
                        throw new InvalidDataException($"bad token {token} at 0x{_pos - 4:x}");
                }
            }
        }

        /// <summary>Native factories pop their own arguments off the shared
        /// stack (<c>engine/mdlbin.cpp:121-133</c>); these arities mirror the
        /// members a frame-image namespace can contain
        /// (<c>engine/modeler.cpp:691-746</c>, <c>engine/frameimage.cpp:280-311</c>).</summary>
        private static int Arity(string name) => name switch
        {
            "ModifiableNumber" => 1,
            "ImportImage" or "FrameImage" => 2,
            _ => 0,
        };

        private Node ReadBinary(Node fn, Stack<Node> stack)
        {
            string name = fn is ImportNode import ? import.Name : "";
            return name switch
            {
                "FrameImage" => ReadFrameImageBinary(stack),
                "ImportImage" => ReadTextureBinary(),
                _ => throw new InvalidDataException($"unsupported binary member '{name}' in a frame image"),
            };
        }

        private TextureNode ReadTextureBinary()
        {
            var texture = ReadSurfaceTexture();
            _lastTexture = texture;
            return texture;
        }

        private TextureNode ReadSurfaceTexture()
        {
            int width = (int)ReadU32();
            int height = (int)ReadU32();
            int pitch = (int)ReadU32();
            uint bitCount = ReadU32();
            uint red = ReadU32();
            uint green = ReadU32();
            uint blue = ReadU32();
            _pos += 4;                                         // alpha mask
            _pos += 4;                                         // colorkey byte + pad (engine/enginep.h:313-324)
            if (bitCount != 16)
                throw new InvalidDataException($"only RGB565 (bitcount=16) handled, got {bitCount}");
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"invalid texture size {width}x{height}");
            if (pitch < width * 2)
                throw new InvalidDataException($"bad .mdl texture pitch {pitch} for {width}px rows");
            int pixelStart = _pos;
            _pos += pitch * height;
            Ensure(_pos);
            return new TextureNode(width, height, pitch, pixelStart, _data, red, green, blue);
        }

        private FrameNode ReadFrameImageBinary(Stack<Node> stack)
        {
            int frameCount = (int)ReadU32();
            if (frameCount <= 0 || frameCount > 4096)
                throw new InvalidDataException($"implausible FrameImage frame count {frameCount}");
            var offsets = new int[frameCount];
            for (int i = 0; i < frameCount; i++) offsets[i] = (int)ReadU32();
            int payloadStart = _pos;
            int payloadLength = offsets[^1];                    // the writer stores offsets[nFrame-1] bytes
            _pos += payloadLength;
            Ensure(_pos);

            // FrameImageFactory::Read pops the frame value and the background
            // image off the shared stack (engine/frameimage.cpp:290-293); the
            // background itself was parsed earlier in this object's stream.
            if (stack.Count > 0) stack.Pop();                  // frame (ModifiableNumber reference)
            if (stack.Count > 0) stack.Pop();                  // background image
            var background = _lastTexture
                ?? throw new InvalidDataException("FrameImage has no background ImportImage");
            return new FrameNode(background.Width, background.Height, background.Pitch,
                background.PixelStart, background.Data,
                background.RedMask, background.GreenMask, background.BlueMask,
                frameCount, offsets, payloadStart, payloadLength);
        }

        private uint ReadU32()
        {
            Ensure(_pos + 4);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        private float ReadSingle()
        {
            Ensure(_pos + 4);
            float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        private string ReadCString()
        {
            int end = Array.IndexOf(_data, (byte)0, _pos);
            if (end < 0) throw new InvalidDataException($"unterminated cstring at 0x{_pos:x}");
            string s = System.Text.Encoding.Latin1.GetString(_data, _pos, end - _pos);
            int length = end - _pos + 1;
            _pos += (length + 3) & ~3;
            Ensure(_pos);
            return s;
        }

        private void Ensure(int end)
        {
            if (end < 0 || end > _data.Length)
                throw new InvalidDataException("truncated .mdl frame image");
        }
    }
}

/// <summary>P945 — a decoded native <c>FrameImage</c> animation: the surface
/// size, its frames in play order (frame 0 is the background), and the
/// namespace's initial <c>frame</c> value.</summary>
public sealed record MdlFrameAnimation(
    int Width, int Height, IReadOnlyList<MdlTexture> Frames, float InitialFrame)
{
    public int FrameCount => Frames.Count;
}
