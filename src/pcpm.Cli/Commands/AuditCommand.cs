using System.ComponentModel;
using System.Text;
using System.Text.Json;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using pcpm.Infrastructure.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Commands;

/// <summary>
/// <c>pcpm audit</c> — security, license and SBOM in one command.
/// <para>Reports:</para>
/// <list type="bullet">
///   <item>Known CVEs for all resolved packages (direct + transitive, from pcpm.lock if present)</item>
///   <item>License violations against the allow/deny lists in pcpm.json</item>
///   <item>CycloneDX JSON SBOM written to <c>./artifacts/sbom.cdx.json</c></item>
///   <item>SPDX JSON SBOM written to <c>./artifacts/sbom.spdx.json</c></item>
/// </list>
/// Exit code 1 when any HIGH/CRITICAL CVE or denied license is found.
/// </summary>
public sealed class AuditCommand : AsyncCommand<AuditCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-sbom")]
        [Description("Skip SBOM generation.")]
        public bool NoSbom { get; init; }

        [CommandOption("--output")]
        [Description("Output directory for SBOM files. Default: ./artifacts/")]
        public string? Output { get; init; }

        [CommandOption("--severity")]
        [Description("Minimum severity to report: low|moderate|high|critical. Default: low.")]
        public string Severity { get; init; } = "low";
    }

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;
    private readonly ILockfileService _lock;
    private readonly IWorkspaceLocator _workspace;
    private readonly INuGetFeed _feed;
    private readonly ConfigurationLoader _config;
    private readonly IAnsiConsole _console;

    public AuditCommand(
        IFileSystem fs,
        ICpmFileService cpm,
        ILockfileService @lock,
        IWorkspaceLocator workspace,
        INuGetFeed feed,
        ConfigurationLoader config,
        IAnsiConsole console)
    {
        _fs = fs;
        _cpm = cpm;
        _lock = @lock;
        _workspace = workspace;
        _feed = feed;
        _config = config;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = CancellationToken.None;
        var root = _workspace.Root;
        var pcpmConfig = await _config.LoadOrDefaultAsync(root, ct).ConfigureAwait(false);

        // Build the package list: from pcpm.lock (full graph) or CPM only.
        var packages = await BuildPackageListAsync(root, ct).ConfigureAwait(false);
        if (packages.Count == 0)
        {
            _console.MarkupLine("[yellow]No packages to audit.[/]");
            return 0;
        }

        _console.MarkupLine($"Auditing [cyan]{packages.Count}[/] packages…");

        // Fetch metadata for all packages in parallel.
        var metadataMap = await FetchAllMetadataAsync(packages, ct).ConfigureAwait(false);

        var minSeverity = settings.Severity.ToLowerInvariant() switch
        {
            "critical" => 3,
            "high" => 2,
            "moderate" => 1,
            _ => 0,
        };

        var exitCode = 0;
        exitCode |= ReportVulnerabilities(packages, metadataMap, minSeverity);
        exitCode |= ReportLicenses(packages, metadataMap, pcpmConfig);

        if (!settings.NoSbom)
        {
            var outDir = settings.Output ?? Path.Combine(root, "artifacts");
            Directory.CreateDirectory(outDir);
            await WriteCycloneDxAsync(packages, metadataMap, outDir, ct).ConfigureAwait(false);
            await WriteSpdxAsync(packages, metadataMap, outDir, ct).ConfigureAwait(false);
        }

        return exitCode;
    }

    // ---- package list ----

    private async Task<IReadOnlyList<(PackageId Id, PackageVersion Version)>> BuildPackageListAsync(
        string root, CancellationToken ct)
    {
        var lockfile = await _lock.ReadOrEmptyAsync(root, ct).ConfigureAwait(false);
        if (lockfile.Packages.Count > 0)
            return lockfile.Packages.Select(p => (p.Id, p.Version)).ToList();

        // Fallback: CPM direct deps only.
        var cpm = await _cpm.ReadAsync(root, ct).ConfigureAwait(false);
        return cpm.PackageVersions.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private async Task<Dictionary<string, PackageMetadata>> FetchAllMetadataAsync(
        IReadOnlyList<(PackageId Id, PackageVersion Version)> packages,
        CancellationToken ct)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, PackageMetadata>(
            StringComparer.OrdinalIgnoreCase);

        await _console.Status().StartAsync("[grey]Fetching package metadata…[/]", async _ =>
        {
            await Parallel.ForEachAsync(
                packages,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (pkg, innerCt) =>
                {
                    try
                    {
                        var meta = await _feed.GetMetadataAsync(pkg.Id, pkg.Version, innerCt).ConfigureAwait(false);
                        result[$"{pkg.Id.Value}/{pkg.Version}"] = meta;
                    }
                    catch { /* tolerate per-package failures */ }
                }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return new Dictionary<string, PackageMetadata>(result, StringComparer.OrdinalIgnoreCase);
    }

    // ---- vulnerability report ----

    private int ReportVulnerabilities(
        IReadOnlyList<(PackageId Id, PackageVersion Version)> packages,
        Dictionary<string, PackageMetadata> meta,
        int minSeverity)
    {
        var vulnGroups = packages
            .Select(p => (p, Meta: meta.GetValueOrDefault($"{p.Id.Value}/{p.Version}")))
            .Where(x => x.Meta?.Vulnerabilities.Count > 0)
            .SelectMany(x => x.Meta!.Vulnerabilities
                .Where(v => int.TryParse(v.Severity, out var s) && s >= minSeverity)
                .Select(v => (x.p.Id, x.p.Version, Vuln: v)))
            .OrderByDescending(x => x.Vuln.Severity)
            .ThenBy(x => x.Id.Value)
            .ToList();

        if (vulnGroups.Count == 0)
        {
            _console.MarkupLine("[green]✓[/] No known vulnerabilities");
            return 0;
        }

        _console.MarkupLine($"\n[bold]Vulnerabilities ({vulnGroups.Count}):[/]");
        var hasHigh = false;
        foreach (var (id, version, vuln) in vulnGroups)
        {
            var color = vuln.IsHighOrCritical ? "red" : "yellow";
            _console.MarkupLine(
                $"  [{color}]{vuln.SeverityLabel.ToUpperInvariant(),-8}[/] " +
                $"[bold]{Markup.Escape(id.Value)} {version}[/]  " +
                $"[grey]{Markup.Escape(vuln.AdvisoryId)}[/]");
            _console.MarkupLine($"           Advisory: [link]{Markup.Escape(vuln.AdvisoryUrl)}[/]");
            if (vuln.IsHighOrCritical) hasHigh = true;
        }

        return hasHigh ? 1 : 0;
    }

    // ---- license report ----

    private int ReportLicenses(
        IReadOnlyList<(PackageId Id, PackageVersion Version)> packages,
        Dictionary<string, PackageMetadata> meta,
        PcpmConfig config)
    {
        var allowed = config.AllowedLicenses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var denied = config.DeniedLicenses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasRestrictions = allowed.Count > 0 || denied.Count > 0;

        var licenseRows = packages
            .Select(p => (p, Meta: meta.GetValueOrDefault($"{p.Id.Value}/{p.Version}")))
            .Select(x =>
            {
                var license = x.Meta?.LicenseExpression
                    ?? (x.Meta?.LicenseUrl != null ? "url-only (unverifiable)" : "unknown");
                return (x.p.Id, x.p.Version, License: license);
            })
            .ToList();

        var unique = licenseRows.GroupBy(r => r.License, StringComparer.OrdinalIgnoreCase).Count();
        _console.MarkupLine($"\n[bold]Licenses ({unique} unique):[/]");

        var violations = new List<(PackageId Id, PackageVersion Version, string License)>();
        foreach (var row in licenseRows.OrderBy(r => r.Id.Value))
        {
            var isDenied = denied.Contains(row.License);
            var notAllowed = allowed.Count > 0 && !allowed.Contains(row.License) &&
                             !row.License.StartsWith("url-only", StringComparison.OrdinalIgnoreCase) &&
                             row.License != "unknown";
            if (isDenied || notAllowed)
                violations.Add((row.Id, row.Version, row.License));
        }

        if (!hasRestrictions)
        {
            // List unique licenses, no enforcement.
            var unique2 = licenseRows
                .GroupBy(r => r.License, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key} ({g.Count()})");
            _console.MarkupLine("  " + string.Join(", ", unique2.Take(20)));
            if (unique > 20) _console.MarkupLine($"  … and {unique - 20} more.");
            return 0;
        }

        if (violations.Count == 0)
        {
            _console.MarkupLine("[green]✓[/] All licenses comply with configured policy");
            return 0;
        }

        foreach (var (id, ver, lic) in violations)
            _console.MarkupLine($"  [red]✗ DENIED[/]  \"{Markup.Escape(id.Value)} {ver}\" — license: [bold]{Markup.Escape(lic)}[/]");

        return 1;
    }

    // ---- SBOM: CycloneDX 1.4 JSON ----

    private async Task WriteCycloneDxAsync(
        IReadOnlyList<(PackageId Id, PackageVersion Version)> packages,
        Dictionary<string, PackageMetadata> meta,
        string outDir,
        CancellationToken ct)
    {
        var components = packages.Select(p =>
        {
            var purl = $"pkg:nuget/{p.Id.Value}@{p.Version}";
            var m = meta.GetValueOrDefault($"{p.Id.Value}/{p.Version}");
            var license = m?.LicenseExpression;
            var comp = new Dictionary<string, object>
            {
                ["type"] = "library",
                ["bom-ref"] = purl,
                ["name"] = p.Id.Value,
                ["version"] = p.Version.ToString(),
                ["purl"] = purl,
            };
            if (!string.IsNullOrEmpty(license))
                comp["licenses"] = new[] { new Dictionary<string, object> { ["license"] = new Dictionary<string, object> { ["id"] = license } } };
            if (m?.Vulnerabilities.Count > 0)
                comp["externalReferences"] = m.Vulnerabilities
                    .Select(v => new Dictionary<string, object> { ["type"] = "advisory", ["url"] = v.AdvisoryUrl })
                    .ToArray();
            return comp;
        }).ToList();

        var sbom = new Dictionary<string, object>
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.4",
            ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
            ["version"] = 1,
            ["metadata"] = new Dictionary<string, object>
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
                ["tools"] = new[] { new Dictionary<string, object> { ["vendor"] = "pcpm", ["name"] = "pcpm", ["version"] = "0.1.0" } },
            },
            ["components"] = components,
        };

        var path = Path.Combine(outDir, "sbom.cdx.json");
        var json = JsonSerializer.Serialize(sbom, new JsonSerializerOptions { WriteIndented = true });
        await _fs.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        _console.MarkupLine($"SBOM (CycloneDX): [cyan]{Path.GetRelativePath(_workspace.Root, path)}[/]");
    }

    // ---- SBOM: SPDX 2.3 JSON ----

    private async Task WriteSpdxAsync(
        IReadOnlyList<(PackageId Id, PackageVersion Version)> packages,
        Dictionary<string, PackageMetadata> meta,
        string outDir,
        CancellationToken ct)
    {
        var pkgList = packages.Select((p, i) =>
        {
            var spdxId = $"SPDXRef-Package-{SanitizeSpdxId(p.Id.Value)}-{p.Version}";
            var m = meta.GetValueOrDefault($"{p.Id.Value}/{p.Version}");
            var license = m?.LicenseExpression ?? "NOASSERTION";
            var purl = $"pkg:nuget/{p.Id.Value}@{p.Version}";
            return new Dictionary<string, object>
            {
                ["SPDXID"] = spdxId,
                ["name"] = p.Id.Value,
                ["versionInfo"] = p.Version.ToString(),
                ["downloadLocation"] = m?.PackageContentUrl ?? "NOASSERTION",
                ["licenseConcluded"] = license,
                ["licenseDeclared"] = license,
                ["copyrightText"] = "NOASSERTION",
                ["externalRefs"] = new[] { new Dictionary<string, object>
                {
                    ["referenceCategory"] = "PACKAGE-MANAGER",
                    ["referenceLocator"] = purl,
                    ["referenceType"] = "purl",
                } },
            };
        }).ToList();

        var spdx = new Dictionary<string, object>
        {
            ["spdxVersion"] = "SPDX-2.3",
            ["dataLicense"] = "CC0-1.0",
            ["SPDXID"] = "SPDXRef-DOCUMENT",
            ["name"] = "pcpm-sbom",
            ["documentNamespace"] = $"https://pcpm/sbom/{Guid.NewGuid()}",
            ["creationInfo"] = new Dictionary<string, object>
            {
                ["created"] = DateTimeOffset.UtcNow.ToString("o"),
                ["creators"] = new[] { "Tool: pcpm-0.1.0" },
            },
            ["packages"] = pkgList,
        };

        var path = Path.Combine(outDir, "sbom.spdx.json");
        var json = JsonSerializer.Serialize(spdx, new JsonSerializerOptions { WriteIndented = true });
        await _fs.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        _console.MarkupLine($"SBOM (SPDX):      [cyan]{Path.GetRelativePath(_workspace.Root, path)}[/]");
    }

    private static string SanitizeSpdxId(string id) =>
        new StringBuilder(id.Length)
            .Append(id.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '-').ToArray())
            .ToString();
}
