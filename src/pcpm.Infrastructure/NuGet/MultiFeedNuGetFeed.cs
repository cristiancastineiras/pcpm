using Microsoft.Extensions.Logging;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.NuGet;

/// <summary>
/// <see cref="INuGetFeed"/> that aggregates multiple <see cref="NuGetFeed"/> instances
/// and tries them in priority order, identical to how NuGet.config feed priority works.
///
/// <para>For list/metadata operations the first feed that returns a non-empty/successful
/// result wins. For downloads, the first feed that succeeds wins.</para>
/// </summary>
public sealed class MultiFeedNuGetFeed : INuGetFeed
{
    private readonly IReadOnlyList<NuGetFeed> _feeds;
    private readonly ILogger<MultiFeedNuGetFeed> _logger;

    public MultiFeedNuGetFeed(IReadOnlyList<NuGetFeed> feeds, ILogger<MultiFeedNuGetFeed> logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(feeds.Count);
        _feeds = feeds;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PackageVersion>> ListVersionsAsync(PackageId id, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var feed in _feeds)
        {
            try
            {
                var versions = await feed.ListVersionsAsync(id, ct).ConfigureAwait(false);
                if (versions.Count > 0) return versions;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Feed failed for ListVersions({Id}), trying next", id.Value);
                last = ex;
            }
        }
        if (last is not null) throw last;
        return Array.Empty<PackageVersion>();
    }

    public async Task<PackageMetadata> GetMetadataAsync(PackageId id, PackageVersion version, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var feed in _feeds)
        {
            try
            {
                return await feed.GetMetadataAsync(id, version, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Feed failed for GetMetadata({Id} {Version}), trying next", id.Value, version);
                last = ex;
            }
        }
        throw last ?? new NuGetFeedException($"No feed returned metadata for '{id.Value}' {version}.");
    }

    public async Task DownloadPackageAsync(PackageId id, PackageVersion version, string destinationPath, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var feed in _feeds)
        {
            try
            {
                await feed.DownloadPackageAsync(id, version, destinationPath, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Feed failed for Download({Id} {Version}), trying next", id.Value, version);
                last = ex;
                // Clean up any partial download before trying the next feed.
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { /* best effort */ }
            }
        }
        throw last ?? new NuGetFeedException($"No feed could download '{id.Value}' {version}.");
    }

    // ---- factory ----

    /// <summary>
    /// Builds a <see cref="MultiFeedNuGetFeed"/> (or a plain <see cref="NuGetFeed"/> when
    /// only one feed is configured) from a <see cref="PcpmConfig"/>.
    /// </summary>
    public static INuGetFeed Create(
        PcpmConfig config,
        ILogger<NuGetFeed> feedLogger,
        ILogger<MultiFeedNuGetFeed> multiLogger)
    {
        // Prefer the new Feeds list; fall back to legacy FeedUrls.
        var feedDefs = config.Feeds.Count > 0
            ? config.Feeds
            : config.FeedUrls.Select(url => new FeedConfig { Name = url, Url = url }).ToList();

        if (feedDefs.Count == 0)
            feedDefs = [new FeedConfig { Name = "nuget.org", Url = "https://api.nuget.org/v3/index.json" }];

        var feeds = feedDefs
            .Where(f => !string.IsNullOrWhiteSpace(f.Url))
            .Select(f => new NuGetFeed(BuildHttpClient(f), feedLogger, f.Url))
            .ToList();

        return feeds.Count == 1
            ? (INuGetFeed)feeds[0]
            : new MultiFeedNuGetFeed(feeds, multiLogger);
    }

    private static HttpClient BuildHttpClient(FeedConfig config)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip |
                System.Net.DecompressionMethods.Deflate |
                System.Net.DecompressionMethods.Brotli,
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("pcpm/0.1.0 (+https://github.com/local/pcpm)");

        if (config.Auth?.EnvVar is { } envVar)
        {
            var token = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(token))
            {
                var scheme = (config.Auth.Scheme ?? "basic").ToLowerInvariant();
                if (scheme == "bearer")
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
                else
                {
                    // Basic auth: username "pcpm", password = token.
                    // Compatible with Azure Artifacts, GitHub Packages, Artifactory.
                    var encoded = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"pcpm:{token}"));
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
                }
            }
        }

        return client;
    }
}
