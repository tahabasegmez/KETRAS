using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Ketras.Simulation.Px4Sitl;

/// <summary>The outcome of a finished docker command.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">What it wrote to standard output.</param>
/// <param name="Error">What it wrote to standard error.</param>
internal sealed record DockerResult(int ExitCode, string Output, string Error)
{
    /// <summary>It succeeded.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Adapter over the <c>docker</c> command line: finished commands with their output, and long-running ones whose lines
/// are handed over as they come. Arguments are passed as a list, never through a shell.
/// </summary>
/// <param name="command">The docker executable (a fake one in tests).</param>
internal sealed class DockerCli(string command)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs a command to its end; <see cref="Win32Exception"/> when docker is not installed.</summary>
    public async Task<DockerResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        var start = StartInfo(arguments);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{command} did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout ?? CommandTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            return new DockerResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>Starts a long-running command; each output line (standard output and error) goes to <paramref name="line"/>.</summary>
    public Process Launch(IEnumerable<string> arguments, bool redirectInput, Action<string?> line)
    {
        var start = StartInfo(arguments);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = redirectInput;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => line(e.Data);
        process.ErrorDataReceived += (_, e) => line(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Ends a process and its children; nothing happens when it is gone.</summary>
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private ProcessStartInfo StartInfo(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
