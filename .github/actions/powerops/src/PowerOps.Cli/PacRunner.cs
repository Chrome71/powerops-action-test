using System.ComponentModel;
using System.Diagnostics;

namespace PowerOps.Cli;

public sealed record PacResult(bool Started, bool TimedOut, int ExitCode, string Stdout, string Stderr = "");

// Runs pac as a subprocess. Arguments go through ArgumentList, never a shell string.
public sealed class PacRunner(string executable = "pac", TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public PacResult Run(IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        args.ToList().ForEach(info.ArgumentList.Add);

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (Win32Exception)
        {
            return new PacResult(false, false, 0, "");
        }

        using (process)
        {
            process.StandardInput.Close();
            var clock = Stopwatch.StartNew();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // The timeout covers the exit and both stream reads: a child that inherits the pipes can keep them open after pac exits.
            if (!process.WaitForExit(_timeout) || !Task.WaitAll([stdout, stderr], Remaining(clock)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the wait and the kill.
                }

                return new PacResult(true, true, 0, "");
            }

            return new PacResult(true, false, process.ExitCode, stdout.Result, stderr.Result);
        }
    }

    private TimeSpan Remaining(Stopwatch clock) => _timeout > clock.Elapsed ? _timeout - clock.Elapsed : TimeSpan.Zero;
}