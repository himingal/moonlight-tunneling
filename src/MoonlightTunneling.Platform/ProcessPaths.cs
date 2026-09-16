using System.Diagnostics;
using System.Text;

namespace MoonlightTunneling.Platform;

public readonly record struct ProcessEntry(int Pid, string Path);

public static class ProcessPaths
{
    /// <summary>Full image path of a process, or null when it can't be opened (system/protected processes).</summary>
    public static string? GetPath(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>All processes whose image path can be read. Cheap enough to poll every few seconds.</summary>
    public static List<ProcessEntry> Snapshot()
    {
        // EnumProcesses can't say how much room it needed: grow until it stops filling the buffer.
        var pids = new uint[1024];
        int needed;
        while (true)
        {
            if (!Native.EnumProcesses(pids, pids.Length * sizeof(uint), out needed)) return [];
            if (needed < pids.Length * sizeof(uint)) break;
            pids = new uint[pids.Length * 2];
        }
        int count = needed / sizeof(uint);
        var list = new List<ProcessEntry>(count);
        for (int i = 0; i < count; i++)
        {
            if (pids[i] <= 4) continue;
            var path = GetPath((int)pids[i]);
            if (path != null) list.Add(new ProcessEntry((int)pids[i], path));
        }
        return list;
    }

    /// <summary>Kills every process whose path satisfies the predicate. Returns how many were killed.</summary>
    public static int KillWhere(Func<string, bool> pathMatches, TimeSpan wait)
    {
        int killed = 0;
        var victims = new List<Process>();
        foreach (var p in Snapshot())
        {
            if (p.Pid == Environment.ProcessId || !pathMatches(p.Path)) continue;
            try
            {
                var proc = Process.GetProcessById(p.Pid);
                proc.Kill(entireProcessTree: true);
                victims.Add(proc);
                killed++;
            }
            catch
            {
                // already gone or not ours to kill
            }
        }
        var deadline = DateTime.UtcNow + wait;
        foreach (var v in victims)
        {
            var left = deadline - DateTime.UtcNow;
            if (left > TimeSpan.Zero)
            {
                try { v.WaitForExit(left); } catch { }
            }
            v.Dispose();
        }
        return killed;
    }
}
