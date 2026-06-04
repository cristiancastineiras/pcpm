using pcpm.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm outdated</c> — for every package in pcpm.lock, query the feed for the latest
/// stable version and report any that have moved forward.
/// </summary>
public sealed class OutdatedCommand : AsyncCommand
{
    private readonly ILockfileService _lock;
    private readonly INuGetFeed _feed;
    private readonly IWorkspaceLocator _workspace;
    private readonly IAnsiConsole _console;

    public OutdatedCommand(
        ILockfileService @lock,
        INuGetFeed feed,
        IWorkspaceLocator workspace,
        IAnsiConsole console)
    {
        _lock = @lock;
        _feed = feed;
        _workspace = workspace;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var ct = CancellationToken.None;
        var lf = await _lock.ReadOrEmptyAsync(_workspace.Root, ct).ConfigureAwait(false);
        if (lf.Packages.Count == 0)
        {
            _console.MarkupLine("[yellow]pcpm.lock is empty. Run [yellow]pcpm install[/] first.[/]");
            return 0;
        }

        var outdated = new List<(string Id, string Current, string Latest)>();

        await _console.Status()
            .StartAsync("Querying the feed for the latest versions…", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                foreach (var pkg in lf.Packages)
                {
                    ct.ThrowIfCancellationRequested();
                    ctx.Status($"Checking [cyan]{pkg.Id.Value}[/]");
                    try
                    {
                        var versions = await _feed.ListVersionsAsync(pkg.Id, ct).ConfigureAwait(false);
                        var latestStable = versions.FirstOrDefault(v => v.IsStable);
                        if (!latestStable.Value.Equals(pkg.Version.Value))
                        {
                            outdated.Add((pkg.Id.Value, pkg.Version.ToString(), latestStable.ToString()));
                        }
                    }
                    catch
                    {
                        // network blip on a single package — skip and continue
                    }
                }
            }).ConfigureAwait(false);

        if (outdated.Count == 0)
        {
            _console.MarkupLine("[green]Everything is up to date.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Package")
            .AddColumn(new TableColumn("Current").RightAligned())
            .AddColumn(new TableColumn("Latest").RightAligned())
            .AddColumn(new TableColumn("Bump").RightAligned());

        foreach (var (id, current, latest) in outdated.OrderBy(o => o.Id, StringComparer.OrdinalIgnoreCase))
        {
            var bump = ComputeBump(current, latest);
            table.AddRow(
                $"[cyan]{id}[/]",
                $"[grey]{current}[/]",
                $"[green]{latest}[/]",
                $"[yellow]{bump}[/]");
        }

        _console.Write(table);
        _console.MarkupLine($"\n[grey]{outdated.Count} outdated package(s). Use [yellow]pcpm add <pkg> -v <version>[/] to upgrade.[/]");
        return 0;
    }

    private static string ComputeBump(string current, string latest)
    {
        if (!Version.TryParse(current, out var c) || !Version.TryParse(latest, out var l)) return "?";
        if (l.Major > c.Major) return "major";
        if (l.Minor > c.Minor) return "minor";
        if (l.Build > c.Build) return "patch";
        return "=";
    }
}
