using System.Diagnostics;
using pcpm.Core.Abstractions;

namespace pcpm.Infrastructure.Process;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// Streams stdout/stderr to captured strings; the caller can tee them through Spectre.Console
/// for live output if needed.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(request.WorkingDirectory))
        {
            psi.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (var (k, v) in request.EnvironmentVariables)
            {
                psi.Environment[k] = v;
            }
        }

        using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{request.Executable}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
