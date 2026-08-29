namespace Allegiance.Core.Wire;

/// <summary>A wire-encodable message. Decoding is a static member on each concrete
/// type because the reader must know the exact field layout.</summary>
public interface IFedMessage
{
    /// <summary>The numeric <c>fmid</c> written at header offset 2.</summary>
    ushort GetFmid();

    /// <summary>Encode this message to its exact wire bytes (header + fixed + blobs).</summary>
    byte[] Encode();
}
