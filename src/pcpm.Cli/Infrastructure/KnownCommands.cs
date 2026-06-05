namespace pcpm.Cli.Infrastructure;

/// <summary>
/// Single source of truth for the set of command names (and aliases) that pcpm recognises.
/// The <c>Program</c> <c>CommandApp</c> registration and the pre-flight "command not
/// recognised" check both consult this list so they cannot drift apart.
///
/// <para>This is intentionally a static, allocation-free lookup: <c>Program.Main</c> runs
/// it on every invocation, and the only way it ever changes is when someone adds a new
/// command to <c>CommandApp</c> — at which point they must add the new name here
/// too. A unit test enforces that the two sides stay in sync.</para>
/// </summary>
public static class KnownCommands
{
    /// <summary>The set of root command names registered with <c>CommandApp</c>.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "init",
        "add",
        "install",
        "list",
        "remove",
        "why",
        "outdated",
        "store",
        "convert",
        "doctor",
        "audit",
        "ci",
    };

    /// <summary>Aliases of <see cref="Names"/>. Anything in here is also recognised.</summary>
    public static readonly IReadOnlySet<string> Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "i",   // install
        "ls",  // list
        "rm",  // remove
    };

    /// <summary>Flags that pcpm always accepts and that suppress the "command not recognised" check.</summary>
    public static readonly IReadOnlySet<string> GlobalFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "-h",
        "--version", "-V",
    };

    /// <summary>
    /// Return <c>true</c> if <paramref name="candidate"/> is a known command name, an
    /// alias, or a globally-recognised flag. Comparison is ordinal, case-insensitive.
    /// </summary>
    public static bool IsKnown(string candidate) =>
        Names.Contains(candidate) || Aliases.Contains(candidate) || GlobalFlags.Contains(candidate);

    /// <summary>
    /// Given a raw <c>args</c> vector, return the first positional (non-option) argument
    /// — the candidate command name. Returns <c>null</c> if no such argument exists.
    /// </summary>
    public static string? ExtractCommandName(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg)) continue;
            if (arg.StartsWith('-')) continue; // option, not a command
            return arg;
        }
        return null;
    }
}
