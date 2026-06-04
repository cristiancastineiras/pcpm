namespace pcpm.Core.Models;

/// <summary>
/// In-memory representation of a <c>Directory.Packages.props</c> file.
/// In CPM (Central Package Management) mode this file owns the <c>ManagePackageVersionsCentrally</c> flag
/// and the global &lt;PackageVersion&gt; entries; individual .csproj files just declare &lt;PackageReference Include="X" /&gt;.
/// </summary>
public sealed record CentralPackageManagement
{
    /// <summary>True if this file has CPM enabled (ManagePackageVersionsCentrally=true).</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Centralized package versions, keyed by package id.</summary>
    public required IReadOnlyDictionary<PackageId, PackageVersion> PackageVersions { get; init; }

    /// <summary>Global PackageReference items (added via &lt;PackageVersion Include="X" /&gt; is for versions; global PackageReference is for project-wide adds).</summary>
    public IReadOnlyList<PackageId> GlobalPackageReferences { get; init; } = Array.Empty<PackageId>();
}
