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

            // Explicit pcpm config takes highest priority.
            if (hasConfig || hasWorkspace)
                return (current.FullName, hasConfig, hasWorkspace);

            // Directory.Packages.props at this level means it is a CPM workspace root.
            // This is the natural anchor for multi-root layouts (e.g. root/ contains
            // projectA/projectA.sln, projectB/projectB.slnx, projectC/projectC.csproj)
            // where there is no single .sln at the top but there IS a shared CPM file.
            var hasCpm = File.Exists(Path.Combine(current.FullName, "Directory.Packages.props"));
            if (hasCpm)
                return (current.FullName, hasConfig: false, hasWorkspace: false);

            // Treat any solution file as an implicit workspace root so commands work
            // from anywhere inside a repo without requiring pcpm.json.
            var hasSolution = Directory.EnumerateFiles(current.FullName, "*.sln").Any()
                           || Directory.EnumerateFiles(current.FullName, "*.slnx").Any();
            if (hasSolution)
                return (current.FullName, hasConfig: false, hasWorkspace: false);

            // Multi-root: a directory whose immediate children each contain a solution.
            // At least two sub-solutions avoids false positives on plain project folders.
            var subSolutionCount = Directory.EnumerateDirectories(current.FullName)
                .Count(d => Directory.EnumerateFiles(d, "*.sln", SearchOption.TopDirectoryOnly).Any()
                         || Directory.EnumerateFiles(d, "*.slnx", SearchOption.TopDirectoryOnly).Any());
            if (subSolutionCount >= 2)
                return (current.FullName, hasConfig: false, hasWorkspace: false);

            current = current.Parent;
        }
        return (new DirectoryInfo(start).FullName, false, false);
    }
}
