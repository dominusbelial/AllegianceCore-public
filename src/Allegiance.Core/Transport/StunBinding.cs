using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Allegiance.Core.Transport;

/// <summary>P936 port extension: bounded RFC 5389 sections 6/15.2 Binding subset.
/// Optional private receipt attribute proves reception at the observed endpoint.
/// No DNS discovery, authentication, alternate servers or general STUN service.</summary>
public static class StunBinding
{
    public const uint Cookie = 0x2112a442;
    public const int RequestSize = 64, MaxSize = 256;
    public const ushort ReceiptAttribute = 0xc936;
    public static bool HasCookie(ReadOnlySpan<byte> packet) => packet.Length >= 8
        && BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) == Cookie;

    public static byte[] CreateRequest()
    {
        var packet = new byte[RequestSize];
        Header(packet, 0x0001, RequestSize - 20);
        RandomNumberGenerator.Fill(packet.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 0x8026); // PADDING
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), RequestSize - 24);
        return packet;
    }

    private static void Header(Span<byte> packet, ushort type, int size)
    {
        packet.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(packet, type);
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], (ushort)size);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], Cookie);
    }

    public static bool IsRequest(ReadOnlySpan<byte> packet) => Valid(packet, 0x0001);

    private static bool Valid(ReadOnlySpan<byte> packet, ushort type)
    {
        if (packet.Length is < 20 or > MaxSize || !HasCookie(packet)
            || BinaryPrimitives.ReadUInt16BigEndian(packet) != type
            || BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != packet.Length - 20
            || (packet.Length & 3) != 0) return false;
        for (int offset = 20; offset < packet.Length;)
        {
            if (offset + 4 > packet.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            ushort attribute = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            if (type == 0x0001 && attribute < 0x8000) return false; // unsupported required attr
            offset += 4 + ((length + 3) & ~3);
            if (offset > packet.Length) return false;
        }
        return true;
    }

    public static int WriteSuccess(ReadOnlySpan<byte> request, IPEndPoint observed, ReadOnlySpan<byte> receipt, Span<byte> output)
    {
        if (!IsRequest(request) || receipt.Length != 16) return 0;
        var ip = observed.Address.IsIPv4MappedToIPv6 ? observed.Address.MapToIPv4() : observed.Address;
        int addressBytes = ip.AddressFamily == AddressFamily.InterNetwork ? 4 : 16;
        // Only the padded game usage receives the receipt. The responder answers
        // unverified sources, so a reply is never larger than its request: short
        // requests (e.g. a bare 20-byte Binding) get no answer at all.
        bool includeReceipt = request.Length >= RequestSize;
        int size = 20 + 8 + addressBytes + (includeReceipt ? 20 : 0);
        if (size > request.Length || output.Length < size) return 0;
        var packet = output[..size];
        Header(packet, 0x0101, size - 20);
        request.Slice(8, 12).CopyTo(packet[8..]);
        BinaryPrimitives.WriteUInt16BigEndian(packet[20..], 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(packet[22..], (ushort)(addressBytes + 4));
        packet[25] = addressBytes == 4 ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(packet[26..], (ushort)(observed.Port ^ (Cookie >> 16)));
        ip.TryWriteBytes(packet[28..], out _);
        for (int i = 0; i < addressBytes; i++) packet[28 + i] ^= packet[4 + i];
        if (!includeReceipt) return size;
        int tail = 28 + addressBytes;
        BinaryPrimitives.WriteUInt16BigEndian(packet[tail..], ReceiptAttribute);
        BinaryPrimitives.WriteUInt16BigEndian(packet[(tail + 2)..], 16);
        receipt.CopyTo(packet[(tail + 4)..]);
        return size;
    }

    public static bool TryReadSuccess(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> transaction,
        out IPEndPoint? endpoint, out string? receipt)
    {
        endpoint = null; receipt = null;
        if (transaction.Length != 12 || !Valid(packet, 0x0101) || !packet.Slice(8, 12).SequenceEqual(transaction)) return false;
        Span<byte> address = stackalloc byte[16];
        for (int offset = 20; offset < packet.Length;)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            var value = packet.Slice(offset + 4, length);
            if (type == 0x0020)
            {
                if (endpoint is not null || length < 4 || value[0] != 0) return false;
                int count = value[1] == 1 ? 4 : value[1] == 2 ? 16 : 0;
                if (count == 0 || length != count + 4) return false;
                for (int i = 0; i < count; i++) address[i] = (byte)(value[i + 4] ^ packet[4 + i]);
                int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (int)(Cookie >> 16);
                if (port == 0) return false;
                endpoint = new IPEndPoint(new IPAddress(address[..count]), port);
            }
            else if (type == ReceiptAttribute)
            {
                if (length != 16 || receipt is not null) return false;
                receipt = Convert.ToHexString(value);
            }
            else if (type < 0x8000) return false;
            offset += 4 + ((length + 3) & ~3);
        }
        return endpoint is not null;
    }
}
