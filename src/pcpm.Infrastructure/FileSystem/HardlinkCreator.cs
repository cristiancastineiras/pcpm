using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using pcpm.Core.Abstractions;

namespace pcpm.Infrastructure.FileSystem;

/// <summary>
/// Cross-platform file-link creator.
/// <list type="bullet">
///   <item><b>Windows</b>: NTFS hardlinks via <c>CreateHardLinkW</c> (kernel32). Zero extra
///   disk — source and link share the same inode data. Requires both paths on the same volume,
///   which is the normal case (<c>%LOCALAPPDATA%\pcpm\store</c> and
///   <c>~/.nuget/packages</c> are both on the system drive).</item>
///   <item><b>macOS / Linux</b>: symlinks via <c>File.CreateSymbolicLink</c> (available since
///   .NET 6). Symlinks work across filesystems and are natively understood by MSBuild and
///   NuGet restore.</item>
/// </list>
/// </summary>
public sealed class HardlinkCreator : IHardlinkCreator
{
    // IsSupported is always true — we can always do at least a symlink.
    public bool IsSupported => true;

    public void Create(string targetFile, string linkPath)
    {
        // Ensure link directory exists.
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
            CreateHardLinkWindows(targetFile, linkPath);
        else
            CreateSymlinkUnix(targetFile, linkPath);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateHardLinkWindows(string targetFile, string linkPath)
    {
        if (!NativeMethods.CreateHardLink(linkPath, targetFile, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_NOT_SAME_DEVICE (0x11) or ERROR_ACCESS_DENIED when on different volumes.
            // Fall back to a symlink so cross-volume setups still work.
            if (error is 0x11 or 5)
            {
                CreateSymlinkUnix(targetFile, linkPath);
                return;
            }
            throw new Win32Exception(error,
                $"CreateHardLink failed for '{targetFile}' -> '{linkPath}' (Win32 error {error}).");
        }
    }

    private static void CreateSymlinkUnix(string targetFile, string linkPath)
    {
        // File.CreateSymbolicLink resolves the target relative to the link's directory
        // only when given a relative path. Use an absolute target so the link is always valid
        // regardless of cwd.
        File.CreateSymbolicLink(linkPath, targetFile);
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);
    }
}
