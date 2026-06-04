using System.ComponentModel;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm convert</c> — convert a workspace to Central Package Management, or revert from it.
/// <para>
/// Scans all .csproj/.fsproj/.vbproj files, collects every versioned
/// <c>&lt;PackageReference&gt;</c>, writes the resolved versions to
/// <c>Directory.Packages.props</c>, and strips the <c>Version</c> attribute from each
/// project file. Pass <c>--revert</c> to undo a previous conversion.
/// </para>
/// </summary>
public sealed class ConvertCommand : AsyncCommand<ConvertCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-r|--revert")]
        [Description("Revert from CPM: restore Version attributes to project files.")]
        public bool Revert { get; init; }

        [CommandOption("-d|--dry-run")]
        [Description("Show what would change without writing any files.")]
        public bool DryRun { get; init; }

        [CommandOption("-f|--force")]
        [Description("Apply changes without prompting for confirmation.")]
        public bool Force { get; init; }

        [CommandOption("-m|--merge")]
        [Description("Merge collected versions with an existing Directory.Packages.props instead of overwriting it.")]
        public bool Merge { get; init; }

        [CommandOption("--min-version")]
        [Description("Pick the minimum version found across projects instead of the maximum (default).")]
        public bool MinVersion { get; init; }

        [CommandOption("--ignore-prerelease")]
        [Description("Ignore pre-release versions when selecting a representative version.")]
        public bool IgnorePrerelease { get; init; }

        [CommandOption("-x|--exclude-dirs <REGEX>")]
        [Description(@"Regex to exclude directory segments. Default: ^\.|^bin$|^obj$")]
        public string ExcludeDirectories { get; init; } = @"^\.|^bin$|^obj$";
    }

    private readonly IConvertService _converter;
    private readonly IWorkspaceLocator _workspace;
    private readonly IAnsiConsole _console;

    public ConvertCommand(IConvertService converter, IWorkspaceLocator workspace, IAnsiConsole console)
    {
        _converter = converter;
        _workspace = workspace;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var root = _workspace.Root;
        var isRevert = settings.Revert;

        _console.MarkupLine(isRevert
            ? $"[grey]Reverting from CPM in[/] [yellow]{root}[/]"
            : $"[grey]Converting to CPM in[/] [yellow]{root}[/]");

        // Always preview first (dry-run) so we can show the user what would change.
        var previewOptions = BuildOptions(settings, dryRun: true);
        ConvertResult preview;
        try
        {
            preview = await RunServiceAsync(isRevert, root, previewOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        RenderPreview(preview, root, isRevert);

        if (preview.ModifiedProjects.Count == 0 && preview.CollectedVersions.Count == 0)
        {
            _console.MarkupLine("[grey]Nothing to do.[/]");
            return 0;
        }

        if (settings.DryRun)
        {
            _console.MarkupLine("\n[yellow]Dry run complete.[/] Omit [cyan]--dry-run[/] to apply changes.");
            return 0;
        }

        // Confirm unless --force.
        if (!settings.Force)
        {
            _console.WriteLine();
            if (!_console.Confirm("Apply these changes?", defaultValue: false))
            {
                _console.MarkupLine("[grey]Aborted.[/]");
                return 0;
            }
        }

        // Apply.
        var applyOptions = BuildOptions(settings, dryRun: false);
        try
        {
            var result = await RunServiceAsync(isRevert, root, applyOptions, ct).ConfigureAwait(false);
            foreach (var w in result.Warnings)
                _console.MarkupLine($"[yellow]warn:[/] {Markup.Escape(w)}");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (isRevert)
            _console.MarkupLine("\n[bold green]Revert complete.[/] Version attributes restored to project files.");
        else
            _console.MarkupLine("\n[bold green]Conversion complete.[/] Run [yellow]pcpm install[/] to lock the dependency graph.");

        return 0;
    }

    private Task<ConvertResult> RunServiceAsync(
        bool revert, string root, ConvertOptions options, CancellationToken ct) =>
        revert
            ? _converter.RevertFromCpmAsync(root, options, ct)
            : _converter.ConvertToCpmAsync(root, options, ct);

    private void RenderPreview(ConvertResult result, string root, bool isRevert)
    {
        foreach (var w in result.Warnings)
            _console.MarkupLine($"[yellow]warn:[/] {Markup.Escape(w)}");

        if (result.CollectedVersions.Count > 0)
        {
            var table = new Table()
                .AddColumn("Package")
                .AddColumn("Version")
                .Border(TableBorder.Simple)
                .BorderStyle(Style.Parse("grey"));

            foreach (var (id, ver) in result.CollectedVersions
                         .OrderBy(kv => kv.Key.Value, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(
                    $"[cyan]{Markup.Escape(id.Value)}[/]",
                    $"[green]{Markup.Escape(ver.ToString())}[/]");
            }

            _console.Write(table);
        }

        if (result.ModifiedProjects.Count > 0)
        {
            var verb = isRevert ? "restore Version to" : "strip Version from";
            _console.MarkupLine($"\n[grey]Would {verb} {result.ModifiedProjects.Count} project file(s):[/]");
            foreach (var p in result.ModifiedProjects)
                _console.MarkupLine($"  [grey]{Markup.Escape(Path.GetRelativePath(root, p))}[/]");
        }
    }

    private static ConvertOptions BuildOptions(Settings settings, bool dryRun) =>
        new()
        {
            DryRun = dryRun,
            Force = settings.Force,
            Merge = settings.Merge,
            MinVersion = settings.MinVersion,
            IgnorePrerelease = settings.IgnorePrerelease,
            ExcludeDirectories = settings.ExcludeDirectories,
        };
}
