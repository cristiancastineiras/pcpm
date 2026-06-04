using System.Text.RegularExpressions;

namespace pcpm.Core.Models;

/// <summary>
/// A NuGet package identifier (e.g. "Newtonsoft.Json").
/// Validated as: 1+ identifier characters separated by dots or dashes.
/// </summary>
public readonly partial record struct PackageId
{
    private static readonly Regex IdPattern = Generated();

    public string Value { get; }

    private PackageId(string value) => Value = value;

    public static PackageId Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > 100)
            throw new ArgumentException($"Package id '{trimmed}' is too long.", nameof(value));
        if (!IdPattern.IsMatch(trimmed))
            throw new ArgumentException($"'{value}' is not a valid NuGet package id.", nameof(value));
        return new PackageId(trimmed);
    }

    public static bool TryCreate(string? value, out PackageId id)
    {
        if (value is null || !IdPattern.IsMatch(value.Trim()))
        {
            id = default;
            return false;
        }
        id = new PackageId(value.Trim());
        return true;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Generated();
}
