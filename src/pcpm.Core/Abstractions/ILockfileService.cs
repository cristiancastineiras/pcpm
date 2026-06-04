using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

/// <summary>
/// Reads and writes the <c>pcpm.lock</c> file at the workspace root.
/// JSON format with deterministic ordering — safe to commit to source control.
/// </summary>
public interface ILockfileService
{
    /// <summary>Read the existing lockfile, or return a fresh empty one if none exists.</summary>
    Task<Lockfile> ReadOrEmptyAsync(string workspaceRoot, CancellationToken ct);

    /// <summary>Write the lockfile atomically (write to temp + rename) and pretty-print.</summary>
    Task WriteAsync(string workspaceRoot, Lockfile lockfile, CancellationToken ct);
}
