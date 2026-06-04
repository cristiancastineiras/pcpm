using System.ComponentModel;
using pcpm.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm store status</c> — show disk usage and count for the global content-addressable store.
/// </summary>
public sealed class StoreCommand : AsyncCommand<StoreCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[action]")]
        [Description("Subcommand: status (default), path, prune.")]
        public string Action { get; init; } = "status";
    }

    private readonly IPackageStore _store;
    private readonly IAnsiConsole _console;

    public StoreCommand(IPackageStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        switch (settings.Action.ToLowerInvariant())
        {
            case "path":
                _console.MarkupLine($"[yellow]{_store.RootPath}[/]");
                return 0;
            case "status":
            {
                var stats = await _store.GetStatsAsync(ct).ConfigureAwait(false);
                _console.MarkupLine($"[bold]pcpm store[/]");
                _console.MarkupLine($"  [grey]Path[/]    [yellow]{stats.RootPath}[/]");
                _console.MarkupLine($"  [grey]Packages[/] [green]{stats.PackageCount}[/]");
                _console.MarkupLine($"  [grey]Size[/]    [green]{FormatBytes(stats.TotalBytes)}[/]");
                return 0;
            }
            case "prune":
                _console.MarkupLine("[grey]Prune is not implemented yet (it would walk the lockfile in the current workspace and remove any non-referenced hashes).[/]");
                return 0;
            default:
                _console.MarkupLine($"[red]Unknown action '{settings.Action}'.[/] Try [yellow]status[/], [yellow]path[/] or [yellow]prune[/].");
                return 1;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.##} {units[u]}";
    }
}
