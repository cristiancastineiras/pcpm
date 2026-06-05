using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MsBuildTask = Microsoft.Build.Utilities.Task;

namespace pcpm.MsBuild;

/// <summary>
/// MSBuild task: after <c>CopyFilesMarkedCopyLocal</c> places NuGet package assemblies in
/// <c>bin/</c>, this task replaces each file copy with an NTFS hardlink (Windows) or symlink
/// (Unix) back to the NuGet global packages cache.
///
/// <para>Net effect: one physical copy of each DLL on disk regardless of how many projects
/// reference it — the same saving pnpm achieves for JS node_modules.</para>
///
/// <para>Only files sourced from the NuGet global packages folder are touched (identified by
/// path prefix matching <c>$(NuGetPackageRoot)</c>). The project's own output assemblies are
/// never modified.</para>
///
/// <para>If a hardlink fails because source and destination are on different volumes,
/// the task logs a warning and falls back to a regular file copy — the build never fails
/// due to storage topology.</para>
/// </summary>
public sealed class PcpmRelinkBinTask : MsBuildTask
{
    /// <summary>
    /// Items from <c>@(ReferenceCopyLocalPaths)</c>. Item identity is the source file path;
    /// <c>%(DestinationSubDirectory)</c> metadata gives the relative sub-folder within the
    /// output directory.
    /// </summary>
    [Required]
    public ITaskItem[] ReferenceCopyLocalPaths { get; set; } = [];

    /// <summary>The build output directory, e.g. <c>bin/Debug/net10.0/</c>.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the NuGet global packages cache.
    /// Pass <c>$(NuGetPackageRoot)</c> from MSBuild; falls back to <c>~/.nuget/packages</c>.
    /// </summary>
    public string GlobalPackagesFolder { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        var nugetRoot = ResolveNuGetRoot();
        var outputFull = Path.GetFullPath(OutputPath);

        var linked = 0;
        var skipped = 0;
        var fallback = 0;

        foreach (var item in ReferenceCopyLocalPaths)
        {
            var sourcePath = item.ItemSpec;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                continue;

            // Only relink files that come from the NuGet global packages cache.
            var sourceNorm = Path.GetFullPath(sourcePath);
            if (!sourceNorm.StartsWith(nugetRoot, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var destSubDir = item.GetMetadata("DestinationSubDirectory") ?? string.Empty;
            var destPath = Path.GetFullPath(
                Path.Combine(outputFull, destSubDir, Path.GetFileName(sourcePath)));

            if (!File.Exists(destPath))
            {
                skipped++;
                continue;
            }

            if (OperatingSystem.IsWindows())
            {
                if (RelinkWindows(sourceNorm, destPath))
                    linked++;
                else
                    fallback++;
            }
            else
            {
                RelinkUnix(sourceNorm, destPath);
                linked++;
            }
        }

        Log.LogMessage(
            MessageImportance.Normal,
            "[pcpm] bin/ relink: {0} hardlinked, {1} skipped, {2} copied (cross-volume fallback).",
            linked, skipped, fallback);

        return !Log.HasLoggedErrors;
    }

    // ---- platform-specific link helpers ----

    [SupportedOSPlatform("windows")]
    private bool RelinkWindows(string source, string dest)
    {
        try
        {
            File.Delete(dest);

            if (NativeMethods.CreateHardLink(dest, source, IntPtr.Zero))
                return true;

            var err = Marshal.GetLastWin32Error();
            // ERROR_NOT_SAME_DEVICE (0x11 = 17): source and dest are on different volumes.
            Log.LogWarning(
                "[pcpm] Hardlink failed (Win32 error {0}, likely cross-volume). Copying {1} instead.",
                err, Path.GetFileName(dest));
            File.Copy(source, dest, overwrite: false);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogWarning("[pcpm] Relink failed for {0}: {1}. Falling back to copy.", Path.GetFileName(dest), ex.Message);
            try
            {
                if (!File.Exists(dest))
                    File.Copy(source, dest, overwrite: false);
            }
            catch { /* best effort — original copy is still valid */ }
            return false;
        }
    }

    private void RelinkUnix(string source, string dest)
    {
        try
        {
            File.Delete(dest);
            File.CreateSymbolicLink(dest, source);
        }
        catch (Exception ex)
        {
            Log.LogWarning("[pcpm] Symlink failed for {0}: {1}. Falling back to copy.", Path.GetFileName(dest), ex.Message);
            try
            {
                if (!File.Exists(dest))
                    File.Copy(source, dest, overwrite: false);
            }
            catch { /* best effort */ }
        }
    }

    // ---- helpers ----

    private string ResolveNuGetRoot()
    {
        var folder = !string.IsNullOrEmpty(GlobalPackagesFolder)
            ? GlobalPackagesFolder
            : Environment.GetEnvironmentVariable("NUGET_PACKAGES")
              ?? Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                  ".nuget", "packages");

        // Normalize and ensure it ends with a separator for reliable StartsWith checks.
        return Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
               + Path.DirectorySeparatorChar;
    }

    // ---- Win32 P/Invoke ----

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
