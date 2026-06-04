namespace pcpm.Core.Abstractions;

/// <summary>
/// Filesystem abstraction so that Infrastructure can be unit-tested with an in-memory fake.
/// The contract is intentionally narrow: only what pcpm actually needs.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken ct);
    Task WriteAllTextAsync(string path, string content, CancellationToken ct);
    Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct);

    /// <summary>Atomically replace the contents of <paramref name="path"/> by writing to a temp file and renaming.</summary>
    Task AtomicReplaceAsync(string path, string content, CancellationToken ct);

    void CreateDirectory(string path);

    /// <summary>Enumerate files matching any of the given glob patterns (relative to <paramref name="root"/>).
    /// Supports <c>**</c>, <c>*</c> and <c>?</c>. Used to find .csproj files in a workspace.</summary>
    IEnumerable<string> EnumerateFiles(string root, IReadOnlyList<string> patterns);

    /// <summary>Enumerate subdirectories one level deep, non-recursive.</summary>
    IEnumerable<string> EnumerateDirectories(string root);

    /// <summary>Delete a file if it exists. No-op if missing.</summary>
    void DeleteFile(string path);

    /// <summary>Get the file size in bytes, or 0 if missing.</summary>
    long GetFileSize(string path);
}
