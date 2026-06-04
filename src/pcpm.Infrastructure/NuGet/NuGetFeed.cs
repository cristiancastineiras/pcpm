using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.NuGet;

/// <summary>
/// NuGet v3 feed client. Speaks the public <c>https://api.nuget.org/v3/index.json</c> protocol:
/// <list type="bullet">
///   <item>List versions: <c>GET /v3/registration5-semver1/{lowerId}/index.json</c></item>
///   <item>Get metadata:  same URL, page through catalog entries</item>
///   <item>Download .nupkg: <c>GET &lt;packageContent URL&gt;</c> (a CDN URL like
///   <c>https://api.nuget.org/v3-flatcontainer/&lt;lowerId&gt;/&lt;version&gt;/&lt;id&gt;.&lt;version&gt;.nupkg</c>)</item>
/// </list>
/// </summary>
public sealed class NuGetFeed : INuGetFeed
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly ILogger<NuGetFeed> _logger;
    private readonly string _indexUrl;

    /// <summary>
    /// Per-instance cache of registration indexes keyed by lower-case package id.
    /// Eliminates the duplicate HTTP round-trip between ListVersionsAsync and GetMetadataAsync,
    /// and deduplicates concurrent requests for the same package during parallel resolution.
    /// Uses <see cref="CancellationToken.None"/> in the factory so one caller's cancellation
    /// cannot poison the shared entry.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<RegistrationIndex?>> _indexCache = new(StringComparer.OrdinalIgnoreCase);

    public NuGetFeed(HttpClient http, ILogger<NuGetFeed> logger, string indexUrl = "https://api.nuget.org/v3/index.json")
    {
        _http = http;
        _logger = logger;
        _indexUrl = indexUrl;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("https://api.nuget.org/");
        }
    }

    public async Task<IReadOnlyList<PackageVersion>> ListVersionsAsync(PackageId id, CancellationToken ct)
    {
        _logger.LogDebug("Listing versions for {PackageId}", id.Value);

        RegistrationIndex? index;
        try
        {
            index = await FetchIndexAsync(id).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NuGetFeedException($"Failed to list versions for '{id.Value}'.", ex);
        }

        ct.ThrowIfCancellationRequested();

        if (index is null || index.Items is null || index.Items.Count == 0)
        {
            return Array.Empty<PackageVersion>();
        }

        var versions = new List<PackageVersion>();
        foreach (var page in index.Items)
        {
            if (page.Items is null) continue;
            foreach (var leaf in page.Items)
            {
                if (PackageVersion.TryCreate(leaf.CatalogEntry?.Version ?? leaf.Version, out var v))
                {
                    versions.Add(v);
                }
            }
        }
        // Newest first for resolver friendliness.
        versions.Sort((a, b) => -a.Value.CompareTo(b.Value));
        return versions;
    }

    public async Task<PackageMetadata> GetMetadataAsync(PackageId id, PackageVersion version, CancellationToken ct)
    {
        // Re-use the already-cached index — no second HTTP call.
        var index = await FetchIndexAsync(id).ConfigureAwait(false)
            ?? throw new NuGetFeedException($"No registration index for '{id.Value}'.");

        ct.ThrowIfCancellationRequested();

        foreach (var page in index.Items ?? Enumerable.Empty<RegistrationPage>())
        {
            foreach (var leaf in page.Items ?? Enumerable.Empty<RegistrationLeaf>())
            {
                if (string.Equals(leaf.CatalogEntry?.Version, version.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf.Version, version.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var entry = leaf.CatalogEntry
                        ?? throw new NuGetFeedException($"No catalog entry for '{id.Value}' {version}.");
                    return entry.ToPackageMetadata(id);
                }
            }
        }

        throw new NuGetFeedException($"Version {version} of '{id.Value}' not found in registration index.");
    }

    public async Task DownloadPackageAsync(PackageId id, PackageVersion version, string destinationPath, CancellationToken ct)
    {

        var url = $"v3-flatcontainer/{id.Value.ToLowerInvariant()}/{version.ToString().ToLowerInvariant()}/{id.Value.ToLowerInvariant()}.{version.ToString().ToLowerInvariant()}.nupkg";
        _logger.LogDebug("Downloading {PackageId} {Version} from {Url}", id.Value, version, url);

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Stream to disk rather than buffering the whole .nupkg in memory.
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    // -- private helpers --

    /// <summary>
    /// Fetch the registration index for <paramref name="id"/>, caching the in-flight <see cref="Task"/>
    /// so parallel callers for the same package share one HTTP request.
    /// </summary>
    private Task<RegistrationIndex?> FetchIndexAsync(PackageId id)
    {
        var key = id.Value.ToLowerInvariant();
        return _indexCache.GetOrAdd(key, k => FetchAndHydrateAsync(k));
    }

    /// <summary>
    /// Fetch the registration index and concurrently hydrate any page stubs.
    /// NuGet's registration API is paged for packages with many versions: the top-level index
    /// returns pages where <c>Items</c> is <c>null</c> and only the <c>@id</c> URL is set.
    /// We fetch those pages in parallel so callers always get a fully-populated index.
    /// </summary>
    private async Task<RegistrationIndex?> FetchAndHydrateAsync(string lowerId)
    {
        var index = await _http.GetFromJsonAsync<RegistrationIndex>(
            $"v3/registration5-semver1/{lowerId}/index.json",
            Options,
            CancellationToken.None).ConfigureAwait(false);

        if (index?.Items is null) return index;

        // Fast path: all pages are already inline (small packages).
        if (index.Items.All(p => p.Items is not null)) return index;

        // Slow path: some pages are stubs — fetch them concurrently.
        var hydratedPages = await Task.WhenAll(
            index.Items.Select(HydratePageAsync)
        ).ConfigureAwait(false);

        return index with { Items = hydratedPages };
    }

    private async Task<RegistrationPage> HydratePageAsync(RegistrationPage page)
    {
        if (page.Items is not null) return page;       // already inline
        if (string.IsNullOrEmpty(page.PageId)) return page; // nothing to fetch

        try
        {
            // page.PageId is an absolute URL — HttpClient handles absolute URIs fine.
            var fetched = await _http.GetFromJsonAsync<RegistrationPage>(
                page.PageId, Options, CancellationToken.None).ConfigureAwait(false);
            return fetched ?? page;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to hydrate registration page {Url}", page.PageId);
            return page;
        }
    }
}

/// <summary>Wrapper to surface NuGet-protocol errors as our own exception type.</summary>
public sealed class NuGetFeedException : Exception
{
    public NuGetFeedException(string message) : base(message) { }
    public NuGetFeedException(string message, Exception inner) : base(message, inner) { }
}

// -- Wire models for the registration API. Field names are camelCase to match the JSON. --

internal sealed record RegistrationIndex(
    int Count,
    IReadOnlyList<RegistrationPage>? Items);

internal sealed record RegistrationPage(
    [property: JsonPropertyName("@id")] string PageId,
    int Count,
    IReadOnlyList<RegistrationLeaf>? Items);

internal sealed record RegistrationLeaf(
    string Id,
    string Version,
    RegistrationCatalogEntry? CatalogEntry);

internal sealed record RegistrationCatalogEntry(
    string Id,
    string Version,
    string? Description,
    string? Authors,                                 // NuGet returns this as a comma-separated string OR an array
    string? ProjectUrl,
    string? LicenseUrl,
    DateTimeOffset? Published,
    string? PackageContent,
    IReadOnlyList<DependencyGroupDto>? DependencyGroups)
{
    public PackageMetadata ToPackageMetadata(PackageId id) => new(
        Id: id,
        Version: PackageVersion.Create(Version),
        Description: Description,
        Authors: ParseAuthors(Authors),
        ProjectUrl: ProjectUrl,
        LicenseUrl: LicenseUrl,
        Published: Published,
        PackageContentUrl: PackageContent ?? string.Empty,
        DependencyGroups: (DependencyGroups ?? Array.Empty<DependencyGroupDto>())
            .Select(g => new DependencyGroup(g.TargetFramework, (g.Dependencies ?? Array.Empty<DependencyDto>())
                .Select(d => new RawDependency(d.Id, d.Range)).ToList()))
            .ToList());

    private static IReadOnlyList<string> ParseAuthors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

internal sealed record DependencyGroupDto(
    string? TargetFramework,
    IReadOnlyList<DependencyDto>? Dependencies);

internal sealed record DependencyDto(string Id, string Range);
