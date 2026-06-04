namespace pcpm.Core.Abstractions;

/// <summary>
/// Runs an external process (notably <c>dotnet restore</c> and <c>dotnet build</c>) and surfaces
/// exit code, stdout, stderr. Used by <c>pcpm install</c> to hand off to the MSBuild restore after
/// the store is pre-warmed.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct);
}

/// <summary>Request to run a process.</summary>
public sealed record ProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

/// <summary>Outcome of a process run. Captures stdout/stderr text + exit code.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
