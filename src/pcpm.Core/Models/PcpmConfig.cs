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

    /// <summary>NuGet feed URLs (in priority order). Defaults to nuget.org.</summary>
    public IReadOnlyList<string> FeedUrls { get; init; } = ["https://api.nuget.org/v3/index.json"];

    /// <summary>Override for the global store path. Default is OS-conventional user data dir.</summary>
    public string? StorePathOverride { get; init; }

    /// <summary>Whether to use hardlinks (true) or copies (false) when materialising packages into ~/.nuget/packages.
    /// Hardlinks are the pnpm-style choice: zero extra disk, instant.</summary>
    public bool UseHardlinks { get; init; } = true;

    /// <summary>Whether <c>pcpm install</c> should invoke <c>dotnet restore</c> automatically after store materialisation.</summary>
    public bool AutoRunDotnetRestore { get; init; } = true;
}
