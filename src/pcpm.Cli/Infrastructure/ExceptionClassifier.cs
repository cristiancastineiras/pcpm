using Spectre.Console.Cli;

namespace pcpm.Cli.Infrastructure;

/// <summary>
/// Classifies an <see cref="Exception"/> into a <see cref="ClassifiedError"/> describing
/// (a) which category the user sees, (b) the friendly headline to print, (c) the detailed
/// body text (escaped for Spectre markup), and (d) the exit code.
///
/// <para>Centralising this here keeps <see cref="ExceptionMessageBuilder"/> out of the
/// Spectre/console dependency surface and lets the test suite assert on plain records
/// without spinning up an <c>IAnsiConsole</c>.</para>
/// </summary>
public static class ExceptionClassifier
{
    /// <summary>
    /// Map <paramref name="exception"/> to a user-facing classification. Falls back to
    /// <see cref="ErrorCategory.Unexpected"/> for anything not in the known table.
    /// </summary>
    public static ClassifiedError Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Order matters: most-derived first. The base types (IOException,
        // OperationCanceledException, Exception) must come after every more specific
        // subtype — otherwise the compiler rejects later cases as unreachable.
        return exception switch
        {
            // ---- Network-specific timeouts (TaskCanceledException wraps HttpRequestException on timeout) ----
            TaskCanceledException tce when tce.InnerException is System.Net.Http.HttpRequestException =>
                new ClassifiedError(
                    ErrorCategory.Network,
                    "Network request timed out.",
                    "The NuGet feed did not respond in time. Check your network connection and try again.",
                    ExitCode: 1),

            // ---- Most specific environment errors (subclasses of IOException) ----
            DirectoryNotFoundException dnf => new ClassifiedError(
                ErrorCategory.Environment,
                "Directory not found.",
                dnf.Message,
                ExitCode: 1),

            FileNotFoundException fnf => new ClassifiedError(
                ErrorCategory.Environment,
                "File not found.",
                fnf.Message,
                ExitCode: 1),

            PathTooLongException ptl => new ClassifiedError(
                ErrorCategory.Environment,
                "Path too long for the current platform/filesystem.",
                ptl.Message,
                ExitCode: 1),

            // ---- General I/O ----
            IOException io => new ClassifiedError(
                ErrorCategory.Environment,
                "I/O error.",
                io.Message,
                ExitCode: 1),

            UnauthorizedAccessException uae => new ClassifiedError(
                ErrorCategory.Environment,
                "Permission denied.",
                uae.Message,
                ExitCode: 1),

            // ---- Cancellation (TaskCanceledException is a subclass of OperationCanceledException) ----
            TaskCanceledException tce => new ClassifiedError(
                ErrorCategory.Cancelled,
                "Operation cancelled.",
                string.IsNullOrWhiteSpace(tce.Message) ? "The task was cancelled." : tce.Message,
                ExitCode: 130),

            OperationCanceledException => new ClassifiedError(
                ErrorCategory.Cancelled,
                "Operation cancelled.",
                "The operation was cancelled before it could complete. No changes were made.",
                ExitCode: 130), // POSIX convention for SIGINT/Ctrl-C

            // ---- Network ----
            System.Net.Http.HttpRequestException hre => new ClassifiedError(
                ErrorCategory.Network,
                "Network error talking to a NuGet feed.",
                string.IsNullOrWhiteSpace(hre.Message) ? "The HTTP request failed." : hre.Message,
                ExitCode: 1),

            // ---- Spectre / CLI parser errors ----
            Spectre.Console.Cli.CommandParseException cpe => new ClassifiedError(
                ErrorCategory.UserInput,
                "Could not parse the command line.",
                string.IsNullOrWhiteSpace(cpe.Message) ? "One or more arguments or options are invalid." : cpe.Message,
                ExitCode: 2),

            Spectre.Console.Cli.CommandRuntimeException cre => new ClassifiedError(
                ErrorCategory.UserInput,
                "Command failed.",
                string.IsNullOrWhiteSpace(cre.Message) ? "The command reported an error." : cre.Message,
                ExitCode: 1),

            // ---- User input / config ----
            ArgumentException ae => new ClassifiedError(
                ErrorCategory.UserInput,
                "Invalid argument.",
                string.IsNullOrWhiteSpace(ae.Message) ? "An argument was not valid." : ae.Message,
                ExitCode: 2),

            System.Text.Json.JsonException je => new ClassifiedError(
                ErrorCategory.Configuration,
                "Could not parse a configuration or manifest file.",
                $"{je.Message} (path: {je.Path ?? "?"}, line: {je.LineNumber}, position: {je.BytePositionInLine})",
                ExitCode: 1),

            InvalidOperationException ioe => new ClassifiedError(
                ErrorCategory.UserInput,
                "Operation not valid in the current state.",
                string.IsNullOrWhiteSpace(ioe.Message) ? "The operation is not allowed here." : ioe.Message,
                ExitCode: 1),

            // ---- Expected gaps / platform limits ----
            NotSupportedException nse => new ClassifiedError(
                ErrorCategory.Environment,
                "Operation not supported on this platform or configuration.",
                nse.Message,
                ExitCode: 1),

            NotImplementedException => new ClassifiedError(
                ErrorCategory.ExpectedGap,
                "This feature is not implemented yet.",
                "The requested feature is on the roadmap but has not been implemented in this version of pcpm.",
                ExitCode: 1),

            // ---- Catch-all: anything we don't recognise ----
            // Mark it unexpected so the CLI knows to log a full diagnostic and suggest filing a bug.
            _ => new ClassifiedError(
                ErrorCategory.Unexpected,
                $"Unexpected error ({exception.GetType().Name}).",
                string.IsNullOrWhiteSpace(exception.Message) ? "No further details were provided." : exception.Message,
                ExitCode: 1),
        };
    }
}

/// <summary>Coarse-grained error category, used for exit code and reporting.</summary>
public enum ErrorCategory
{
    /// <summary>User pressed Ctrl-C or otherwise cancelled the operation.</summary>
    Cancelled,
    /// <summary>User-supplied argument/option/identifier was invalid.</summary>
    UserInput,
    /// <summary>A configuration or manifest file is malformed.</summary>
    Configuration,
    /// <summary>External environment problem (file system, permissions, network).</summary>
    Environment,
    /// <summary>Network/HTTP problem talking to a NuGet feed.</summary>
    Network,
    /// <summary>Known feature gap (e.g. <c>NotImplementedException</c> for a reserved verb).</summary>
    ExpectedGap,
    /// <summary>Anything we don't have a specific bucket for — write a full log, suggest filing a bug.</summary>
    Unexpected,
}

/// <summary>
/// Result of classifying an exception. <see cref="Headline"/> and <see cref="Details"/>
/// are kept as plain strings (Markup-escaped) so callers can decide how to render them
/// in Spectre markup or plain text.
/// </summary>
public sealed record ClassifiedError(
    ErrorCategory Category,
    string Headline,
    string Details,
    int ExitCode);
