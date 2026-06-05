namespace pcpm.Core.Models;

/// <summary>
/// A NuGet v3 feed declaration in <c>pcpm.json</c>.
/// Multiple feeds are tried in declaration order: first hit wins.
/// </summary>
public sealed record FeedConfig
{
    /// <summary>Display name used in log messages.</summary>
    public string Name { get; init; } = "";

    /// <summary>NuGet v3 service-index URL, e.g. <c>https://api.nuget.org/v3/index.json</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>Optional auth configuration. <c>null</c> = anonymous.</summary>
    public FeedAuth? Auth { get; init; }
}

/// <summary>
/// Authentication for a NuGet feed.
/// The token is read from an environment variable at runtime — never stored in pcpm.json.
/// </summary>
public sealed record FeedAuth
{
    /// <summary>
    /// Name of the environment variable holding the PAT / token.
    /// Examples: <c>"AZURE_DEVOPS_PAT"</c>, <c>"GITHUB_TOKEN"</c>.
    /// </summary>
    public string? EnvVar { get; init; }

    /// <summary>
    /// HTTP auth scheme. <c>"basic"</c> (default) encodes <c>pcpm:{token}</c> as
    /// Base64 — compatible with Azure Artifacts and GitHub Packages.
    /// <c>"bearer"</c> sends the token as a Bearer header directly.
    /// </summary>
    public string Scheme { get; init; } = "basic";
}
