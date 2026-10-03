using Android.Content;
using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.Android;

/// <summary>
/// P500 — the Android implementation of <see cref="IConnectStore"/>: the
/// in-app connect screen's saved servers / player name / lobby URL / last-used
/// server, persisted as one JSON file in the app's PRIVATE data dir
/// (<c>FilesDir</c>). The file is our own shape (<see cref="ConnectProfile"/>
/// serialization), tolerated on corrupt/unreadable load so the app still boots.
/// </summary>
public sealed class AndroidConnectStore : IConnectStore
{
    private readonly Context _context;
    private const string FileName = "connect-profile.json";

    public AndroidConnectStore(Context context)
    {
        _context = context;
    }

    private string ProfilePath
        => Path.Combine(_context.FilesDir!.AbsolutePath, FileName);

    public ConnectProfile Load()
    {
        try
        {
            if (File.Exists(ProfilePath))
            {
                string json = File.ReadAllText(ProfilePath);
                var profile = ConnectProfile.Parse(json);
                Console.WriteLine($"[android] connect profile loaded: {profile.Servers.Count} servers, name '{profile.PlayerName}'");
                return profile;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[android] connect-profile unreadable ({ex.Message}); starting empty");
        }
        return new ConnectProfile();
    }

    public void Save(ConnectProfile profile)
    {
        try
        {
            File.WriteAllText(ProfilePath, profile.Serialize());
            Console.WriteLine($"[android] connect profile saved ({profile.Servers.Count} servers, '{profile.PlayerName}')");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[android] FAILED to save connect profile: {ex.Message}");
        }
    }
}
