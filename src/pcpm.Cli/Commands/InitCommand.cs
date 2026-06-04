using System.ComponentModel;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using pcpm.Infrastructure.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm init</c> — initialise a pcpm workspace at the current directory:
/// <list type="bullet">
///   <item>Writes <c>pcpm.json</c> with the default configuration.</item>
///   <item>Writes <c>Directory.Packages.props</c> with CPM enabled (empty &lt;PackageVersion&gt; list).</item>
///   <item>If multiple .csproj files exist, also writes <c>pcpm-workspace.yaml</c>.</item>
/// </list>
/// Idempotent: re-running is safe and does not overwrite user-edited fields it doesn't know about.
/// </summary>
public sealed class InitCommand : AsyncCommand<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--force")]
        [Description("Overwrite existing pcpm.json / Directory.Packages.props if they exist.")]
        public bool Force { get; init; }
    }

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;
    private readonly IWorkspaceLocator _workspace;
    private readonly ConfigurationLoader _config;
    private readonly IAnsiConsole _console;

    public InitCommand(
        IFileSystem fs,
        ICpmFileService cpm,
        IWorkspaceLocator workspace,
        ConfigurationLoader config,
        IAnsiConsole console)
    {
        _fs = fs;
        _cpm = cpm;
        _workspace = workspace;
        _config = config;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var root = _workspace.Root;
        _console.MarkupLine($"[grey]Initialising pcpm workspace at[/] [yellow]{root}[/]");

        var pcpmJson = Path.Combine(root, "pcpm.json");
        if (_fs.FileExists(pcpmJson) && !settings.Force)
        {
            _console.MarkupLine($"[red]pcpm.json already exists.[/] Use [yellow]--force[/] to overwrite.");
            return 1;
        }
        await _config.SaveAsync(root, new PcpmConfig(), ct).ConfigureAwait(false);
        _console.MarkupLine("[green]OK[/] wrote [yellow]pcpm.json[/]");

        var cpmPath = Path.Combine(root, _cpm.CpmFileName);
        if (_fs.FileExists(cpmPath) && !settings.Force)
        {
            _console.MarkupLine($"[grey]skip {_cpm.CpmFileName} already exists, leaving alone.[/] (use --force to overwrite)");
        }
        else
        {
            await _cpm.WriteAsync(root, new CentralPackageManagement
            {
                IsEnabled = true,
                PackageVersions = new Dictionary<PackageId, PackageVersion>(),
            }, ct).ConfigureAwait(false);
            _console.MarkupLine($"[green]OK[/] wrote [yellow]{_cpm.CpmFileName}[/] with CPM enabled");
        }

        var projects = _fs.EnumerateFiles(root, new[] { "**/*.csproj" }).ToList();
        if (projects.Count > 1)
        {
            var wsPath = Path.Combine(root, "pcpm-workspace.yaml");
            if (!_fs.FileExists(wsPath))
            {
                await _fs.WriteAllTextAsync(wsPath, "packages:\n  - '**/*.csproj'\n", ct).ConfigureAwait(false);
                _console.MarkupLine("[green]OK[/] wrote [yellow]pcpm-workspace.yaml[/] (multi-project workspace)");
            }
        }
        else if (projects.Count == 0)
        {
            _console.MarkupLine("[grey]No .csproj files found at the workspace root -- that's fine for now.[/]");
        }

        _console.MarkupLine("\n[bold green]Workspace ready.[/] Next: [yellow]pcpm add <package>[/] or [yellow]pcpm install[/].");
        return 0;
    }
}
