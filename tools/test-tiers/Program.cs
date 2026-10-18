using System.Text.Json;
using Allegiance.TestInfra;

Console.WriteLine(JsonSerializer.Serialize(new
{
    factor = PerfCalibration.CpuSpeedFactor,
    referenceMs = PerfCalibration.RawReferenceMs,
    nominalMs = PerfCalibration.NominalReferenceMs,
    fastLimitSeconds = Math.Max(10, PerfCalibration.Scale(10)),
}));
