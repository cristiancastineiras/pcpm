namespace pcpm.Core.Models;

/// <summary>
/// pcpm's per-repository configuration file (<c>pcpm.json</c> at the repo root).
/// Drives feed selection, store behaviour, and the "where is the workspace root" decision.
/// </summary>
public sealed record PcpmConfig
{
    /// <summary>Config schema version. Bump on incompatible changes.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Whether CPM is required for this repo. Default true — pcpm's whole point is CPM.</summary>
    public bool RequireCentralPackageManagement { get; init; } = true;

    /// <summary>
    /// NuGet feed definitions with optional auth. Tried in declaration order; first hit wins.
    /// When non-empty, takes precedence over <see cref="FeedUrls"/>.
    /// </summary>
    public IReadOnlyList<FeedConfig> Feeds { get; init; } = [];

    /// <summary>Legacy: plain feed URLs without auth. Used when <see cref="Feeds"/> is empty.</summary>
    public IReadOnlyList<string> FeedUrls { get; init; } = ["https://api.nuget.org/v3/index.json"];

    /// <summary>Override for the global store path. Default is OS-conventional user data dir.</summary>
    public string? StorePathOverride { get; init; }

    /// <summary>Whether to use hardlinks (true) or copies (false) when materialising packages into ~/.nuget/packages.
    /// Hardlinks are the pnpm-style choice: zero extra disk, instant.</summary>
    public bool UseHardlinks { get; init; } = true;

    /// <summary>Whether <c>pcpm install</c> should invoke <c>dotnet restore</c> automatically after store materialisation.</summary>
    public bool AutoRunDotnetRestore { get; init; } = true;

    /// <summary>
    /// SPDX license expression allow-list for <c>pcpm audit</c>.
    /// When non-empty, packages with licenses NOT in this list will fail the audit.
    /// </summary>
    public IReadOnlyList<string> AllowedLicenses { get; init; } = [];

    /// <summary>
    /// SPDX license expression deny-list for <c>pcpm audit</c>.
    /// Packages with licenses in this list always fail the audit, regardless of AllowedLicenses.
    /// </summary>
    public IReadOnlyList<string> DeniedLicenses { get; init; } = [];
}

