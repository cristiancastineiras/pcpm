using NuGet.Versioning;

namespace pcpm.Core.Models;

/// <summary>
/// A NuGet package version, backed by <see cref="NuGetVersion"/> for full SemVer 2.0 + SemVer 1.0 compatibility
/// (including pre-release, metadata and floating semantics).
/// </summary>
public readonly record struct PackageVersion
{
    public NuGetVersion Value { get; }

    private PackageVersion(NuGetVersion value) => Value = value;

    public static PackageVersion Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!NuGetVersion.TryParse(value, out var version))
            throw new ArgumentException($"'{value}' is not a valid NuGet version.", nameof(value));
        return new PackageVersion(version);
    }

    public static bool TryCreate(string? value, out PackageVersion version)
    {
        if (value is not null && NuGetVersion.TryParse(value, out var parsed))
        {
            version = new PackageVersion(parsed);
            return true;
        }
        version = default;
        return false;
    }

    public bool IsPrerelease => Value.IsPrerelease;
    public bool IsStable => !Value.IsPrerelease;

    public int CompareTo(PackageVersion other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}
