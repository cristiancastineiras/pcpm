using System.ComponentModel;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm remove &lt;PACKAGE&gt;</c> — undo <c>add</c>: remove the package from CPM and from every
/// project's &lt;PackageReference&gt; list. Idempotent: removing a package that isn't there is a no-op.
/// </summary>
public sealed class RemoveCommand : AsyncCommand<RemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PACKAGE>")]
        [Description("Package id to remove.")]
        public string Package { get; init; } = "";
    }

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;
    private readonly IProjectFileService _projects;
    private readonly IProjectDiscovery _discovery;
    private readonly IWorkspaceLocator _workspace;
    private readonly IAnsiConsole _console;

    public RemoveCommand(
        IFileSystem fs,
        ICpmFileService cpm,
        IProjectFileService projects,
        IProjectDiscovery discovery,
        IWorkspaceLocator workspace,
        IAnsiConsole console)
    {
        _fs = fs;
        _cpm = cpm;
        _projects = projects;
        _discovery = discovery;
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

        await _cpm.RemovePackageVersionAsync(root, id, ct).ConfigureAwait(false);
        _console.MarkupLine($"[green]✓[/] removed from [yellow]{_cpm.CpmFileName}[/]");

        var projectPaths = await _discovery.FindProjectsAsync(root, ct).ConfigureAwait(false);
        var touched = 0;
        foreach (var p in projectPaths)
        {
            var info = await _projects.ReadAsync(p, ct).ConfigureAwait(false);
            if (info.PackageReferences.Any(r => r.Value.Equals(id.Value, StringComparison.OrdinalIgnoreCase)))
            {
                await _projects.RemovePackageReferenceAsync(p, id, ct).ConfigureAwait(false);
                _console.MarkupLine($"[green]✓[/] removed from [grey]{Path.GetRelativePath(root, p)}[/]");
                touched++;
            }
        }

        if (touched == 0) _console.MarkupLine("[grey]No project referenced this package.[/]");
        _console.MarkupLine("[grey]Run [yellow]pcpm install[/] to refresh pcpm.lock.[/]");
        return 0;
    }
}
