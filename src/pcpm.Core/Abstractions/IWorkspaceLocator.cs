namespace pcpm.Core.Abstractions;

/// <summary>
/// Finds the workspace root for a pcpm operation. The workspace root is the directory containing
/// <c>pcpm.json</c> (preferred) or a <c>pcpm-workspace.yaml</c>. If neither is present, the current
/// working directory is used as the root.
/// </summary>
public interface IWorkspaceLocator
{
    /// <summary>The absolute path to the workspace root.</summary>
    string Root { get; }

    /// <summary>True if a <c>pcpm.json</c> exists in <see cref="Root"/>.</summary>
    bool HasConfig { get; }

    /// <summary>True if a <c>pcpm-workspace.yaml</c> exists in <see cref="Root"/>.</summary>
    bool HasWorkspace { get; }
}
