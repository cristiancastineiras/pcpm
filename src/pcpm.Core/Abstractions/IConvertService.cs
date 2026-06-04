using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

/// <summary>
/// Converts a workspace to (or reverts from) Central Package Management.
/// </summary>
public interface IConvertService
{
    /// <summary>
    /// Scans all project files, collects versioned <c>&lt;PackageReference&gt;</c> entries,
    /// writes the resolved versions to <c>Directory.Packages.props</c>, and strips the
    /// <c>Version</c> attribute from each project file.
    /// </summary>
    Task<ConvertResult> ConvertToCpmAsync(string workspaceRoot, ConvertOptions options, CancellationToken ct);

    /// <summary>
    /// Reads versions from <c>Directory.Packages.props</c> and writes them back into each
    /// project's <c>&lt;PackageReference&gt;</c> elements, effectively undoing CPM.
    /// </summary>
    Task<ConvertResult> RevertFromCpmAsync(string workspaceRoot, ConvertOptions options, CancellationToken ct);
}
