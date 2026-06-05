using Microsoft.Extensions.Logging;
using pcpm.Core.Abstractions;

namespace pcpm.Infrastructure.MsBuild;

/// <summary>
/// Writes (and maintains) the MSBuild integration files that enable pnpm-style bin/ hardlinking
/// for all projects in a workspace.
///
/// <para>What gets written on <see cref="WriteAsync"/>:</para>
/// <list type="bullet">
///   <item>
///     <c>&lt;workspace&gt;/.pcpm/pcpm.MsBuild.targets</c> — the actual UsingTask + Target
///     declaration, with an absolute path to <c>pcpm.MsBuild.dll</c>.
///   </item>
///   <item>
///     <c>&lt;workspace&gt;/Directory.Build.targets</c> — an import of the targets file above,
///     wrapped in a pcpm marker block so it can be safely updated or removed later.
///     An existing file is amended (one import line added); a missing file is created.
///   </item>
/// </list>
///
/// <para>The hardlinking only activates when a project explicitly opts in via
/// <c>&lt;PcpmLinkBinaries&gt;true&lt;/PcpmLinkBinaries&gt;</c> in its
/// <c>Directory.Build.props</c>. The default is off, so existing workflows are never broken.</para>
/// </summary>
public sealed class MsBuildTargetsWriter
{
    private const string PcpmDir = ".pcpm";
    private const string PcpmTargetsFileName = "pcpm.MsBuild.targets";
    private const string DirectoryBuildTargetsFileName = "Directory.Build.targets";
    private const string BeginMarker = "<!-- BEGIN pcpm-relink -->";
    private const string EndMarker = "<!-- END pcpm-relink -->";

    private readonly IFileSystem _fs;
    private readonly ILogger<MsBuildTargetsWriter> _logger;

    public MsBuildTargetsWriter(IFileSystem fs, ILogger<MsBuildTargetsWriter> logger)
    {
        _fs = fs;
        _logger = logger;
    }

    /// <summary>
    /// Writes <c>.pcpm/pcpm.MsBuild.targets</c> and injects an import into
    /// <c>Directory.Build.targets</c> at the workspace root.
    /// Safe to call on every <c>pcpm install</c> — idempotent.
    /// </summary>
    public async Task WriteAsync(string workspaceRoot, CancellationToken ct)
    {
        var msbuildDllPath = FindMsBuildAssembly();
        if (msbuildDllPath is null)
        {
            _logger.LogWarning(
                "pcpm.MsBuild.dll not found next to the pcpm executable ({Base}). " +
                "Directory.Build.targets will not be written. " +
                "Ensure pcpm.MsBuild.dll is in the same directory as the pcpm executable.",
                AppContext.BaseDirectory);
            return;
        }

        // 1. Write .pcpm/pcpm.MsBuild.targets
        var pcpmDir = Path.Combine(workspaceRoot, PcpmDir);
        _fs.CreateDirectory(pcpmDir);

        var targetsPath = Path.Combine(pcpmDir, PcpmTargetsFileName);
        await _fs.WriteAllTextAsync(targetsPath, BuildTargetsFileContent(msbuildDllPath), ct).ConfigureAwait(false);
        _logger.LogDebug("Wrote {Path}", targetsPath);

        // 2. Ensure Directory.Build.targets at workspace root imports it.
        await EnsureImportAsync(workspaceRoot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the pcpm import block from <c>Directory.Build.targets</c> and deletes
    /// <c>.pcpm/pcpm.MsBuild.targets</c>. Called when the workspace opts out.
    /// </summary>
    public async Task RemoveAsync(string workspaceRoot, CancellationToken ct)
    {
        var dbt = Path.Combine(workspaceRoot, DirectoryBuildTargetsFileName);
        if (_fs.FileExists(dbt))
        {
            var text = await _fs.ReadAllTextAsync(dbt, ct).ConfigureAwait(false);
            var cleaned = RemoveMarkerBlock(text);
            if (cleaned != text)
            {
                await _fs.AtomicReplaceAsync(dbt, cleaned, ct).ConfigureAwait(false);
                _logger.LogDebug("Removed pcpm-relink import block from {Path}", dbt);
            }
        }

        var targets = Path.Combine(workspaceRoot, PcpmDir, PcpmTargetsFileName);
        _fs.DeleteFile(targets);
    }

    // ---- private ----

    private static string? FindMsBuildAssembly()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "pcpm.MsBuild.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string BuildTargetsFileContent(string msbuildDllPath)
    {
        // Use forward slashes in the XML attribute — MSBuild accepts both on Windows and
        // forward slashes avoid the need for XML escaping of backslashes.
        var dllPath = msbuildDllPath.Replace('\\', '/');

        return
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!--
              Managed by pcpm. Do not edit manually.

              To enable bin/ hardlinking for a project:
                Add  <PcpmLinkBinaries>true</PcpmLinkBinaries>  to your Directory.Build.props.
              To disable without removing this file:
                Add  <PcpmLinkBinaries>false</PcpmLinkBinaries>  or simply omit the property.
              To remove entirely:
                Delete this file and the import block in Directory.Build.targets.
            -->
            <Project>

              <!--
                PcpmRelinkBinLibraries — runs after MSBuild copies NuGet DLLs to bin/.
                Replaces each copy with an NTFS hardlink (Windows) or symlink (Unix) back to
                ~/.nuget/packages, so the bytes are stored only once regardless of how many
                projects reference the same package.

                Conditions:
                  PcpmLinkBinaries = true   opt-in; off by default
                  IsPublishing    != true   never alter dotnet publish output (reproducibility)
              -->
              <UsingTask TaskName="pcpm.MsBuild.PcpmRelinkBinTask"
                         AssemblyFile="{dllPath}" />

              <Target Name="PcpmRelinkBinLibraries"
                      AfterTargets="CopyFilesMarkedCopyLocal"
                      Condition="'$(PcpmLinkBinaries)' == 'true' and '$(IsPublishing)' != 'true'">
                <pcpm.MsBuild.PcpmRelinkBinTask
                  ReferenceCopyLocalPaths="@(ReferenceCopyLocalPaths)"
                  OutputPath="$(OutputPath)"
                  GlobalPackagesFolder="$(NuGetPackageRoot)" />
              </Target>

            </Project>
            """;
    }

    private static string BuildImportSnippet()
    {
        return
            $"""
                  {BeginMarker}
                  <Import Project="$(MSBuildThisFileDirectory).pcpm/{PcpmTargetsFileName}"
                          Condition="Exists('$(MSBuildThisFileDirectory).pcpm/{PcpmTargetsFileName}')" />
                  {EndMarker}
            """;
    }

    private async Task EnsureImportAsync(string workspaceRoot, CancellationToken ct)
    {
        var dbt = Path.Combine(workspaceRoot, DirectoryBuildTargetsFileName);
        var snippet = BuildImportSnippet();

        if (!_fs.FileExists(dbt))
        {
            var content =
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Project>
                {snippet}
                </Project>
                """;
            await _fs.WriteAllTextAsync(dbt, content, ct).ConfigureAwait(false);
            _logger.LogDebug("Created {Path}", dbt);
            return;
        }

        var existing = await _fs.ReadAllTextAsync(dbt, ct).ConfigureAwait(false);

        if (existing.Contains(BeginMarker, StringComparison.Ordinal))
        {
            // Already present — update the block in case the DLL path changed.
            var updated = ReplaceMarkerBlock(existing, snippet);
            if (updated != existing)
                await _fs.AtomicReplaceAsync(dbt, updated, ct).ConfigureAwait(false);
            return;
        }

        // Append our import just before </Project>.
        var closeIdx = existing.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        string merged;
        if (closeIdx >= 0)
            merged = existing[..closeIdx] + snippet + "\n" + existing[closeIdx..];
        else
            merged = existing + "\n" + snippet + "\n";

        await _fs.AtomicReplaceAsync(dbt, merged, ct).ConfigureAwait(false);
        _logger.LogDebug("Injected pcpm-relink import into existing {Path}", dbt);
    }

    private static string ReplaceMarkerBlock(string text, string newSnippet)
    {
        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return text;
        var end = text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return text;
        end += EndMarker.Length;
        return text[..begin] + newSnippet + text[end..];
    }

    private static string RemoveMarkerBlock(string text)
    {
        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return text;
        var end = text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return text;
        end += EndMarker.Length;
        // Consume the trailing newline so we don't leave a blank line.
        while (end < text.Length && (text[end] is '\n' or '\r')) end++;
        return text[..begin] + text[end..];
    }
}
