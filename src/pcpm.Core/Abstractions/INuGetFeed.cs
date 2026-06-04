using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

#pragma warning disable SA1652 // Enable XML documentation validation

/// <summary>
/// Abstraction over the NuGet v3 feed. The concrete implementation lives in <c>pcpm.Infrastructure</c>
/// and uses <see cref="HttpClient"/> via <c>IHttpClientFactory</c>.
/// All methods are async and accept a <see cref="CancellationToken"/>.
/// </summary>
public interface INuGetFeed
{
    /// <summary>List all available versions for a package id, newest first.</summary>
    Task<IReadOnlyList<PackageVersion>> ListVersionsAsync(PackageId id, CancellationToken ct);

    /// <summary>Get full metadata for a specific id+version (used to read dependency groups).</summary>
    Task<PackageMetadata> GetMetadataAsync(PackageId id, PackageVersion version, CancellationToken ct);

    /// <summary>Download the .nupkg bytes for a specific id+version, streaming to disk at <paramref name="destinationPath"/>.</summary>
    Task DownloadPackageAsync(PackageId id, PackageVersion version, string destinationPath, CancellationToken ct);
}
