using System.Text;
using System.Text.Json;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Optional local identity fixture. The proof host creates lobby
/// accounts and signs ordinary audience-bound join tickets; the server keeps
/// AllowAnonymous=false. No production listener or authentication bypass.</summary>
internal static class BotLogonIdentity
{
    internal sealed record Settings(string ClientVersion, string MissionPassword, string BrokerDirectory);
    internal sealed record Reply(string Callsign, string Token);
    internal static string? FilePath { get; set; }
    internal static void Apply(LogonRequest request, string name) => Apply(request, name, FilePath);
    internal static void Apply(LogonRequest request, string name, string? filePath)
    {
        if (filePath is null) return;
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(filePath)) ?? throw new InvalidDataException("Invalid bot logon fixture.");
        string nonce = Guid.NewGuid().ToString("N");
        string input = Path.Combine(settings.BrokerDirectory, nonce + ".request");
        string output = Path.Combine(settings.BrokerDirectory, nonce + ".reply");
        try
        {
            File.WriteAllText(input + ".tmp", JsonSerializer.Serialize(new { name }));
            File.Move(input + ".tmp", input);
            long deadline = Environment.TickCount64 + 15000;
            while (!File.Exists(output))
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Proof identity broker did not answer.");
                Thread.Sleep(20);
            }
            var reply = JsonSerializer.Deserialize<Reply>(File.ReadAllText(output)) ?? throw new InvalidDataException("Invalid bot identity reply.");
            request.CharacterName = reply.Callsign;
            request.ZoneTicket = Encoding.UTF8.GetBytes(reply.Token);
            request.ClientVersion = settings.ClientVersion;
            request.MissionPassword = settings.MissionPassword;
        }
        finally { File.Delete(input); File.Delete(input + ".tmp"); File.Delete(output); }
    }
}
