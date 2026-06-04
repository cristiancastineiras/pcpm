using pcpm.Core.Abstractions;
using pcpm.Infrastructure.FileSystem;

namespace pcpm.Tests;

/// <summary>
/// Test fixture that creates a unique temp directory per test and exposes a real
/// <see cref="PhysicalFileSystem"/> over it. Disposed automatically by xUnit.
/// </summary>
public sealed class TempWorkspace : IAsyncDisposable
{
    public string Root { get; }
    public IFileSystem FileSystem { get; }

    public TempWorkspace()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcpm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        FileSystem = new PhysicalFileSystem();
    }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public async Task WriteFileAsync(string relative, string content) =>
        await FileSystem.WriteAllTextAsync(Path(relative), content, CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
