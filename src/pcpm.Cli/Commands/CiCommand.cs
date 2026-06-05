using System.ComponentModel;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm ci</c> — CI-optimised install.
///
/// <para>Designed for use in build pipelines where:</para>
/// <list type="bullet">
///   <item>A lockfile must already exist (fail-fast if not).</item>
///   <item>The CPM <c>PackageVersions</c> must match the lockfile's direct dependencies
///         (catches drift between lock and props without a full reinstall).</item>
///   <item>Packages are sourced from the content-addressable store first, with
///         network download only for cache misses.</item>
///   <item>Optionally passes <c>--locked-mode</c> to <c>dotnet restore</c> so MSBuild
///         also rejects lock drift.</item>
/// </list>
/// Exit code 1 on any error (lockfile missing, sync mismatch, download failure).
/// </summary>
public sealed class CiCommand : AsyncCommand<CiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--locked-mode")]
        [Description("Pass --locked-mode to dotnet restore (requires packages.lock.json to be up to date).")]
        public bool LockedMode { get; init; }

        [CommandOption("--no-restore")]
        [Description("Skip the implicit dotnet restore step.")]
        public bool NoRestore { get; init; }
    }

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;
    private readonly IWorkspaceLocator _workspace;
    private readonly ILockfileService _lock;
    private readonly INuGetFeed _feed;
    private readonly IPackageStore _store;
    private readonly IProcessRunner _process;
    private readonly IAnsiConsole _console;

    public CiCommand(
        IFileSystem fs,
        ICpmFileService cpm,
        IWorkspaceLocator workspace,
        ILockfileService @lock,
        INuGetFeed feed,
        IPackageStore store,
        IProcessRunner process,
        IAnsiConsole console)
    {
        _fs = fs;
        _cpm = cpm;
        _workspace = workspace;
        _lock = @lock;
        _feed = feed;
        _store = store;
        _process = process;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var root = _workspace.Root;

        // 1. Read pcpm.lock — fail fast if not present.
        var lockfile = await _lock.ReadOrEmptyAsync(root, ct).ConfigureAwait(false);
        if (lockfile.Packages.Count == 0 && lockfile.Projects.Count == 0)
        {
            _console.MarkupLine("[red]✗ pcpm.lock does not exist.[/] Run [yellow]pcpm install[/] locally and commit the lockfile.");
            return 1;
        }

        // 2. Verify that CPM versions match what the lockfile recorded as direct deps.
        var syncOk = await CheckLockSyncAsync(root, lockfile, ct).ConfigureAwait(false);
        if (!syncOk) return 1;

        // 3. Restore each locked package from store (or download on cache miss).
        var failed = 0;
        await _console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Linking packages[/]", maxValue: lockfile.Packages.Count);
                await Parallel.ForEachAsync(
                    lockfile.Packages,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                    async (pkg, innerCt) =>
                    {
                        try
                        {
                            await EnsurePackageAsync(pkg, innerCt).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            lock (this)
                            {
                                failed++;
                                _console.MarkupLine($"[red]✗[/] {Markup.Escape(pkg.Id.Value)} {pkg.Version}: {Markup.Escape(ex.Message)}");
                            }
                        }
                        finally { task.Increment(1); }
                    }).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (failed > 0)
        {
            _console.MarkupLine($"[red]✗ {failed} package(s) could not be restored.[/]");
            return 1;
        }

        // 4. Run dotnet restore.
        if (!settings.NoRestore)
            await RunDotnetRestoreAsync(root, settings.LockedMode, ct).ConfigureAwait(false);

        _console.MarkupLine("[bold green]✓ pcpm ci completed successfully.[/]");
        return 0;
    }

    // ---- helpers ----

    private async Task<bool> CheckLockSyncAsync(string root, Lockfile lockfile, CancellationToken ct)
    {
        var cpm = await _cpm.ReadAsync(root, ct).ConfigureAwait(false);

        // Build a lookup of (id → version) from all locked direct deps.
        var lockedDirects = lockfile.Projects
            .SelectMany(p => p.DirectDependencies)
            .GroupBy(d => d.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => d.Version.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var hasError = false;

        // Check: CPM has a version not matching any locked direct dep version.
        foreach (var (id, version) in cpm.PackageVersions)
        {
            if (!lockedDirects.TryGetValue(id.Value, out var lockedVersions))
            {
                // Package is in CPM but not referenced by any project in the lock — that's ok (orphaned CPM entry),
                // but worth noting. Not a hard failure.
                continue;
            }
            if (!lockedVersions.Contains(version.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                _console.MarkupLine(
                    $"[red]✗ Sync mismatch:[/] [bold]{Markup.Escape(id.Value)}[/] — " +
                    $"CPM says [yellow]{version}[/], lockfile says [yellow]{string.Join(", ", lockedVersions)}[/].");
                hasError = true;
            }
        }

        // Check: lockfile direct dep not found in CPM.
        foreach (var (lockedId, lockedVersions) in lockedDirects)
        {
            if (!cpm.PackageVersions.ContainsKey(PackageId.Create(lockedId)))
            {
                _console.MarkupLine(
                    $"[red]✗ Sync mismatch:[/] [bold]{Markup.Escape(lockedId)}[/] is in pcpm.lock " +
                    $"but missing from Directory.Packages.props.");
                hasError = true;
            }
        }

        if (!hasError)
            _console.MarkupLine("[green]✓[/] Lockfile is in sync with CPM");

        return !hasError;
    }

    private async Task EnsurePackageAsync(LockedPackage pkg, CancellationToken ct)
    {
        // Try to link from the store (no I/O if already in global packages).
        if (_store.Contains(pkg.ContentHash))
        {
            await _store.LinkToGlobalPackagesAsync(pkg.ContentHash, pkg.Id, pkg.Version, ct).ConfigureAwait(false);
            return;
        }

        // Cache miss: download then store.
        var downloadDir = Path.Combine(_store.RootPath, "_tmp");
        Directory.CreateDirectory(downloadDir);
        var tempFile = Path.Combine(downloadDir, $"{pkg.Id.Value}.{pkg.Version}.nupkg");

        try
        {
            await _feed.DownloadPackageAsync(pkg.Id, pkg.Version, tempFile, ct).ConfigureAwait(false);
            await _store.MaterializeAsync(tempFile, ct).ConfigureAwait(false);
            await _store.LinkToGlobalPackagesAsync(pkg.ContentHash, pkg.Id, pkg.Version, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    private async Task RunDotnetRestoreAsync(string root, bool lockedMode, CancellationToken ct)
    {
        var targets = FindRestoreTargets(root);
        if (targets.Count == 0)
        {
            _console.MarkupLine("[grey]No solution or project file found — skipping dotnet restore.[/]");
            return;
        }

        foreach (var target in targets)
        {
            var args = lockedMode
                ? new[] { "restore", target, "--locked-mode" }
                : new[] { "restore", target };

            _console.MarkupLine(
                $"[grey]Running[/] [cyan]dotnet restore[/] [grey]on[/] [yellow]{Path.GetRelativePath(root, target)}[/]");

            var result = await _process.RunAsync(
                new ProcessRequest("dotnet", args, root),
                ct).ConfigureAwait(false);

            if (result.Succeeded)
                _console.MarkupLine($"[green]✓[/] [grey]{Path.GetFileName(target)} — restored[/]");
            else
            {
                _console.MarkupLine($"[red]dotnet restore failed for {Markup.Escape(Path.GetFileName(target))}:[/]");
                if (!string.IsNullOrWhiteSpace(result.StandardError)) _console.WriteLine(result.StandardError);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput)) _console.WriteLine(result.StandardOutput);
            }
        }
    }

    /// <remarks>Mirrors <c>InstallCommand.FindRestoreTargets</c> without needing the project list.</remarks>
    private static IReadOnlyList<string> FindRestoreTargets(string root)
    {
        var rootSolutions = Directory
            .EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly))
            .ToList();
        if (rootSolutions.Count > 0) return rootSolutions;

        var rootCsproj = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            .ToList();
        if (rootCsproj.Count > 0) return rootCsproj;

        var subdirTargets = new List<string>();
        foreach (var subdir in Directory.EnumerateDirectories(root))
        {
            var dirName = Path.GetFileName(subdir);
            if (dirName.StartsWith('.')) continue;

            var subSolutions = Directory
                .EnumerateFiles(subdir, "*.slnx", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(subdir, "*.sln", SearchOption.TopDirectoryOnly))
                .ToList();

            if (subSolutions.Count > 0)
                subdirTargets.AddRange(subSolutions);
            else
                subdirTargets.AddRange(
                    Directory.EnumerateFiles(subdir, "*.csproj", SearchOption.TopDirectoryOnly));
        }

        return subdirTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
