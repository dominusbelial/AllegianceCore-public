using Allegiance.Tools.P928;

if (args.Length == 2 && args[0] == "--allocations")
{ AllocationReport.Write(args[1]); return; }

bool supplemental = args.Length == 2 && args[0] == "--supplemental";
if (args.Length != 1 && !supplemental) throw new ArgumentException("usage: p928-allocation-proof <output.csv>");
var trace = P928Trace.Run(supplemental);
File.WriteAllLines(args[supplemental ? 1 : 0], trace);
Console.WriteLine($"P928 trace: {trace.Length - 2} ticks; {trace[^1]}");
