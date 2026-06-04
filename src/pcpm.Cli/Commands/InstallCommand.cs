using System.ComponentModel;
using System.Diagnostics;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using pcpm.Infrastructure.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm install</c> — the workhorse. Resolves the full dependency graph, downloads each
/// unique package into the content-addressable store, hardlinks it into
/// <c>~/.nuget/packages</c> so <c>dotnet restore</c> sees it instantly, and writes
/// <c>pcpm.lock</c>. Finally invokes <c>dotnet restore</c> if the workspace contains
/// a .sln or .csproj at the root.
/// </summary>
public sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-restore")]
        [Description("Skip the implicit `dotnet restore` at the end.")]
        public bool NoRestore { get; init; }

        [CommandOption("--no-lock")]
        [Description("Skip writing pcpm.lock (debug only).")]
        public bool NoLock { get; init; }
    }

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;
    private readonly IProjectFileService _projects;
    private readonly IProjectDiscovery _discovery;
    private readonly IWorkspaceLocator _workspace;
    private readonly INuGetFeed _feed;
    private readonly IPackageStore _store;
    private readonly ILockfileService _lock;
    private readonly IDependencyResolver _resolver;
    private readonly IProcessRunner _process;
    private readonly IAnsiConsole _console;

    public InstallCommand(
        IFileSystem fs,
        ICpmFileService cpm,
        IProjectFileService projects,
        IProjectDiscovery discovery,
        IWorkspaceLocator workspace,
        INuGetFeed feed,
        IPackageStore store,
        ILockfileService @lock,
        IDependencyResolver resolver,
        IProcessRunner process,
        IAnsiConsole console)
    {
        _fs = fs;
        _cpm = cpm;
        _projects = projects;
        _discovery = discovery;
        _workspace = workspace;
        _feed = feed;
        _store = store;
        _lock = @lock;
        _resolver = resolver;
        _process = process;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var sw = Stopwatch.StartNew();
        var ct = CancellationToken.None;
        var root = _workspace.Root;

        // 1. Read CPM and the project graph.
        var cpm = await _cpm.ReadAsync(root, ct).ConfigureAwait(false);
        if (!cpm.IsEnabled)
        {
            _console.MarkupLine("[red]Directory.Packages.props does not have CPM enabled.[/] Run [yellow]pcpm init[/] first.");
            return 1;
        }
        var projectPaths = await _discovery.FindProjectsAsync(root, ct).ConfigureAwait(false);
        if (projectPaths.Count == 0)
        {
            _console.MarkupLine("[yellow]No projects found.[/] Nothing to install.");
            return 0;
        }

        _console.MarkupLine($"Scope: all [cyan]{projectPaths.Count}[/] workspace project{(projectPaths.Count == 1 ? "" : "s")}");

        // 2. Build the direct-dependency list.
        var perProject = new List<LockedProject>(projectPaths.Count);
        var directDeps = new Dictionary<PackageId, PackageDependency>();
        foreach (var projectPath in projectPaths)
        {
            var info = await _projects.ReadAsync(projectPath, ct).ConfigureAwait(false);
            var directForProject = new List<LockedDependency>();
            foreach (var id in info.PackageReferences)
            {
                if (!cpm.PackageVersions.TryGetValue(id, out var version))
                {
                    _console.MarkupLine($"[red]Project '{info.Name}' references '{id.Value}' but it has no version in CPM.[/]");
                    return 1;
                }
                var range = VersionRangeHelper.Exact(version);
                var dep = new PackageDependency(id, range);
                directDeps[id] = dep;
                directForProject.Add(new LockedDependency(id, version));
            }
            perProject.Add(new LockedProject(projectPath, directForProject));
        }

        if (directDeps.Count == 0)
        {
            _console.MarkupLine("[yellow]No direct dependencies declared in any project. Nothing to install.[/]");
            return 0;
        }

        // 3. Resolve the transitive graph.
        var tfm = await PickResolutionTfmAsync(projectPaths, ct).ConfigureAwait(false);

        var result = await _console.Status()
            .StartAsync("[grey]Resolving…[/]", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                return await _resolver.ResolveAsync(directDeps.Values.ToList(), tfm, _feed, ct).ConfigureAwait(false);
            });

        if (result.HasConflicts)
        {
            result = await AutoResolveConflictsAsync(result, directDeps, tfm, root, ct).ConfigureAwait(false);

            if (result.HasConflicts)
            {
                _console.MarkupLine("[red]Unresolvable dependency conflicts:[/]");
                foreach (var c in result.Conflicts)
                {
                    _console.MarkupLine($"  [red]•[/] [bold]{Markup.Escape(c.Id.Value)}[/]");
                    foreach (var r in c.Requests)
                        _console.MarkupLine($"      [grey]{Markup.Escape(r.Requester)}[/] requires [yellow]{Markup.Escape(r.Range)}[/]");
                }
                return 1;
            }
        }

        var totalResolved = result.Resolved.Count;
        PrintProgressLine(totalResolved, reused: 0, downloaded: 0, added: 0);

        // Show pnpm-style packages header and progress bar.
        var consoleWidth = _console.Profile.Width > 20 ? _console.Profile.Width : 80;
        var barWidth = Math.Min(totalResolved, consoleWidth - 4);
        _console.MarkupLine($"Packages: [green]+{totalResolved}[/]");
        _console.MarkupLine($"[green]{new string('+', barWidth)}[/]");

        // 4. Materialise all resolved packages in parallel (bounded concurrency).
        //    Results are collected into a ConcurrentBag then sorted for the lockfile.
        var sortedResolved = result.Resolved.Values
            .OrderBy(r => r.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var materialBag = new System.Collections.Concurrent.ConcurrentBag<(
            PackageId Id, PackageVersion Version,
            string Hash, IReadOnlyList<PackageDependency> Deps,
            bool WasReused)>();

        var reused = 0;
        var downloaded = 0;
        var added = 0;
        var processed = 0;
        var reportEvery = Math.Max(1, totalResolved / 10);
        var progressLock = new object();

        await Parallel.ForEachAsync(
            sortedResolved,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (resolvedPkg, innerCt) =>
            {
                var (hash, deps, wasReused) = await MaterialiseOneAsync(resolvedPkg, tfm, innerCt).ConfigureAwait(false);
                materialBag.Add((resolvedPkg.Id, resolvedPkg.Version, hash, deps, wasReused));

                lock (progressLock)
                {
                    if (wasReused) reused++;
                    else downloaded++;
                    added++;
                    processed++;
                    if (processed % reportEvery == 0 && processed < totalResolved)
                        PrintProgressLine(totalResolved, reused, downloaded, added);
                }
            });

        var lockedPackages = materialBag
            .OrderBy(r => r.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(r => new LockedPackage(
                r.Id, r.Version, r.Hash,
                r.Deps.Select(d => new LockedDependency(d.Id, RequireExact(d))).ToList()))
            .ToList();

        // Final "done" progress line.
        PrintProgressLine(totalResolved, reused, downloaded, added, done: true);

        // 5. Direct-dependency summary (mirrors pnpm's "dependencies:" block).
        _console.WriteLine();
        PrintPackageSummary(directDeps, cpm);

        // 6. Write the lockfile.
        if (!settings.NoLock)
        {
            var lf = new Lockfile
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                PcpmVersion = "0.1.0",
                Packages = lockedPackages.OrderBy(p => p.Id.Value, StringComparer.OrdinalIgnoreCase).ToList(),
                Projects = perProject
                    .Select(p => new LockedProject(p.ProjectPath, p.DirectDependencies.ToList()))
                    .ToList(),
            };
            await _lock.WriteAsync(root, lf, ct).ConfigureAwait(false);
        }

        // 7. Run dotnet restore.
        if (!settings.NoRestore)
            await RunDotnetRestoreAsync(root, ct).ConfigureAwait(false);

        sw.Stop();
        _console.MarkupLine($"\n[bold green]Done[/] [grey]in {sw.Elapsed.TotalSeconds:F1}s[/]");
        return 0;
    }

    private async Task<(string hash, IReadOnlyList<PackageDependency> deps, bool wasReused)> MaterialiseOneAsync(
        ResolvedPackage resolved, string tfm, CancellationToken ct)
    {
        // Download to a per-package temp file under the store root.
        var downloadDir = Path.Combine(_store.RootPath, "_tmp");
        Directory.CreateDirectory(downloadDir);
        var tempFile = Path.Combine(downloadDir, $"{resolved.Id.Value}.{resolved.Version}.nupkg");

        Directory.CreateDirectory(_store.RootPath);

        // Skip download if the package is already in the global NuGet packages folder
        // (e.g. user restored once with plain `dotnet restore`).
        var globalPkgDir = Path.Combine(
            PackageStore.DefaultGlobalPackagesFolder(),
            resolved.Id.Value.ToLowerInvariant(),
            resolved.Version.ToString().ToLowerInvariant());
        var globalNupkg = Path.Combine(
            globalPkgDir,
            $"{resolved.Id.Value.ToLowerInvariant()}.{resolved.Version.ToString().ToLowerInvariant()}.nupkg");

        string hash;
        bool wasReused;
        if (Directory.Exists(globalPkgDir) && File.Exists(globalNupkg))
        {
            // Already in ~/.nuget/packages — bring into store if needed, skip the download.
            hash = await _store.ComputeFileHashAsync(globalNupkg, ct).ConfigureAwait(false);
            if (!_store.Contains(hash))
                await _store.MaterializeAsync(globalNupkg, ct).ConfigureAwait(false);
            wasReused = true;
        }
        else
        {
            await _feed.DownloadPackageAsync(resolved.Id, resolved.Version, tempFile, ct).ConfigureAwait(false);
            hash = await _store.MaterializeAsync(tempFile, ct).ConfigureAwait(false);
            wasReused = false;
        }

        await _store.LinkToGlobalPackagesAsync(hash, resolved.Id, resolved.Version, ct).ConfigureAwait(false);
        return (hash, resolved.Dependencies, wasReused);
    }

    private void PrintProgressLine(int resolved, int reused, int downloaded, int added, bool done = false)
    {
        var suffix = done ? ", [bold]done[/]" : string.Empty;
        _console.MarkupLine(
            $"[grey]Progress:[/] resolved [bold]{resolved}[/], reused [bold]{reused}[/], " +
            $"downloaded [bold]{downloaded}[/], added [bold]{added}[/]{suffix}");
    }

    private void PrintPackageSummary(
        Dictionary<PackageId, PackageDependency> directDeps,
        CentralPackageManagement cpm)
    {
        if (directDeps.Count == 0) return;
        _console.MarkupLine("[bold]dependencies:[/]");
        foreach (var (id, _) in directDeps.OrderBy(kv => kv.Key.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (cpm.PackageVersions.TryGetValue(id, out var ver))
                _console.MarkupLine($"[green]+[/] [cyan]{Markup.Escape(id.Value)}[/] [grey]{ver}[/]");
        }
    }

    private async Task RunDotnetRestoreAsync(string root, CancellationToken ct)
    {
        var target = Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault()
                     ?? Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                     ?? Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (target is null)
        {
            _console.MarkupLine("[grey]No solution or project file at the root — skipping dotnet restore.[/]");
            return;
        }

        _console.MarkupLine($"[grey]Running[/] [cyan]dotnet restore[/] [grey]on[/] [yellow]{Path.GetFileName(target)}[/]");
        var result = await _process.RunAsync(
            new ProcessRequest("dotnet", new[] { "restore", target }, root),
            ct).ConfigureAwait(false);

        if (result.Succeeded)
        {
            _console.MarkupLine("[green]✓ dotnet restore succeeded[/]");
        }
        else
        {
            _console.MarkupLine("[red]dotnet restore failed:[/]");
            _console.WriteLine(result.StandardError);
            _console.WriteLine(result.StandardOutput);
        }
    }

    private async Task<string> PickResolutionTfmAsync(IReadOnlyList<string> projectPaths, CancellationToken ct)
    {
        // Pick the TFM from the first project. For multi-targeting projects, prefer net10.0.
        foreach (var p in projectPaths)
        {
            var info = await _projects.ReadAsync(p, ct).ConfigureAwait(false);
            if (info.TargetFrameworks.Count == 0) continue;
            var preferred = info.TargetFrameworks.FirstOrDefault(t => t.StartsWith("net10", StringComparison.OrdinalIgnoreCase))
                            ?? info.TargetFrameworks[0];
            return preferred;
        }
        return "net10.0";
    }

    private static PackageVersion RequireExact(PackageDependency dep)
    {
        // The resolver returns a fully-pinned range (single version). Materialise it as PackageVersion.
        var min = dep.Range.MinVersion
            ?? throw new InvalidOperationException($"Dependency {dep.Id.Value} has no pinned min version.");
        return PackageVersion.Create(min.ToString());
    }

    /// <summary>
    /// Automatically resolves dependency conflicts by pinning each conflicting package
    /// in <c>Directory.Packages.props</c> and re-running resolution.
    ///
    /// Conflicts happen when a transitive package is discovered (added to constraints) but
    /// the resolver couldn't pick a satisfying version — either because the NuGet feed had
    /// no results (e.g. paged registration) or because multiple constraints clash.
    ///
    /// We loop up to <c>maxRounds</c> times to handle cascading transitives that are
    /// themselves discovered only after earlier conflicts are resolved.
    /// </summary>
    private async Task<ResolutionResult> AutoResolveConflictsAsync(
        ResolutionResult result,
        Dictionary<PackageId, PackageDependency> directDeps,
        string tfm,
        string root,
        CancellationToken ct)
    {
        const int maxRounds = 5;

        for (var round = 0; round < maxRounds && result.HasConflicts; round++)
        {
            var pinned = new List<(PackageId Id, PackageVersion Version)>();

            foreach (var conflict in result.Conflicts)
            {
                IReadOnlyList<PackageVersion> versions;
                try { versions = await _feed.ListVersionsAsync(conflict.Id, ct).ConfigureAwait(false); }
                catch { continue; }

                var best = PickBestVersion(versions, conflict.Requests);
                if (best is null) continue;

                await _cpm.SetPackageVersionAsync(root, conflict.Id, best.Value, ct).ConfigureAwait(false);
                pinned.Add((conflict.Id, best.Value));
            }

            if (pinned.Count == 0) break; // nothing resolvable — surface remaining conflicts

            _console.MarkupLine($"[grey]Auto-pinned [green]{pinned.Count}[/] transitive package(s):[/]");
            foreach (var (id, ver) in pinned.OrderBy(p => p.Id.Value, StringComparer.OrdinalIgnoreCase))
                _console.MarkupLine($"  [green]+[/] [cyan]{Markup.Escape(id.Value)}[/] [grey]{ver}[/]");

            // Treat newly pinned packages as resolvable direct deps for the next round.
            foreach (var (id, ver) in pinned)
                directDeps[id] = new PackageDependency(id, VersionRangeHelper.Exact(ver));

            result = await _resolver.ResolveAsync(directDeps.Values.ToList(), tfm, _feed, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Picks the highest version from <paramref name="versions"/> (already sorted newest-first)
    /// that satisfies all <paramref name="requests"/>. Returns <c>null</c> if none qualifies.
    /// </summary>
    private static PackageVersion? PickBestVersion(
        IReadOnlyList<PackageVersion> versions,
        IReadOnlyList<VersionRangeRequest> requests)
    {
        foreach (var v in versions)
        {
            var ok = true;
            foreach (var r in requests)
            {
                if (!NuGet.Versioning.VersionRange.TryParse(r.Range, out var range)) continue;
                if (!range.Satisfies(v.Value)) { ok = false; break; }
            }
            if (ok) return v;
        }
        return null;
    }
}

/// <summary>Tiny helper for building <c>VersionRange</c>s with a single pinned version.</summary>
internal static class VersionRangeHelper
{
    public static NuGet.Versioning.VersionRange Exact(PackageVersion v) =>
        NuGet.Versioning.VersionRange.Parse($"[{v}, {v}]");
}
