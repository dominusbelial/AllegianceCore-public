using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    [ThreadStatic] private static ConnectionSession? _frameSession;
    [ThreadStatic] private static DecisionSnapshot? _frame;
    [ThreadStatic] private static long _frameNow;

    private static DecisionSnapshot SnapshotFor(ConnectionSession cs) =>
        _frameSession == cs && _frame is { } frame ? frame : cs.World.Decision;

    // Nested helpers reuse their caller's frame and time, including scenario controllers.
    private readonly struct DecisionFrameScope : IDisposable
    {
        private readonly ConnectionSession? _previousSession;
        private readonly DecisionSnapshot? _previousFrame;
        private readonly long _previousNow;
        public DecisionFrameScope(ConnectionSession? cs, DecisionSnapshot? prepared = null)
        {
            _previousSession = _frameSession;
            _previousFrame = _frame;
            _previousNow = _frameNow;
            if (cs is not null && _frameSession != cs)
            {
                _frameSession = cs;
                _frame = prepared ?? cs.World.Decision;
                _frameNow = Environment.TickCount64;
            }
        }
        public void Dispose()
        {
            _frameSession = _previousSession;
            _frame = _previousFrame;
            _frameNow = _previousNow;
        }
    }
}
