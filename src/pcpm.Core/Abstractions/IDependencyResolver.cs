using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

/// <summary>
/// Pure-logic dependency resolver. Given a set of direct dependencies (one per project) and a way to
/// fetch transitive metadata from a feed, return a fully-resolved graph where every dependency has
/// an exact pinned version. No I/O happens inside the resolver itself — the feed does that.
/// </summary>
public interface IDependencyResolver
{
    /// <summary>
    /// Resolve the full transitive closure of the given direct dependencies.
    /// </summary>
    /// <param name="directDependencies">Direct PackageReferences across all projects, in the resolution target framework.</param>
    /// <param name="targetFramework">The TFM to use for picking the right dependency group (e.g. "net10.0").</param>
    /// <param name="feed">The feed to query for transitive metadata. Caller owns the feed's lifetime.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A resolution result with the chosen versions, conflicts, and the full transitive graph.</returns>
    Task<ResolutionResult> ResolveAsync(
        IReadOnlyList<PackageDependency> directDependencies,
        string targetFramework,
        INuGetFeed feed,
        CancellationToken ct);
}

/// <summary>
/// Outcome of dependency resolution. <see cref="Resolved"/> is keyed by package id and contains
/// the chosen version + its transitive dependencies. <see cref="Conflicts"/> lists packages that
/// could not be resolved (e.g. no version satisfied all accumulated constraints).
/// </summary>
public sealed record ResolutionResult(
    IReadOnlyDictionary<PackageId, ResolvedPackage> Resolved,
    IReadOnlyList<ResolutionConflict> Conflicts)
{
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>A single resolved package and its pinned dependencies.</summary>
public sealed record ResolvedPackage(
    PackageId Id,
    PackageVersion Version,
    IReadOnlyList<PackageDependency> Dependencies);

/// <summary>A version-range conflict: two or more paths required incompatible ranges of the same package.</summary>
public sealed record ResolutionConflict(
    PackageId Id,
    IReadOnlyList<VersionRangeRequest> Requests);

/// <summary>One side of a conflict: a requester and the version range it asked for.</summary>
public sealed record VersionRangeRequest(
    string Requester,
    string Range);
