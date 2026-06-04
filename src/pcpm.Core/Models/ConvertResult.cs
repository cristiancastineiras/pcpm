namespace pcpm.Core.Models;

/// <summary>Summary of a completed (or dry-run) <c>pcpm convert</c> or <c>pcpm convert --revert</c>.</summary>
public sealed record ConvertResult(
    IReadOnlyList<string> ModifiedProjects,
    IReadOnlyDictionary<PackageId, PackageVersion> CollectedVersions,
    IReadOnlyList<string> Warnings);
