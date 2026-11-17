using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(IntroPreferences))]
[JsonSerializable(typeof(PlayerConfig))]
internal partial class BrowserProfileJson : JsonSerializerContext;
