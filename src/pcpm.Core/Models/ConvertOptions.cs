namespace pcpm.Core.Models;

/// <summary>Options controlling how <c>pcpm convert</c> (and its <c>--revert</c> variant) behaves.</summary>
public sealed record ConvertOptions
{
    /// <summary>Do not write anything to disk — only report what would change.</summary>
    public bool DryRun { get; init; }

    /// <summary>Skip the confirmation prompt before making changes.</summary>
    public bool Force { get; init; }

    /// <summary>
    /// Merge gathered versions with those already in <c>Directory.Packages.props</c>
    /// instead of overwriting the entire file.
    /// </summary>
    public bool Merge { get; init; }

    /// <summary>Pick the minimum version found across projects instead of the maximum (default).</summary>
    public bool MinVersion { get; init; }

    /// <summary>Ignore pre-release versions when selecting a representative version.</summary>
    public bool IgnorePrerelease { get; init; }

    /// <summary>
    /// Regex applied to each directory segment to decide whether to skip it.
    /// Defaults to <c>^\.|^bin$|^obj$</c> (hidden dirs, bin, obj).
    /// </summary>
    public string ExcludeDirectories { get; init; } = @"^\.|^bin$|^obj$";
}
