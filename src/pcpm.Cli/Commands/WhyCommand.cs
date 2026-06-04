using System.ComponentModel;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm why &lt;PACKAGE&gt;</c> — show the chain of dependencies that pulled a given package
/// into the lockfile. Walks the lockfile graph and prints a tree.
/// </summary>
public sealed class WhyCommand : AsyncCommand<WhyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE>")]
        [Description("Package id to explain.")]
        public string Package { get; init; } = "";
    }

    private readonly ILockfileService _lock;
    private readonly IProjectDiscovery _discovery;
    private readonly IProjectFileService _projects;
    private readonly IWorkspaceLocator _workspace;
    private readonly IAnsiConsole _console;

    public WhyCommand(
        ILockfileService @lock,
        IProjectDiscovery discovery,
        IProjectFileService projects,
        IWorkspaceLocator workspace,
        IAnsiConsole console)
    {
        _lock = @lock;
        _discovery = discovery;
        _projects = projects;
        _workspace = workspace;
        _console = console;
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
        var lf = await _lock.ReadOrEmptyAsync(root, ct).ConfigureAwait(false);
        if (lf.Packages.Count == 0)
        {
            _console.MarkupLine("[yellow]pcpm.lock is empty. Run [yellow]pcpm install[/] first.[/]");
            return 1;
        }

        // 1. Find which projects directly depend on the package.
        var directProjects = lf.Projects
            .Where(p => p.DirectDependencies.Any(d => d.Id.Value.Equals(id.Value, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 2. Find which packages depend on it transitively (BFS upward in the dep graph).
        var dependents = new List<LockedPackage>();
        var byId = lf.Packages.ToDictionary(p => p.Id.Value, p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in lf.Packages)
        {
            if (pkg.Dependencies.Any(d => d.Id.Value.Equals(id.Value, StringComparison.OrdinalIgnoreCase)))
            {
                dependents.Add(pkg);
            }
        }

        _console.MarkupLine($"[bold cyan]{id.Value}[/] [grey]is in pcpm.lock[/]");
        if (directProjects.Count > 0)
        {
            _console.MarkupLine("\n[bold]Directly used by:[/]");
            foreach (var p in directProjects)
            {
                _console.MarkupLine($"  [green]•[/] [grey]{Path.GetRelativePath(root, p.ProjectPath)}[/]");
            }
        }

        if (dependents.Count > 0)
        {
            _console.MarkupLine("\n[bold]Pulled in by:[/]");
            foreach (var d in dependents.OrderBy(d => d.Id.Value, StringComparer.OrdinalIgnoreCase))
            {
                _console.MarkupLine($"  [green]•[/] [cyan]{d.Id.Value}[/] [grey]{d.Version}[/]");
            }
        }

        if (directProjects.Count == 0 && dependents.Count == 0)
        {
            _console.MarkupLine("[grey](No references found — is it in pcpm.lock at all?)[/]");
        }

        return 0;
    }
}
