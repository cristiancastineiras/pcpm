namespace pcpm.Core.Abstractions;

/// <summary>
/// Finds project files (.csproj) inside a pcpm workspace. Honours <c>pcpm-workspace.yaml</c>
/// if present, otherwise falls back to <c>**/*.csproj</c>.
/// </summary>
public interface IProjectDiscovery
{
    /// <summary>
    /// Enumerate absolute project file paths under <paramref name="workspaceRoot"/>.
    /// Results are sorted and de-duplicated.
    /// </summary>
    Task<IReadOnlyList<string>> FindProjectsAsync(string workspaceRoot, CancellationToken ct);
}
