using System.Text.RegularExpressions;
using pcpm.Core.Abstractions;

namespace pcpm.Infrastructure.Project;

/// <summary>
/// Default <see cref="IProjectDiscovery"/>. Reads <c>pcpm-workspace.yaml</c> if it exists
/// and uses its <c>packages</c> patterns; otherwise falls back to <c>**/*.csproj</c>.
/// The YAML parser is intentionally tiny — just enough for the two-line shape we write
/// (no need to pull in YamlDotNet for this).
/// </summary>
public sealed class ProjectDiscoveryService : IProjectDiscovery
{
    private const string WorkspaceFileName = "pcpm-workspace.yaml";

    private static readonly Regex PatternLine = new(
        @"^\s*-\s*['""]?([^'""\s]+)['""]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IFileSystem _fs;

    public ProjectDiscoveryService(IFileSystem fs) => _fs = fs;

    public async Task<IReadOnlyList<string>> FindProjectsAsync(string workspaceRoot, CancellationToken ct)
    {
        var patterns = await LoadPatternsAsync(workspaceRoot, ct).ConfigureAwait(false);
        var found = _fs.EnumerateFiles(workspaceRoot, patterns).ToList();
        return found
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> LoadPatternsAsync(string workspaceRoot, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, WorkspaceFileName);
        if (!_fs.FileExists(path)) return new[] { "**/*.csproj", "**/*.fsproj", "**/*.vbproj" };

        var text = await _fs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var patterns = new List<string>();
        var inPackages = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("#")) continue;
            if (!inPackages)
            {
                if (Regex.IsMatch(line, @"^packages\s*:", RegexOptions.CultureInvariant))
                {
                    inPackages = true;
                    if (line.Contains('[') && line.Contains(']'))
                    {
                        // Inline flow style: packages: ['**/*.csproj']
                        var m = Regex.Match(line, @"\[([^\]]*)\]");
                        if (m.Success)
                        {
                            foreach (var p in SplitFlowList(m.Groups[1].Value))
                            {
                                if (!string.IsNullOrWhiteSpace(p)) patterns.Add(p.Trim().Trim('\'', '"'));
                            }
                        }
                    }
                }
                continue;
            }
            var match = PatternLine.Match(line);
            if (match.Success)
            {
                patterns.Add(match.Groups[1].Value);
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                // A new top-level key — stop scanning the packages list.
                break;
            }
        }

        return patterns.Count == 0 ? new[] { "**/*.csproj" } : patterns;
    }

    private static IEnumerable<string> SplitFlowList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
