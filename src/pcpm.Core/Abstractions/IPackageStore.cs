using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

/// <summary>
/// The pnpm-style content-addressable store. Layout:
/// <code>
/// &lt;root&gt;/
///   v1/
///     &lt;contentHash&gt;/
///       pkg.nupkg                  # the immutable downloaded payload
///       extracted/                 # the unzipped package contents
///         &lt;id&gt;/&lt;version&gt;/lib/net10.0/X.dll
/// </code>
/// All writes are content-addressable: two different package versions hash to two different directories.
/// </summary>
public interface IPackageStore
{
    /// <summary>Absolute path to the store root (e.g. <c>%LOCALAPPDATA%/pcpm/store</c> on Windows).</summary>
    string RootPath { get; }

    /// <summary>True if a package with the given content hash is already in the store.</summary>
    bool Contains(string contentHash);

    /// <summary>Compute the SHA-256 of a file and return it as lowercase hex.</summary>
    Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct);

    /// <summary>Materialise a downloaded .nupkg into the store under its content hash.
    /// If a package with the same hash is already present, this is a no-op (idempotent).</summary>
    /// <returns>The content hash actually used as the store key.</returns>
    Task<string> MaterializeAsync(string nupkgPath, CancellationToken ct);

    /// <summary>Hardlink (or copy, if hardlinks disabled) the stored package's extracted files
    /// into the global NuGet packages folder at <c>~/.nuget/packages/{id}/{version}/</c>.
    /// This is what makes <c>dotnet restore</c> see the package without pcpm having to reimplement MSBuild.</summary>
    Task LinkToGlobalPackagesAsync(string contentHash, PackageId id, PackageVersion version, CancellationToken ct);

    /// <summary>Get disk-usage stats for the store. Used by <c>pcpm store status</c>.</summary>
    Task<StoreStats> GetStatsAsync(CancellationToken ct);
}

/// <summary>Snapshot of the store's disk usage. All sizes are in bytes.</summary>
public sealed record StoreStats(long TotalBytes, int PackageCount, string RootPath);
