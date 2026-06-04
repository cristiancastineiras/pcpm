using NuGet.Versioning;

namespace pcpm.Core.Models;

/// <summary>
/// A dependency entry: a target <see cref="PackageId"/> with a <see cref="VersionRange"/> constraint.
/// A floating range (e.g. "*", "1.0.*") is allowed — the resolver will pin it to a concrete version.
/// </summary>
public sealed record PackageDependency(PackageId Id, VersionRange Range)
{
    public string RangeText => Range.ToString();

    public static PackageDependency Parse(string id, string rangeText)
    {
        var pkgId = PackageId.Create(id);
        if (!VersionRange.TryParse(rangeText, out var range))
            throw new ArgumentException($"'{rangeText}' is not a valid NuGet version range.", nameof(rangeText));
        return new PackageDependency(pkgId, range);
    }
}
