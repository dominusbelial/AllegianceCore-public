using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Server;

/// <summary>
/// Source-generated <c>System.Text.Json</c> contract (P185) covering every type
/// that crosses a JSON boundary in <c>Allegiance.Server</c>, so the process can
/// publish as a Native AOT binary with no reflection-based serialization:
///
///   - <see cref="ServerConfig"/> / <see cref="GameConfig"/> / <see cref="LobbyConfig"/>
///     — the <c>server.json</c> config file, read case-insensitively and tolerating
///     comments and trailing commas exactly as the former reflection-based loader did.
///   - <see cref="LobbyRegisterRequest"/> / <see cref="LobbyRegisterResponse"/> — the
///     lobby-heartbeat HTTP payloads, written camelCase to a byte-identical wire shape.
///
/// Config properties use setters so absent JSON members retain their initializers.
/// Init-only members can be assigned default(T) by the generated constructor path;
/// the reflection guard in ConfigDefaultsJsonTests covers this entire contract graph.
///
/// A single option set reproduces the union of both former paths' behaviour:
/// <see cref="JsonSourceGenerationOptionsAttribute.PropertyNamingPolicy"/> =
/// CamelCase only affects WRITES (the heartbeat request — the old
/// <c>System.Net.Http.Json</c> Web defaults serialized camelCase), while all reads
/// are case-insensitive so the PascalCase-named config types bind to camelCase
/// config keys identically. No JSON bytes that crossed a boundary change versus the
/// pre-AOT reflection-based paths.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(GameConfig))]
[JsonSerializable(typeof(Allegiance.Core.Model.MissionGoals))]
[JsonSerializable(typeof(LobbyConfig))]
[JsonSerializable(typeof(LiveSettingsConfig))]
[JsonSerializable(typeof(TestHarnessConfig))]
[JsonSerializable(typeof(TestUnlockConfig))]
[JsonSerializable(typeof(AdminCheatsConfig))]
[JsonSerializable(typeof(DiscoveryConfig))]
[JsonSerializable(typeof(ReconnectConfig))]
[JsonSerializable(typeof(UpdatesConfig))]
[JsonSerializable(typeof(MapGenConfig))]
[JsonSerializable(typeof(LobbyRegisterRequest))]
[JsonSerializable(typeof(LobbyRegisterResponse))]
// P871b — the lobby's new-game relay: the requests a server may claim (an array
// of GameDto-shaped records) and the claim body it posts back.
[JsonSerializable(typeof(ClaimedGame))]
[JsonSerializable(typeof(ClaimedGame[]))]
[JsonSerializable(typeof(GameClaimRequest))]
[JsonSerializable(typeof(LobbyMatchReport))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Allegiance.Core.Transport.EgressWindow))]
internal sealed partial class ServerJsonContext : JsonSerializerContext
{
}
