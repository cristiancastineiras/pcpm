using NuGet.Frameworks;
using NuGet.Versioning;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Core.Services;

/// <summary>
/// Pure-logic dependency resolver. BFS over the dependency graph with union-of-constraints
/// semantics: when a package is required by multiple paths, we pick the highest version that
/// satisfies ALL accumulated constraints. No I/O happens here — the <see cref="INuGetFeed"/>
/// is injected and the resolver awaits it on demand.
/// </summary>
public sealed class DependencyResolver : IDependencyResolver
{
    public async Task<ResolutionResult> ResolveAsync(
        IReadOnlyList<PackageDependency> directDependencies,
        string targetFramework,
        INuGetFeed feed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directDependencies);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        var resolved = new Dictionary<PackageId, ResolvedPackage>();
        var constraints = new Dictionary<PackageId, List<VersionRangeRequest>>();

        // Seed direct-dep constraints.
        foreach (var dep in directDependencies)
            AddConstraint(constraints, dep.Id, new VersionRangeRequest(Requester: "(direct)", Range: dep.RangeText));

        // Wave-based parallel BFS:
        //   Each iteration takes all unresolved packages with known constraints and fetches
        //   their versions + metadata concurrently.  Results are collected single-threadedly
        //   before the next wave so constraint accumulation stays correct.
        var wave = new HashSet<PackageId>(directDependencies.Select(d => d.Id));

        while (wave.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Snapshot constraints for each id in this wave so tasks read a stable copy.
            var waveTasks = wave
                .Where(id => !resolved.ContainsKey(id))
                .Select(id =>
                {
                    var reqs = constraints.TryGetValue(id, out var r)
                        ? (IReadOnlyList<VersionRangeRequest>)r.ToList()
                        : Array.Empty<VersionRangeRequest>();
                    return ResolveOneAsync(id, reqs, targetFramework, feed, ct);
                })
                .ToList();

            var waveResults = await Task.WhenAll(waveTasks).ConfigureAwait(false);

            var nextWave = new HashSet<PackageId>();
            foreach (var pkg in waveResults)
            {
                if (pkg is null) continue;   // conflict or transient error
                resolved[pkg.Id] = pkg;
                foreach (var dep in pkg.Dependencies)
                {
                    AddConstraint(constraints, dep.Id,
                        new VersionRangeRequest(Requester: pkg.Id.Value, Range: dep.RangeText));
                    if (!resolved.ContainsKey(dep.Id))
                        nextWave.Add(dep.Id);
                }
            }
            wave = nextWave;
        }

        var conflicts = BuildConflicts(constraints, resolved);
        return new ResolutionResult(resolved, conflicts);
    }

    // -- private helpers --

    /// <summary>
    /// Resolve a single package: list available versions, pick the best one satisfying
    /// <paramref name="requests"/>, fetch its metadata, and return the resolved package.
    /// Returns <c>null</c> on transient feed errors or unsatisfiable constraints.
    /// </summary>
    private static async Task<ResolvedPackage?> ResolveOneAsync(
        PackageId id,
        IReadOnlyList<VersionRangeRequest> requests,
        string targetFramework,
        INuGetFeed feed,
        CancellationToken ct)
    {
        IReadOnlyList<PackageVersion> versions;
        try { versions = await feed.ListVersionsAsync(id, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        if (requests.Count == 0) return null;
        var chosen = ChooseBestVersion(versions, requests);
        if (chosen is null) return null;

        PackageMetadata metadata;
        try { metadata = await feed.GetMetadataAsync(id, chosen.Value, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        var deps = SelectDependencyGroup(metadata, targetFramework);
        return new ResolvedPackage(id, chosen.Value, deps);
    }

    private static PackageVersion? ChooseBestVersion(
        IReadOnlyList<PackageVersion> versions,
        IReadOnlyList<VersionRangeRequest> requests)
    {
        foreach (var v in versions.OrderByDescending(v => v.Value))
        {
            var satisfiesAll = true;
            foreach (var r in requests)
            {
                if (!VersionRange.TryParse(r.Range, out var range)) continue;
                if (!range.Satisfies(v.Value)) { satisfiesAll = false; break; }
            }
            if (satisfiesAll) return v;
        }
        return null;
    }

    /// <summary>
    /// Selects the best matching dependency group for <paramref name="targetFramework"/>
    /// using the real NuGet framework compatibility/reduction logic
    /// (<see cref="FrameworkReducer"/>), exactly as <c>dotnet restore</c> does.
    /// </summary>
    private static IReadOnlyList<PackageDependency> SelectDependencyGroup(
        PackageMetadata metadata,
        string targetFramework)
    {
        if (metadata.DependencyGroups.Count == 0) return Array.Empty<PackageDependency>();

        // Parse the project TFM once.
        var projectFw = NuGetFramework.ParseFolder(targetFramework);

        // Build a map of framework → group index and collect the candidate frameworks.
        var candidates = new List<NuGetFramework>(metadata.DependencyGroups.Count);
        var indexByFw = new Dictionary<NuGetFramework, int>(metadata.DependencyGroups.Count);

        for (int i = 0; i < metadata.DependencyGroups.Count; i++)
        {
            var raw = metadata.DependencyGroups[i].TargetFramework;
            var fw = string.IsNullOrEmpty(raw)
                ? NuGetFramework.AnyFramework
                : NuGetFramework.ParseFolder(raw);
            candidates.Add(fw);
            indexByFw.TryAdd(fw, i);   // first one wins if duplicates
        }

        // Use the NuGet FrameworkReducer to pick the nearest compatible framework.
        var reducer = new FrameworkReducer();
        var nearest = reducer.GetNearest(projectFw, candidates);

        if (nearest is not null && indexByFw.TryGetValue(nearest, out var idx))
            return ConvertDeps(metadata.DependencyGroups[idx].Dependencies);

        // No compatible group found — package has no deps applicable to this TFM.
        return Array.Empty<PackageDependency>();
    }

    private static IReadOnlyList<PackageDependency> ConvertDeps(IReadOnlyList<RawDependency> deps)
    {
        var result = new List<PackageDependency>(deps.Count);
        foreach (var d in deps)
        {
            if (!PackageId.TryCreate(d.Id, out var id)) continue;
            if (!VersionRange.TryParse(d.Range, out var range))
            {
                // Treat unparseable as floating "*" so we still try to resolve.
                range = VersionRange.All;
            }
            result.Add(new PackageDependency(id, range));
        }
        return result;
    }

    private static IReadOnlyList<ResolutionConflict> BuildConflicts(
        Dictionary<PackageId, List<VersionRangeRequest>> constraints,
        Dictionary<PackageId, ResolvedPackage> resolved)
    {
        var conflicts = new List<ResolutionConflict>();
        foreach (var (id, requests) in constraints)
        {
            if (resolved.ContainsKey(id)) continue;
            conflicts.Add(new ResolutionConflict(id, requests));
        }
        return conflicts;
    }

    private static void AddConstraint(
        Dictionary<PackageId, List<VersionRangeRequest>> constraints,
        PackageId id,
        VersionRangeRequest request)
    {
        if (!constraints.TryGetValue(id, out var list))
        {
            list = new List<VersionRangeRequest>();
            constraints[id] = list;
        }
        list.Add(request);
    }
}
