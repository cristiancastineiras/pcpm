using System.Text;
using pcpm.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Infrastructure;

/// <summary>
/// Renders a <see cref="ClassifiedError"/> (plus the log file path, when one was
/// written) into a Spectre-marked-up string suitable for printing to
/// <see cref="IAnsiConsole"/>. Centralised so the same wording is used whether the
/// error came from Spectre, from a <c>try/catch</c> in <c>Program</c>, or from a
/// command's own <c>ExecuteAsync</c>.
/// </summary>
public static class ExceptionMessageBuilder
{
    /// <summary>
    /// Build the user-facing error block. <paramref name="logResult"/> is optional; if
    /// provided and non-empty, a "diagnostic log written to …" footer is added.
    /// </summary>
    public static string Build(
        ClassifiedError classified,
        Exception? original,
        ExceptionLogWriteResult? logResult)
    {
        var sb = new StringBuilder(capacity: 512);

        // Headline
        sb.Append("[red]").Append(EscapeMarkup(classified.Headline)).Append("[/]");

        // Body (skip if it's identical to the headline — keeps short messages tight)
        if (!string.IsNullOrWhiteSpace(classified.Details) &&
            !string.Equals(classified.Details, classified.Headline, StringComparison.Ordinal))
        {
            sb.Append('\n').Append(EscapeMarkup(classified.Details));
        }

        // Log footer (when we managed to write one)
        if (logResult is { LogFilePath.Length: > 0 })
        {
            sb.Append("\n[grey]Diagnostic log:[/] [yellow]")
              .Append(EscapeMarkup(logResult.LogFilePath))
              .Append("[/]");
        }
        else if (logResult is { UsedFallback: true, FallbackReason: { Length: > 0 } reason })
        {
            sb.Append("\n[grey]Could not write a diagnostic log:[/] ").Append(EscapeMarkup(reason));
        }

        // For truly unexpected errors, gently suggest filing a bug.
        if (classified.Category == ErrorCategory.Unexpected)
        {
            sb.Append("\n[grey]Please consider filing an issue at[/] [yellow]https://github.com/")
              .Append("your-org/pcpm/issues[/] [grey]with the diagnostic log above.[/]");
        }

        // Keep the original exception type visible in the markup (no actual text leaks
        // into the message; the type is in the headline already, but we double-link so
        // grepping the source for "Unexpected error" is sufficient).
        _ = original;

        return sb.ToString();
    }

    /// <summary>
    /// Spectre markup uses <c>[</c> and <c>]</c> as escape characters. Strip them out of
    /// any untrusted string before embedding it inside a markup literal.
    /// </summary>
    internal static string EscapeMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return Markup.Escape(text);
    }
}
