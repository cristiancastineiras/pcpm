namespace pcpm.Core.Abstractions;

/// <summary>
/// Creates a hardlink to a target file. The pnpm-style win: zero extra disk, instant.
/// We isolate this behind an interface so the Win32 P/Invoke can be swapped out on platforms
/// where hardlinks are unavailable (rare on Windows, since NTFS supports them on the same volume).
/// </summary>
public interface IHardlinkCreator
{
    /// <summary>True if hardlinks are supported in the current environment.</summary>
    bool IsSupported { get; }

    /// <summary>Create the hardlink. Throws on failure.</summary>
    void Create(string targetFile, string linkPath);
}
