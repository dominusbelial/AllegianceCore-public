using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class ConfigDefaultsJsonTests
{
    [Fact]
    public void Mcp_worker_json_graphs_preserve_all_defaults()
    {
        // MCP options are CLI/environment based, not JSON configs. These are its
        // incoming worker RPC contracts; production uses reflection JSON defaults.
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        foreach (var type in new[] { typeof(WorkerReply), typeof(WorkerState), typeof(CoordinationDecisions) })
            JsonDefaultsGuard.Graph(options.GetTypeInfo(type));
    }
}
