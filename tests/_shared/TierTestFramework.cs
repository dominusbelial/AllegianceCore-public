using System.Text.Json;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Allegiance.TestInfra;

/// <summary>One discoverable Tier per test, including facts from third-party
/// discoverers (SkippableFact) and every theory row. Other traits are untouched.</summary>
public sealed class TierTestFramework(IMessageSink sink) : XunitTestFramework(sink)
{
    protected override ITestFrameworkDiscoverer CreateDiscoverer(IAssemblyInfo assemblyInfo)
        => new TierDiscoverer(assemblyInfo, SourceInformationProvider, DiagnosticMessageSink);
}

public sealed class TierDiscoverer(
    IAssemblyInfo assembly, ISourceInformationProvider source, IMessageSink sink)
    : XunitTestFrameworkDiscoverer(assembly, source, sink)
{
    private static readonly object ManifestGate = new();

    protected override bool FindTestsForMethod(ITestMethod testMethod, bool includeSourceInformation,
        IMessageBus messageBus, ITestFrameworkDiscoveryOptions discoveryOptions)
        => base.FindTestsForMethod(testMethod, includeSourceInformation, new TierMessageBus(messageBus), discoveryOptions);

    private sealed class TierMessageBus(IMessageBus inner) : IMessageBus
    {
        public void Dispose() { } // The discoverer owns the wrapped bus.
        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestCaseDiscoveryMessage discovered) ApplyTier(discovered.TestCase);
            return inner.QueueMessage(message);
        }
    }

    public static void EnsureTier(IDictionary<string, List<string>> traits, string name)
    {
        if (!traits.TryGetValue("Tier", out var tiers)) traits["Tier"] = tiers = ["Fast"];
        if (tiers.Count != 1 || tiers[0] is not ("Fast" or "Live" or "Perf"))
            throw new InvalidOperationException($"{name}: expected exactly one Tier=Fast|Live|Perf; got {string.Join(",", tiers)}");
    }

    private static void ApplyTier(ITestCase testCase)
    {
        var manifest = Environment.GetEnvironmentVariable("TT1_DISCOVERY_MANIFEST");
        try
        {
            RecordCase(testCase, manifest);
        }
        catch (Exception error)
        {
            // xUnit may report discovery errors yet return success for the
            // remaining cases. Preserve them for the runner's audit too.
            if (!string.IsNullOrEmpty(manifest))
                lock (ManifestGate) File.AppendAllText(manifest,
                    JsonSerializer.Serialize(new { error = error.Message }) + Environment.NewLine);
            throw;
        }
    }

    private static void RecordCase(ITestCase testCase, string? manifest)
    {
        EnsureTier(testCase.Traits, testCase.DisplayName);
        var tiers = testCase.Traits["Tier"];

        // Used by the runner to audit discovery coverage and map TRX rows back
        // to traits. Separate files per assembly; discovery itself can be concurrent.
        if (!string.IsNullOrEmpty(manifest))
        {
            // xUnit lists a single container for non-serializable theory data,
            // then expands it during execution. Record the expected runtime rows
            // too, without executing any test or changing xUnit serialization.
            var names = new List<string> { testCase.DisplayName };
            if (testCase is XunitTheoryTestCase)
            {
                var method = testCase.TestMethod.Method.ToRuntimeMethod();
                names = method.GetCustomAttributes(true).OfType<DataAttribute>()
                    .SelectMany(attribute => attribute.GetData(method))
                    .Select(arguments => testCase.TestMethod.Method.GetDisplayNameWithArguments(
                        testCase.DisplayName, arguments, null)).ToList();
                if (names.Count == 0) throw new InvalidOperationException($"{testCase.DisplayName}: no theory data");
            }
            for (int index = 0; index < names.Count; index++)
            {
                var row = JsonSerializer.Serialize(new
                {
                    id = testCase.UniqueID + ":" + index,
                    discoveryId = testCase.UniqueID,
                    discoveryName = testCase.DisplayName,
                    name = names[index],
                    method = testCase.TestMethod.TestClass.Class.Name + "." + testCase.TestMethod.Method.Name,
                    tier = tiers[0], traits = testCase.Traits,
                });
                lock (ManifestGate) File.AppendAllText(manifest, row + Environment.NewLine);
            }
        }
    }
}
