using pcpm.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm list</c> — list all packages in the current pcpm.lock as a Spectre table.
/// </summary>
public sealed class ListCommand : AsyncCommand
{
    private readonly ILockfileService _lock;
    private readonly IWorkspaceLocator _workspace;
    private readonly IAnsiConsole _console;

    public ListCommand(
        ILockfileService @lock,
        IWorkspaceLocator workspace,
        IAnsiConsole console)
    {
        _lock = @lock;
        _workspace = workspace;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var ct = CancellationToken.None;
        var lf = await _lock.ReadOrEmptyAsync(_workspace.Root, ct).ConfigureAwait(false);

        if (lf.Packages.Count == 0)
        {
            _console.MarkupLine("[yellow]pcpm.lock[/] has no packages. Run [yellow]pcpm install[/] first.");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Package[/]"))
            .AddColumn(new TableColumn("[bold]Version[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Hash[/]"))
            .AddColumn(new TableColumn("[bold]Deps[/]").RightAligned());

        foreach (var p in lf.Packages.OrderBy(p => p.Id.Value, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                $"[cyan]{p.Id.Value}[/]",
                $"[green]{p.Version}[/]",
                $"[grey]{p.ContentHash[..12]}[/]",
                p.Dependencies.Count.ToString());
        }

        _console.Write(table);
        _console.MarkupLine($"\n[grey]{lf.Packages.Count} packages • lockfile generated at {lf.GeneratedAt:u}[/]");
        return 0;
    }
}
