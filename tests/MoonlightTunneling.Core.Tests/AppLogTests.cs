using MoonlightTunneling.Core;
using Xunit;

namespace MoonlightTunneling.Core.Tests;

public class AppLogTests
{
    static AppLogTests() => TestEnv.Init();

    /// <summary>Regression: a UI error re-raising itself wrote ~10 MB of the same line in minutes.</summary>
    [Fact]
    public void RepeatedMessageIsCollapsed()
    {
        var seen = new List<string>();
        void Handler(LogEntry e) { lock (seen) seen.Add(e.Message); }
        AppLog.Added += Handler;
        try
        {
            var msg = "loop-" + Guid.NewGuid();
            for (int i = 0; i < 1000; i++) AppLog.Error(msg);
            AppLog.Info("after-" + msg);
            lock (seen)
            {
                Assert.Equal(1, seen.Count(m => m == msg));
                Assert.Contains(seen, m => m.Contains("repeated 999"));
            }
        }
        finally
        {
            AppLog.Added -= Handler;
        }
    }
}
