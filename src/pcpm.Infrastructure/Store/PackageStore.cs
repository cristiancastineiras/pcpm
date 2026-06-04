using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Store;

/// <summary>
/// Content-addressable package store, pnpm-style.
///
/// <para>Layout under <see cref="RootPath"/>:</para>
/// <code>
/// v1/
///   &lt;sha256-hex&gt;/
///     pkg.nupkg           # the original downloaded payload, immutable
///     extracted/          # the unzipped package contents (one-time extract)
///       &lt;id&gt;/&lt;version&gt;/lib/net10.0/X.dll
///       ...
/// </code>
///
/// <para>The hardlink to <c>~/.nuget/packages</c> is created by
/// <see cref="LinkToGlobalPackagesAsync"/>. The whole point: each unique package version
/// is stored on disk exactly once, and every project that uses it points to the same bytes
/// via NTFS hardlinks. Disk usage stays flat, not O(projects × deps).</para>
/// </summary>
public sealed class PackageStore : IPackageStore
{
    private readonly IFileSystem _fs;
    private readonly IHardlinkCreator _hardlink;
    private readonly ILogger<PackageStore> _logger;
    private readonly string _globalPackagesFolder;

    public string RootPath { get; }

    public PackageStore(
        IFileSystem fs,
        IHardlinkCreator hardlink,
        ILogger<PackageStore> logger,
        string? storeRootOverride = null,
        string? globalPackagesOverride = null)
    {
        _fs = fs;
        _hardlink = hardlink;
        _logger = logger;
        RootPath = storeRootOverride ?? DefaultStorePath();
        _globalPackagesFolder = globalPackagesOverride ?? DefaultGlobalPackagesFolder();
    }

    public bool Contains(string contentHash)
    {
        var dir = Path.Combine(RootPath, "v1", contentHash);
        return _fs.DirectoryExists(dir);
    }

    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var bytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<string> MaterializeAsync(string nupkgPath, CancellationToken ct)
    {
        var hash = await ComputeFileHashAsync(nupkgPath, ct).ConfigureAwait(false);
        var pkgDir = Path.Combine(RootPath, "v1", hash);
        var storedNupkg = Path.Combine(pkgDir, "pkg.nupkg");
        var extractedDir = Path.Combine(pkgDir, "extracted");

        if (_fs.DirectoryExists(pkgDir) && _fs.FileExists(storedNupkg))
        {
            _logger.LogDebug("Package store already has {Hash}, skipping materialization", hash);
            return hash;
        }

        _fs.CreateDirectory(pkgDir);

        // Move the downloaded .nupkg into the store. Use copy+delete to be safe across volumes.
        var bytes = await _fs.ReadAllBytesAsync(nupkgPath, ct).ConfigureAwait(false);
        await _fs.WriteAllBytesAsync(storedNupkg, bytes, ct).ConfigureAwait(false);
        _fs.DeleteFile(nupkgPath);

        _logger.LogDebug("Extracting {Hash} to store", hash);
        await ExtractNupkgAsync(storedNupkg, extractedDir, ct).ConfigureAwait(false);

        return hash;
    }

    public async Task LinkToGlobalPackagesAsync(string contentHash, PackageId id, PackageVersion version, CancellationToken ct)
    {
        var sourceDir = Path.Combine(RootPath, "v1", contentHash, "extracted");
        var targetDir = Path.Combine(
            _globalPackagesFolder,
            id.Value.ToLowerInvariant(),
            version.ToString().ToLowerInvariant());

        _fs.CreateDirectory(targetDir);

        if (_fs.DirectoryExists(sourceDir))
        {
            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).ToList();
            await Parallel.ForEachAsync(files,
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
                (file, _) =>
                {
                    var rel = Path.GetRelativePath(sourceDir, file);
                    var link = Path.Combine(targetDir, rel);
                    var linkDir = Path.GetDirectoryName(link);
                    if (!string.IsNullOrEmpty(linkDir)) _fs.CreateDirectory(linkDir);
                    if (!_fs.FileExists(link)) Link(file, link);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }

        // Also link the .nupkg itself (NuGet looks for <id>.<version>.nupkg in the package folder).
        var storedNupkg = Path.Combine(RootPath, "v1", contentHash, "pkg.nupkg");
        var linkedNupkg = Path.Combine(
            targetDir,
            $"{id.Value.ToLowerInvariant()}.{version.ToString().ToLowerInvariant()}.nupkg");
        if (_fs.FileExists(storedNupkg) && !_fs.FileExists(linkedNupkg))
        {
            Link(storedNupkg, linkedNupkg);
        }

        _logger.LogDebug("Linked {PackageId} {Version} into {Target}", id.Value, version, targetDir);
    }

    public Task<StoreStats> GetStatsAsync(CancellationToken ct)
    {
        long total = 0;
        int count = 0;
        var v1 = Path.Combine(RootPath, "v1");
        if (_fs.DirectoryExists(v1))
        {
            foreach (var dir in Directory.EnumerateDirectories(v1))
            {
                count++;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    total += _fs.GetFileSize(file);
                }
            }
        }
        return Task.FromResult(new StoreStats(total, count, RootPath));
    }

    private void Link(string source, string dest)
    {
        if (_hardlink.IsSupported)
        {
            _hardlink.Create(source, dest);
        }
        else
        {
            File.Copy(source, dest, overwrite: false);
        }
    }

    private static async Task ExtractNupkgAsync(string nupkgPath, string extractDir, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                var dest = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Default store path. Resolution order (mirrors pnpm's PNPM_HOME convention):
    /// <list type="number">
    ///   <item><c>PCPM_HOME</c> env var (if set) → <c>$PCPM_HOME/store</c></item>
    ///   <item>Windows fallback → <c>%LOCALAPPDATA%/pcpm/store</c></item>
    ///   <item>Unix fallback   → <c>$XDG_DATA_HOME/pcpm/store</c> (or <c>~/.local/share/pcpm/store</c>)</item>
    /// </list>
    /// </summary>
    public static string DefaultStorePath()
    {
        var pcpmHome = Environment.GetEnvironmentVariable("PCPM_HOME");
        if (!string.IsNullOrEmpty(pcpmHome))
            return Path.Combine(pcpmHome, "store");

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "pcpm", "store");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, "pcpm", "store");
    }

    /// <summary>Default global NuGet packages folder (same env var rules as <c>dotnet restore</c>).</summary>
    public static string DefaultGlobalPackagesFolder()
    {
        var envOverride = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envOverride)) return envOverride;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");
    }
}
