namespace pcpm.Core.Models;

/// <summary>
/// A package as installed in the local content-addressable store.
/// </summary>
/// <param name="Id">Package identifier.</param>
/// <param name="Version">Exact resolved version (no floating).</param>
/// <param name="ContentHash">SHA-256 of the .nupkg file (lowercase hex). Used as the store key.</param>
/// <param name="Dependencies">Transitive dependencies at the resolution-time target framework.</param>
public sealed record StoredPackage(
    PackageId Id,
    PackageVersion Version,
    string ContentHash,
    IReadOnlyList<PackageDependency> Dependencies);
