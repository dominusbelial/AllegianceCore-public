using Allegiance.Core.Model;

namespace Allegiance.Server;

public sealed partial class Server
{
    private long _skillMatchNumber;
    private int _skillsRequested;
    /// <summary>Thread-safe console/embedding query, emitted on the next simulation pass.</summary>
    public void RequestSkills() => Interlocked.Exchange(ref _skillsRequested, 1);
    private void PrintRequestedSkills()
    {
        if (Interlocked.Exchange(ref _skillsRequested, 0) == 0) return;
        foreach (var mission in _missions) mission.Skills?.Write(Console.Out);
    }
}
