namespace pcpm.Core.Models;

/// <summary>
/// The pcpm.lock file format. Deterministic ordering is guaranteed by the writer — the consumer
/// can rely on <see cref="Packages"/> being sorted by id, then version.
/// </summary>
public sealed record Lockfile
{
    /// <summary>Lockfile format version. Bump on incompatible schema changes.</summary>
    public int LockfileVersion { get; init; } = 1;

    /// <summary>UTC timestamp when the lockfile was last regenerated.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The pcpm version that produced this lockfile.</summary>
    public string PcpmVersion { get; init; } = "0.0.0";

    /// <summary>All packages resolved across the workspace, deduped by id+version.</summary>
    public required IReadOnlyList<LockedPackage> Packages { get; init; }

    /// <summary>Per-project dependency graph, listing only DIRECT dependencies.
    /// Transitive deps are reachable via <see cref="LockedPackage.Dependencies"/>.</summary>
    public required IReadOnlyList<LockedProject> Projects { get; init; }
}

/// <summary>
/// A single locked package entry in the store.
/// </summary>
public sealed record LockedPackage(
    PackageId Id,
    PackageVersion Version,
    string ContentHash,
    IReadOnlyList<LockedDependency> Dependencies);

/// <summary>
/// A reference to another locked package by id+version (no hashes — id+version is the stable identity).
/// </summary>
public sealed record LockedDependency(PackageId Id, PackageVersion Version);

/// <summary>
/// A project's set of direct dependencies, recorded so pcpm can re-resolve cleanly.
/// </summary>
public sealed record LockedProject(
    string ProjectPath,
    IReadOnlyList<LockedDependency> DirectDependencies);
