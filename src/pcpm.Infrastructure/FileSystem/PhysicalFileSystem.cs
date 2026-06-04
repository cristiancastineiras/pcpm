using System.Text.RegularExpressions;
using pcpm.Core.Abstractions;

namespace pcpm.Infrastructure.FileSystem;

/// <summary>
/// Default <see cref="IFileSystem"/> backed by the real disk via <see cref="System.IO.File"/>
/// and <see cref="System.IO.Directory"/>. Glob patterns support <c>*</c>, <c>**</c> and <c>?</c>.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
    }

    public async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task AtomicReplaceAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = path + ".pcpm-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);

            if (File.Exists(path))
            {
                // File.Move with overwrite=true is the .NET 8+ atomic-ish rename.
                File.Move(tempPath, path, overwrite: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string root, IReadOnlyList<string> patterns)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var pattern in patterns)
        {
            foreach (var file in EnumerateGlob(root, pattern))
            {
                yield return file;
            }
        }
    }

    public IEnumerable<string> EnumerateDirectories(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            yield return dir;
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public long GetFileSize(string path)
    {
        if (!File.Exists(path)) return 0;
        var info = new FileInfo(path);
        return info.Length;
    }

    /// <summary>Minimal glob walker. Supports <c>**</c> for recursive descent (any number of directory
    /// segments, including zero — so <c>**/*.csproj</c> matches a <c>Foo.csproj</c> at the root too),
    /// <c>*</c> for any chars in a single segment, <c>?</c> for one char.</summary>
    private static IEnumerable<string> EnumerateGlob(string root, string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var parts = normalized.Split('/');
        var builder = new System.Text.StringBuilder("^");
        var hasRecursive = false;
        string? previous = null;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i > 0)
            {
                // After a ** segment the directory prefix may have been empty (root-level match),
                // so the separator before the next part is optional. After anything else, it's required.
                builder.Append(previous == "**" ? "/?" : "/");
            }
            previous = part;

            if (part == "**")
            {
                hasRecursive = true;
                // Match zero or more "segment/" prefixes.
                builder.Append(@"(?:[^/]+/)*");
            }
            else
            {
                var escaped = Regex.Escape(part)
                    .Replace("\\*", "[^/]*")
                    .Replace("\\?", "[^/]");
                builder.Append(escaped);
            }
        }
        builder.Append('$');
        var regex = new Regex(builder.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);

        if (!Directory.Exists(root)) yield break;
        var searchOption = hasRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(root, "*", searchOption))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (regex.IsMatch(rel)) yield return file;
        }
    }
}
