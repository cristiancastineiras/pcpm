using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Configuration;

/// <summary>
/// Default <see cref="IWorkspaceLocator"/>. Walks up from the current directory looking for
/// <c>pcpm.json</c> or <c>pcpm-workspace.yaml</c>. If neither is found, the current directory is used.
/// </summary>
public sealed class WorkspaceLocator : IWorkspaceLocator
{
    public string Root { get; }
    public bool HasConfig { get; }
    public bool HasWorkspace { get; }

    public WorkspaceLocator(string? startDirectory = null)
    {
        var start = startDirectory ?? Directory.GetCurrentDirectory();
        var (root, hasConfig, hasWorkspace) = WalkUp(start);
        Root = root;
        HasConfig = hasConfig;
        HasWorkspace = hasWorkspace;
    }

    private static (string root, bool hasConfig, bool hasWorkspace) WalkUp(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var hasConfig = File.Exists(Path.Combine(current.FullName, "pcpm.json"));
            var hasWorkspace = File.Exists(Path.Combine(current.FullName, "pcpm-workspace.yaml"));
            if (hasConfig || hasWorkspace)
                return (current.FullName, hasConfig, hasWorkspace);

            // Treat any solution file as an implicit workspace root so commands work
            // from anywhere inside a repo without requiring pcpm.json.
            var hasSolution = Directory.EnumerateFiles(current.FullName, "*.sln").Any()
                           || Directory.EnumerateFiles(current.FullName, "*.slnx").Any();
            if (hasSolution)
                return (current.FullName, hasConfig: false, hasWorkspace: false);

            current = current.Parent;
        }
        return (new DirectoryInfo(start).FullName, false, false);
    }
}
