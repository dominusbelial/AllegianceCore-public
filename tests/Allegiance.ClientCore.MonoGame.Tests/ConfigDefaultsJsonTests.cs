using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class ConfigDefaultsJsonTests
{
    [Fact]
    public void Client_settings_json_preserves_all_defaults()
    {
        JsonDefaultsGuard.Graph(new JsonSerializerOptions(ConnectConfigJson.Options)
            { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(ConnectConfigValues)));
        JsonDefaultsGuard.Graph(new JsonSerializerOptions(ConnectProfile.JsonOptions)
            { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(ConnectProfile)));
        JsonDefaultsGuard.Graph(new JsonSerializerOptions
            { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(CommandGridPreference.Settings)));
        JsonDefaultsGuard.Equal(new ConnectConfigValues(), ConnectConfigJson.Parse("{}").Values, "connect.json {}");
        JsonDefaultsGuard.Equal(new ConnectProfile(), ConnectProfile.Parse("{}"), "profile {}");
    }
}
