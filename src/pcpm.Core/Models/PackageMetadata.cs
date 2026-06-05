using System.Text.Json.Serialization;

namespace pcpm.Core.Models;

/// <summary>
/// Metadata about a package version as returned by the NuGet v3 registration API.
/// </summary>
public sealed record PackageMetadata(
    [property: JsonPropertyName("id")] PackageId Id,
    [property: JsonPropertyName("version")] PackageVersion Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("authors")] IReadOnlyList<string> Authors,
    [property: JsonPropertyName("projectUrl")] string? ProjectUrl,
    [property: JsonPropertyName("licenseUrl")] string? LicenseUrl,
    [property: JsonPropertyName("published")] DateTimeOffset? Published,
    [property: JsonPropertyName("packageContent")] string PackageContentUrl,
    [property: JsonPropertyName("dependencyGroups")] IReadOnlyList<DependencyGroup> DependencyGroups)
{
    /// <summary>
    /// SPDX license expression (e.g. "MIT", "Apache-2.0"), when declared by the package author.
    /// Populated from the <c>licenseExpression</c> field of the NuGet registration catalog entry.
    /// </summary>
    public string? LicenseExpression { get; init; }

    /// <summary>
    /// Known security advisories for this package version, from the NuGet registration API.
    /// Empty for packages with no known vulnerabilities.
    /// </summary>
    public IReadOnlyList<PackageVulnerability> Vulnerabilities { get; init; } = [];
}

/// <summary>
/// A target-framework-grouped dependency list (matches NuGet's nuspec dependencyGroups shape).
/// </summary>
public sealed record DependencyGroup(
    [property: JsonPropertyName("targetFramework")] string? TargetFramework,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<RawDependency> Dependencies);

/// <summary>
/// A raw dependency entry as it appears in the registration API response.
/// </summary>
public sealed record RawDependency(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("range")] string Range);
