using pcpm.Core.Models;

namespace pcpm.Core.Abstractions;

/// <summary>
/// Reads and writes the &lt;PackageReference&gt; entries in a .csproj file.
/// The .csproj file is treated as an MSBuild XML document; pcpm never edits anything
/// outside the &lt;ItemGroup&gt; containing &lt;PackageReference&gt; elements.
/// </summary>
public interface IProjectFileService
{
    /// <summary>Read all pcpm-relevant state from a project file.</summary>
    Task<ProjectInfo> ReadAsync(string projectPath, CancellationToken ct);

    /// <summary>Add a &lt;PackageReference Include="X" /&gt; entry to the project (no Version attribute in CPM mode).</summary>
    Task AddPackageReferenceAsync(string projectPath, PackageId id, CancellationToken ct);

    /// <summary>Remove the &lt;PackageReference Include="X" /&gt; entry for the given id. No-op if absent.</summary>
    Task RemovePackageReferenceAsync(string projectPath, PackageId id, CancellationToken ct);
}
