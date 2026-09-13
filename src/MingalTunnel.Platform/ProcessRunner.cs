using System.Diagnostics;
using System.Text;

namespace MingalTunnel.Platform;

public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => (StdOut + "\n" + StdErr).Trim();
}

public static class ProcessRunner
{
    public static async Task<RunResult> RunAsync(string exe, string args, string? stdin = null,
        string? workingDir = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? "",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin != null)
        {
            // No BOM: sing-box's JSON decoder rejects one at stdin.
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await p.StandardInput.BaseStream.WriteAsync(bytes);
            p.StandardInput.Close();
        }
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new RunResult(-1, "", "timeout");
        }
        return new RunResult(p.ExitCode, await outTask, await errTask);
    }
}
