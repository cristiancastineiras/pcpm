using System.ComponentModel;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using pcpm.Infrastructure.MsBuild;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm add &lt;PACKAGE&gt;</c> — declare a direct dependency. In CPM mode this:
/// <list type="number">
///   <item>Resolves a version (explicit via <c>--version</c>, otherwise the latest stable from the feed).</item>
///   <item>Writes the version into <c>Directory.Packages.props</c>.</item>
///   <item>Adds a &lt;PackageReference Include="X" /&gt; entry to one or more .csproj files.</item>
///   <item>Runs <c>pcpm install</c> to materialise the change in the store.</item>
/// </list>
/// </summary>
public sealed class AddCommand : AsyncCommand<AddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE>")]
        [Description("Package id, e.g. Newtonsoft.Json")]
        public string Package { get; init; } = "";

        [CommandOption("-v|--version")]
        [Description("Explicit version to pin. Defaults to the latest stable on the feed.")]
        public string? Version { get; init; }

        [CommandOption("-p|--project")]
        [Description("Add the PackageReference to a specific project file only. Repeatable. Default: all projects.")]
        public string[]? Project { get; init; }

        [CommandOption("--no-install")]
        [Description("Skip the implicit `pcpm install` at the end.")]
        public bool NoInstall { get; init; }
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
    private readonly MsBuildTargetsWriter _targetsWriter;

    public AddCommand(
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
        IAnsiConsole console,
        MsBuildTargetsWriter targetsWriter)
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
        _targetsWriter = targetsWriter;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        if (!PackageId.TryCreate(settings.Package, out var id))
        {
            _console.MarkupLine($"[red]'{settings.Package}' is not a valid package id.[/]");
            return 1;
        }

        var root = _workspace.Root;

        // 1. Resolve version
        PackageVersion version;
        if (!string.IsNullOrEmpty(settings.Version))
        {
            if (!PackageVersion.TryCreate(settings.Version, out version))
            {
                _console.MarkupLine($"[red]'{settings.Version}' is not a valid version.[/]");
                return 1;
            }
        }
        else
        {
            var versions = await _console.Status()
                .StartAsync("Looking up latest version on the feed…", async ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    return await _feed.ListVersionsAsync(id, ct).ConfigureAwait(false);
                });
            if (versions.Count == 0)
            {
                _console.MarkupLine($"[red]No versions found for '{id.Value}'.[/]");
                return 1;
            }
            version = versions.FirstOrDefault(v => v.IsStable);
            if (version.Value is null || version.Value.Major == 0 && version.Value.IsPrerelease == false)
            {
                // No stable — fall back to the newest of anything.
                version = versions[0];
            }
        }

        _console.MarkupLine($"[cyan]{id.Value}[/] [grey]@[/] [green]{version}[/]");

        // 2. Add to CPM
        await _cpm.SetPackageVersionAsync(root, id, version, ct).ConfigureAwait(false);
        _console.MarkupLine($"[green]✓[/] added to [yellow]{_cpm.CpmFileName}[/]");

        // 3. Add to projects
        var projectPaths = await _discovery.FindProjectsAsync(root, ct).ConfigureAwait(false);
        if (projectPaths.Count == 0)
        {
            _console.MarkupLine("[yellow]No .csproj files found in the workspace. The package is in CPM but not referenced by any project yet.[/]");
        }
        else
        {
            var targets = FilterProjects(projectPaths, settings.Project);
            if (targets.Count == 0)
            {
                _console.MarkupLine("[red]None of the --project filters matched a known project file.[/]");
                return 1;
            }
            foreach (var p in targets)
            {
                await _projects.AddPackageReferenceAsync(p, id, ct).ConfigureAwait(false);
                _console.MarkupLine($"[green]✓[/] added PackageReference to [grey]{Path.GetRelativePath(root, p)}[/]");
            }
        }

        // 4. Implicit install
        if (!settings.NoInstall)
        {
            var install = new InstallCommand(_fs, _cpm, _projects, _discovery, _workspace, _feed, _store, _lock, _resolver, _process, _console, _targetsWriter);
            return await install.ExecuteAsync(context, new InstallCommand.Settings()).ConfigureAwait(false);
        }

        return 0;
    }

    private static IReadOnlyList<string> FilterProjects(IReadOnlyList<string> all, string[]? filters)
    {
        if (filters is null || filters.Length == 0) return all;
        var set = new HashSet<string>(filters, StringComparer.OrdinalIgnoreCase);
        return all.Where(p => set.Contains(p) || set.Contains(Path.GetFileName(p))).ToList();
    }
}
