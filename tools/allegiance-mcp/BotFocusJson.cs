using System.Text.Json.Serialization;

namespace Allegiance.Tools.McpSpawner;

[JsonSerializable(typeof(short?))]
[JsonSerializable(typeof(bool))]
internal sealed partial class BotFocusJson : JsonSerializerContext;
