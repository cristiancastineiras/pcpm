using System.Text.RegularExpressions;
using System.Xml.Linq;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Conversion;

/// <summary>
/// Converts a workspace to or from Central Package Management by reading and writing
/// project files and <c>Directory.Packages.props</c>.
/// </summary>
public sealed class ConvertService : IConvertService
{
    private static readonly string[] ProjectGlobs = ["**/*.csproj", "**/*.fsproj", "**/*.vbproj"];
    private const string PackageReferenceLocalName = "PackageReference";

    private readonly IFileSystem _fs;
    private readonly ICpmFileService _cpm;

    public ConvertService(IFileSystem fs, ICpmFileService cpm)
    {
        _fs = fs;
        _cpm = cpm;
    }

    /// <inheritdoc/>
    public async Task<ConvertResult> ConvertToCpmAsync(
        string workspaceRoot, ConvertOptions options, CancellationToken ct)
    {
        var excludeRegex = BuildExcludeRegex(options.ExcludeDirectories);
        var projectFiles = FindProjectFiles(workspaceRoot, excludeRegex);

        var warnings = new List<string>();

        // Pass 1: collect all versioned PackageReference entries per project file.
        var filesWithVersionedRefs = new List<string>();
        var versionBuckets = new Dictionary<string, List<PackageVersion>>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in projectFiles)
        {
            var text = await _fs.ReadAllTextAsync(projectFile, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            if (doc.Root is null) continue;

            var hasVersionedRef = false;
            foreach (var el in doc.Root.Descendants().Where(e => e.Name.LocalName == PackageReferenceLocalName))
            {
                // CPM supports both Include (regular refs) and Update (condition overrides).
                var name = (string?)el.Attribute("Include") ?? (string?)el.Attribute("Update");
                var versionStr = (string?)el.Attribute("Version");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(versionStr)) continue;

                if (!PackageVersion.TryCreate(versionStr, out var pv))
                {
                    warnings.Add(
                        $"Skipping '{name}' in {RelativePath(workspaceRoot, projectFile)}: " +
                        $"'{versionStr}' is not a valid NuGet version (floating versions are not supported by CPM).");
                    continue;
                }

                if (options.IgnorePrerelease && pv.IsPrerelease) continue;

                if (!versionBuckets.TryGetValue(name, out var bucket))
                    versionBuckets[name] = bucket = [];
                bucket.Add(pv);
                hasVersionedRef = true;
            }

            if (hasVersionedRef)
                filesWithVersionedRefs.Add(projectFile);
        }

        if (versionBuckets.Count == 0)
        {
            return new ConvertResult(
                [],
                new Dictionary<PackageId, PackageVersion>(),
                ["No versioned PackageReferences found. Projects may already use Central Package Management."]);
        }

        // Resolve a single representative version per package (max by default, min with --min-version).
        var resolvedVersions = new Dictionary<PackageId, PackageVersion>();
        foreach (var (name, bucket) in versionBuckets)
        {
            if (!PackageId.TryCreate(name, out var id))
            {
                warnings.Add($"Skipping '{name}': not a valid NuGet package ID.");
                continue;
            }

            bucket.Sort((a, b) => a.CompareTo(b));
            resolvedVersions[id] = options.MinVersion ? bucket[0] : bucket[^1];
        }

        // --merge: keep existing CPM versions for packages not found in any project file.
        if (options.Merge)
        {
            var existing = await _cpm.ReadAsync(workspaceRoot, ct).ConfigureAwait(false);
            foreach (var (id, ver) in existing.PackageVersions)
            {
                if (!resolvedVersions.ContainsKey(id))
                    resolvedVersions[id] = ver;
            }
        }

        if (!options.DryRun)
        {
            // Write Directory.Packages.props.
            await _cpm.WriteAsync(workspaceRoot, new CentralPackageManagement
            {
                IsEnabled = true,
                PackageVersions = resolvedVersions,
            }, ct).ConfigureAwait(false);

            // Strip Version (and VersionOverride) attributes from every PackageReference.
            foreach (var projectFile in filesWithVersionedRefs)
            {
                var text = await _fs.ReadAllTextAsync(projectFile, ct).ConfigureAwait(false);
                var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
                if (doc.Root is null) continue;

                foreach (var el in doc.Root.Descendants().Where(e => e.Name.LocalName == PackageReferenceLocalName))
                {
                    el.Attribute("Version")?.Remove();
                    el.Attribute("VersionOverride")?.Remove();
                }

                await _fs.AtomicReplaceAsync(projectFile, Serialize(doc), ct).ConfigureAwait(false);
            }
        }

        return new ConvertResult(filesWithVersionedRefs, resolvedVersions, warnings);
    }

    /// <inheritdoc/>
    public async Task<ConvertResult> RevertFromCpmAsync(
        string workspaceRoot, ConvertOptions options, CancellationToken ct)
    {
        var excludeRegex = BuildExcludeRegex(options.ExcludeDirectories);
        var projectFiles = FindProjectFiles(workspaceRoot, excludeRegex);

        var cpm = await _cpm.ReadAsync(workspaceRoot, ct).ConfigureAwait(false);
        if (!cpm.IsEnabled || cpm.PackageVersions.Count == 0)
        {
            return new ConvertResult(
                [],
                new Dictionary<PackageId, PackageVersion>(),
                ["No Central Package Management versions found to revert."]);
        }

        // Build a fast lookup: package name (case-insensitive) → version string.
        var versionLookup = cpm.PackageVersions
            .ToDictionary(kv => kv.Key.Value, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        var modifiedProjects = new List<string>();

        foreach (var projectFile in projectFiles)
        {
            var text = await _fs.ReadAllTextAsync(projectFile, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            if (doc.Root is null) continue;

            var changed = false;
            foreach (var el in doc.Root.Descendants().Where(e => e.Name.LocalName == PackageReferenceLocalName))
            {
                var name = (string?)el.Attribute("Include") ?? (string?)el.Attribute("Update");
                if (string.IsNullOrEmpty(name)) continue;

                // Only patch refs that don't already carry an explicit Version.
                if (el.Attribute("Version") is not null) continue;

                if (versionLookup.TryGetValue(name, out var ver))
                {
                    el.SetAttributeValue("Version", ver);
                    changed = true;
                }
                else
                {
                    warnings.Add(
                        $"'{name}' in {RelativePath(workspaceRoot, projectFile)} " +
                        "has no matching entry in Directory.Packages.props — left without a version.");
                }
            }

            if (changed)
            {
                if (!options.DryRun)
                    await _fs.AtomicReplaceAsync(projectFile, Serialize(doc), ct).ConfigureAwait(false);
                modifiedProjects.Add(projectFile);
            }
        }

        return new ConvertResult(modifiedProjects, cpm.PackageVersions, warnings);
    }

    // -- helpers --

    private IReadOnlyList<string> FindProjectFiles(string workspaceRoot, Regex excludeRegex) =>
        _fs.EnumerateFiles(workspaceRoot, ProjectGlobs)
            .Where(p => !HasExcludedSegment(p, workspaceRoot, excludeRegex))
            .ToList();

    private static bool HasExcludedSegment(string fullPath, string root, Regex exclude)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Check directory segments only — skip the filename at the end.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (exclude.IsMatch(segments[i])) return true;
        }
        return false;
    }

    private static Regex BuildExcludeRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string RelativePath(string root, string full) =>
        Path.GetRelativePath(root, full);

    private static string Serialize(XDocument doc) =>
        doc.Declaration is null
            ? doc.ToString()
            : doc.Declaration + Environment.NewLine + doc.Root!.ToString();
}
