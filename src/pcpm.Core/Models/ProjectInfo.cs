namespace pcpm.Core.Models;

/// <summary>
/// A .csproj (or .fsproj/.vbproj) file's pcpm-relevant state:
/// its location, TFM, and the &lt;PackageReference&gt; entries it declares.
/// </summary>
public sealed record ProjectInfo
{
    /// <summary>Absolute path to the project file.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Project name (AssemblyName or file stem).</summary>
    public required string Name { get; init; }

    /// <summary>Target frameworks declared by the project (e.g. ["net10.0"], or ["net8.0","net10.0"] for multi-targeting).</summary>
    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    /// <summary>&lt;PackageReference&gt; entries currently declared in the project (id only — version lives in CPM).</summary>
    public required IReadOnlyList<PackageId> PackageReferences { get; init; }
}
