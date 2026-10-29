namespace Allegiance.Server;

/// <summary>Process-lifetime standalone score identity, distinct from verified
/// online account subjects. FedSrv/FedSrv.CPP:65-84,5881-5886.</summary>
internal static class StandaloneCharacterIds
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static int _next;
    internal static int Get(string name)
    {
        lock (Gate)
        {
            if (!Ids.TryGetValue(name, out int id)) Ids.Add(name, id = ++_next);
            return id;
        }
    }
}
