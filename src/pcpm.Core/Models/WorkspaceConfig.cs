namespace pcpm.Core.Models;

/// <summary>
/// pnpm-style workspace declaration (<c>pcpm-workspace.yaml</c>).
/// Lists the glob patterns that define which projects are part of the workspace.
/// </summary>
public sealed record WorkspaceConfig
{
    /// <summary>Glob patterns (relative to the workspace root) that match project files or project directories.</summary>
    public IReadOnlyList<string> Packages { get; init; } = ["**/*.csproj"];
}
