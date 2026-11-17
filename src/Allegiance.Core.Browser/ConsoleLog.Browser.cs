namespace Allegiance.Core;

// The browser has one event loop and no console-drain thread. These diagnostics
// are not reached by the intro; keep shared model logging safe for later slices.
internal static class ConsoleLog
{
    public static void Write(string line) => Console.WriteLine(line);
}
