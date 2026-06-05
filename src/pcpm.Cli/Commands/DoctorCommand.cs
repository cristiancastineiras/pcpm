using System.ComponentModel;
using System.Xml.Linq;
using NuGet.Versioning;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using pcpm.Infrastructure.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm doctor</c> — audits the workspace for CPM health issues and reports them
/// with clear fix hints. Designed to run in CI pre-build.
/// <para>Checks performed:</para>
/// <list type="bullet">
///   <item>CPM enabled</item>
///   <item>Floating or invalid versions in Directory.Packages.props</item>
///   <item>PackageReference with hardcoded Version= (anti-pattern in CPM)</item>
///   <item>Orphaned CPM entries (packages not referenced by any project)</item>
///   <item>Missing CPM entries (project references without a CPM version)</item>
///   <item>Known CVEs (NuGet registration vulnerability field)</item>
///   <item>Lockfile sync</item>
/// </list>
/// Exit code 1 = errors found; 2 = warnings only; 0 = clean.
/// </summary>
public sealed class DoctorCommand : AsyncCommand<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-cve")]
        [Description("Skip the CVE check (faster; no network calls).")]
        public bool NoCve { get; init; }
    }

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;
    private readonly IProjectFileService _projects;
    private readonly IProjectDiscovery _discovery;
    private readonly IWorkspaceLocator _workspace;
    private readonly INuGetFeed _feed;
    private readonly ILockfileService _lock;
    private readonly ConfigurationLoader _config;
    private readonly IAnsiConsole _console;

    public DoctorCommand(
        IFileSystem fs,
        ICpmFileService cpm,
        IProjectFileService projects,
        IProjectDiscovery discovery,
        IWorkspaceLocator workspace,
        INuGetFeed feed,
        ILockfileService @lock,
        ConfigurationLoader config,
        IAnsiConsole console)
    {
        _fs = fs;
        _cpm = cpm;
        _projects = projects;
        _discovery = discovery;
        _workspace = workspace;
        _feed = feed;
        _lock = @lock;
        _config = config;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var root = _workspace.Root;
        var issues = new List<DoctorIssue>();

        var cpmData = await _cpm.ReadAsync(root, ct).ConfigureAwait(false);
        var projectPaths = await _discovery.FindProjectsAsync(root, ct).ConfigureAwait(false);

        // 1. CPM enabled?
        if (!cpmData.IsEnabled)
            issues.Add(new DoctorIssue(IssueSeverity.Error,
                "CPM is not enabled in Directory.Packages.props.",
                "Add <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally> to the PropertyGroup."));

        // 2. Floating / invalid versions in CPM (read raw XML to catch what CpmFileService skips).
        CheckFloatingVersions(root, issues);

        // 3. Scan projects for anti-patterns + build a reference map.
        var (allRefs, projectIssues) = await ScanProjectsAsync(projectPaths, ct).ConfigureAwait(false);
        issues.AddRange(projectIssues);

        // 4. Orphaned CPM entries (in CPM but not in any project).
        foreach (var (id, _) in cpmData.PackageVersions)
        {
            if (!allRefs.Contains(id.Value, StringComparer.OrdinalIgnoreCase))
                issues.Add(new DoctorIssue(IssueSeverity.Warning,
                    $"\"{id.Value}\" is declared in CPM but not referenced by any project.",
                    $"Remove it from Directory.Packages.props to keep CPM clean."));
        }

        // 5. Missing CPM entries (in a project but not in CPM).
        foreach (var refId in allRefs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!cpmData.PackageVersions.ContainsKey(PackageId.Create(refId)))
                issues.Add(new DoctorIssue(IssueSeverity.Error,
                    $"\"{refId}\" is referenced by a project but has no version in Directory.Packages.props.",
                    $"Add <PackageVersion Include=\"{refId}\" Version=\"x.y.z\" /> to Directory.Packages.props."));
        }

        // 6. CVE check (optional, requires network).
        if (!settings.NoCve && cpmData.PackageVersions.Count > 0)
            await CheckCveAsync(cpmData, issues, ct).ConfigureAwait(false);

        // 7. Lockfile sync.
        await CheckLockfileSyncAsync(root, cpmData, issues, ct).ConfigureAwait(false);

        // --- output ---
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);

        _console.WriteLine();
        if (issues.Count == 0)
        {
            _console.MarkupLine("[bold green]✓ No issues found. Workspace is healthy.[/]");
            return 0;
        }

        _console.MarkupLine($"[bold red]✗ {errors} error(s)[/] [bold yellow]{warnings} warning(s)[/]:\n");
        foreach (var issue in issues)
        {
            var color = issue.Severity == IssueSeverity.Error ? "red" : "yellow";
            var tag = issue.Severity == IssueSeverity.Error ? "[error]" : "[warn] ";
            _console.MarkupLine($"  [{color}]{tag}[/] {Markup.Escape(issue.Message)}");
            if (!string.IsNullOrEmpty(issue.Fix))
                _console.MarkupLine($"         [grey]Fix: {Markup.Escape(issue.Fix)}[/]");
        }
        _console.WriteLine();

        return errors > 0 ? 1 : 2;
    }

    // ---- check helpers ----

    private void CheckFloatingVersions(string root, List<DoctorIssue> issues)
    {
        var propsPath = Path.Combine(root, _cpm.CpmFileName);
        if (!_fs.FileExists(propsPath)) return;

        try
        {
            var text = File.ReadAllText(propsPath);
            var doc = XDocument.Parse(text);
            foreach (var el in doc.Descendants()
                         .Where(e => e.Name.LocalName == "PackageVersion"))
            {
                var id = (string?)el.Attribute("Include") ?? "";
                var ver = (string?)el.Attribute("Version") ?? "";
                if (ver.Contains('*'))
                    issues.Add(new DoctorIssue(IssueSeverity.Error,
                        $"Floating version \"{ver}\" in CPM for \"{id}\" — CPM requires exact versions; restore will fail.",
                        $"Replace the floating range with an exact version in {_cpm.CpmFileName}."));
                else if (!NuGetVersion.TryParse(ver, out _) && !string.IsNullOrEmpty(ver))
                    issues.Add(new DoctorIssue(IssueSeverity.Warning,
                        $"\"{id}\" has an unparseable version \"{ver}\" in CPM.",
                        $"Use a valid SemVer version string in {_cpm.CpmFileName}."));
            }
        }
        catch (Exception ex)
        {
            issues.Add(new DoctorIssue(IssueSeverity.Warning,
                $"Could not parse {_cpm.CpmFileName}: {ex.Message}", null));
        }
    }

    private async Task<(List<string> AllRefs, List<DoctorIssue> Issues)> ScanProjectsAsync(
        IReadOnlyList<string> projectPaths,
        CancellationToken ct)
    {
        var allRefs = new List<string>();
        var issues = new List<DoctorIssue>();

        foreach (var path in projectPaths)
        {
            ProjectInfo info;
            try { info = await _projects.ReadAsync(path, ct).ConfigureAwait(false); }
            catch { continue; }

            foreach (var id in info.PackageReferences)
                allRefs.Add(id.Value);

            // Check for PackageReference with Version= attribute (anti-pattern in CPM).
            try
            {
                var text = await _fs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var doc = XDocument.Parse(text);
                foreach (var el in doc.Descendants()
                             .Where(e => e.Name.LocalName == "PackageReference"))
                {
                    var versionAttr = el.Attribute("Version") ?? el.Attribute("version");
                    if (versionAttr is not null)
                    {
                        var pkgId = (string?)el.Attribute("Include") ?? "";
                        var relPath = Path.GetRelativePath(_workspace.Root, path);
                        issues.Add(new DoctorIssue(IssueSeverity.Warning,
                            $"\"{pkgId}\" in {relPath} has a hardcoded Version=\"{versionAttr.Value}\" — CPM should own all versions.",
                            $"Remove the Version attribute from the PackageReference; add the version to Directory.Packages.props if missing."));
                    }
                }
            }
            catch { /* tolerate parse errors */ }
        }

        return (allRefs, issues);
    }

    private async Task CheckCveAsync(
        CentralPackageManagement cpmData,
        List<DoctorIssue> issues,
        CancellationToken ct)
    {
        _console.MarkupLine("[grey]Checking CVEs…[/]");
        await Parallel.ForEachAsync(
            cpmData.PackageVersions,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (kv, innerCt) =>
            {
                var (id, version) = kv;
                try
                {
                    var meta = await _feed.GetMetadataAsync(id, version, innerCt).ConfigureAwait(false);
                    foreach (var vuln in meta.Vulnerabilities)
                    {
                        lock (issues)
                        {
                            issues.Add(new DoctorIssue(
                                vuln.IsHighOrCritical ? IssueSeverity.Error : IssueSeverity.Warning,
                                $"\"{id.Value} {version}\" has a known CVE ({vuln.AdvisoryId}, severity: {vuln.SeverityLabel}).",
                                $"Update to a patched version. Advisory: {vuln.AdvisoryUrl}"));
                        }
                    }
                }
                catch { /* tolerate per-package failures */ }
            }).ConfigureAwait(false);
    }

    private async Task CheckLockfileSyncAsync(
        string root,
        CentralPackageManagement cpmData,
        List<DoctorIssue> issues,
        CancellationToken ct)
    {
        var lockfile = await _lock.ReadOrEmptyAsync(root, ct).ConfigureAwait(false);
        if (lockfile.Packages.Count == 0)
        {
            // No lockfile — not an error, just informational.
            issues.Add(new DoctorIssue(IssueSeverity.Warning,
                "pcpm.lock does not exist. Run 'pcpm install' to generate it.",
                "pcpm install"));
            return;
        }

        // Build set of direct dep IDs from all locked projects.
        var lockedDirectIds = new HashSet<string>(
            lockfile.Projects.SelectMany(p => p.DirectDependencies).Select(d => d.Id.Value),
            StringComparer.OrdinalIgnoreCase);

        var cpmIds = cpmData.PackageVersions.Keys.Select(k => k.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inCpmNotLock = cpmIds.Except(lockedDirectIds).ToList();
        var inLockNotCpm = lockedDirectIds.Except(cpmIds).ToList();

        if (inCpmNotLock.Count > 0)
            issues.Add(new DoctorIssue(IssueSeverity.Warning,
                $"pcpm.lock is out of sync: {string.Join(", ", inCpmNotLock.Take(5))} in CPM but not in lockfile.",
                "Run 'pcpm install' to regenerate the lockfile."));

        if (inLockNotCpm.Count > 0)
            issues.Add(new DoctorIssue(IssueSeverity.Warning,
                $"pcpm.lock is out of sync: {string.Join(", ", inLockNotCpm.Take(5))} in lockfile but not in CPM.",
                "Run 'pcpm install' to regenerate the lockfile."));

        if (inCpmNotLock.Count == 0 && inLockNotCpm.Count == 0)
            _console.MarkupLine("[green]✓[/] Lockfile is in sync with CPM");
    }
}

internal sealed record DoctorIssue(IssueSeverity Severity, string Message, string? Fix = null);

internal enum IssueSeverity { Error, Warning }
