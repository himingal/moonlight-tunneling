using System.Runtime.InteropServices;

namespace MoonlightTunneling.Platform;

/// <summary>
/// PIDs that currently own a TCP or UDP socket, read from the same Windows
/// tables sing-box uses to attribute connections to processes. Used to list
/// "apps with network right now" when adding an app.
/// </summary>
public static class ConnectionTables
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public static HashSet<int> PidsWithSockets()
    {
        var pids = new HashSet<int>();
        // Row sizes and the offset of dwOwningPid inside each MIB_*ROW_OWNER_PID struct.
        Collect(pids, true, AF_INET, rowSize: 24, pidOffset: 20);
        Collect(pids, true, AF_INET6, rowSize: 56, pidOffset: 52);
        Collect(pids, false, AF_INET, rowSize: 12, pidOffset: 8);
        Collect(pids, false, AF_INET6, rowSize: 28, pidOffset: 24);
        pids.Remove(0);
        pids.Remove(4);
        return pids;
    }

    private static void Collect(HashSet<int> pids, bool tcp, int af, int rowSize, int pidOffset)
    {
        int size = 0;
        uint rc = tcp
            ? Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
            : Native.GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0) return;

        // The table can grow between the two calls; retry a couple of times.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                rc = tcp
                    ? Native.GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                    : Native.GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                if (rc != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                    pids.Add(Marshal.ReadInt32(buf, 4 + i * rowSize + pidOffset));
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}
