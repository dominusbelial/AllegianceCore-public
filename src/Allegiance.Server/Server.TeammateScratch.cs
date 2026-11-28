namespace Allegiance.Server;

public sealed partial class Server
{
    // Indexed by the current frozen peer order. No connection or ship identity
    // survives apply. Only the worker assigned index i can write slot i.
    private BroadcastPeerBuild?[] _teammateResults = [];

    private sealed class BroadcastPeerBuild
    {
        public List<(BroadcastPacket Packet, int Key, bool Guaranteed)>? Packets;
        public List<BroadcastMessage>? Positions;
        public List<string>? VerboseLogs;
        public BroadcastPacket? OwnPose;
        public BroadcastPose? PendingPose;
        public bool SetStatus;
        public short StatusSector;
        public long StatusTick;
        public bool SetClusterInfo;
        public long ClusterInfoTick;
        public long? NextTeammateRefreshTick;
        public HashSet<short>? ExportSent, PositionSent, NpcExportSent;
        public Dictionary<short, long>? TeammateStatusTick, LoadoutVersionSet, NpcStatusTick;

        public void Reset()
        {
            // A build/apply failure can leave this lease outside the snapshot.
            OwnPose?.Release();
            OwnPose = null;
            PendingPose = null;
            SetStatus = SetClusterInfo = false;
            NextTeammateRefreshTick = null;
            Reset(ref Packets);
            Reset(ref Positions);
            Reset(ref VerboseLogs);
            Reset(ref ExportSent);
            Reset(ref PositionSent);
            Reset(ref NpcExportSent);
            Reset(ref TeammateStatusTick);
            Reset(ref LoadoutVersionSet);
            Reset(ref NpcStatusTick);
        }

        // Retain small steady-state buffers. Drop cold-join / burst capacity
        // immediately, including larger prime-sized hash bucket arrays.
        private const int RetainedCapacity = 64;

        private static void Reset<T>(ref List<T>? list)
        {
            if (list?.Capacity > RetainedCapacity) list = null;
            else list?.Clear();
        }

        private static void Reset(ref HashSet<short>? set)
        {
            if (set?.Capacity > RetainedCapacity) set = null;
            else set?.Clear();
        }

        private static void Reset(ref Dictionary<short, long>? map)
        {
            if (map?.Capacity > RetainedCapacity) map = null;
            else map?.Clear();
        }
    }
}
