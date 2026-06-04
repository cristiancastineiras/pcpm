using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

/// <summary>
/// Reads and writes the central <c>Directory.Packages.props</c> file.
/// In CPM mode, this file owns the &lt;PackageVersion&gt; entries and the
/// <c>ManagePackageVersionsCentrally</c> flag.
/// </summary>
public interface ICpmFileService
{
    /// <summary>Path to the CPM file (typically <c>Directory.Packages.props</c> at the repo root).</summary>
    string CpmFileName { get; }

    /// <summary>Read the CPM file. Returns a default empty object if not found.</summary>
    Task<CentralPackageManagement> ReadAsync(string directory, CancellationToken ct);

    /// <summary>Add or update a package version in the CPM file. Idempotent.</summary>
    Task SetPackageVersionAsync(string directory, PackageId id, PackageVersion version, CancellationToken ct);

    /// <summary>Remove a package version entry from the CPM file. No-op if absent.</summary>
    Task RemovePackageVersionAsync(string directory, PackageId id, CancellationToken ct);

    /// <summary>Write the entire CPM model to disk, atomically.</summary>
    Task WriteAsync(string directory, CentralPackageManagement cpm, CancellationToken ct);
}
