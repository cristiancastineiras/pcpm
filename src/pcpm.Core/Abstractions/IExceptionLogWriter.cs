namespace pcpm.Core.Abstractions;

/// <summary>
/// Writes a structured, human-readable diagnostic record for an unhandled exception
/// into the global package store (under <c>&lt;storeRoot&gt;/logs/</c>).
///
/// <para>Records are timestamped with millisecond resolution and UTC time so they sort
/// chronologically and never collide within a single pcpm invocation. The writer is
/// expected to be the very last line of defence: it must never throw, and on any
/// I/O failure (locked file, missing store, permission denied) it should fall back
/// to a best-effort location and report the path it actually used via
/// <see cref="ExceptionLogWriteResult"/>.</para>
/// </summary>
public interface IExceptionLogWriter
{
    /// <summary>
    /// Persist a diagnostic record for <paramref name="exception"/> to a log file
    /// inside the store. Always returns a result; never throws.
    /// </summary>
    /// <param name="exception">The exception to log. Inner exceptions are included recursively.</param>
    /// <param name="context">
    /// Free-form context (command line args, current command name, etc.) to embed in
    /// the record so the log is useful when reviewed later.
    /// </param>
    /// <param name="cancellationToken">Cancellation token; honoured for I/O only.</param>
    ExceptionLogWriteResult Write(Exception exception, ExceptionLogContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Caller-supplied context that is embedded in the log record alongside the exception.
/// </summary>
public sealed record ExceptionLogContext(
    string ApplicationName,
    string ApplicationVersion,
    string CommandLine,
    string? CurrentCommand,
    string WorkingDirectory,
    string StoreRoot);

/// <summary>
/// Outcome of an <see cref="IExceptionLogWriter.Write"/> call. <see cref="LogFilePath"/>
/// is the path the writer actually used (which may be a fallback path if the store was
/// unwritable); it is always non-null and non-empty on a returned instance.
/// </summary>
public sealed record ExceptionLogWriteResult(
    string LogFilePath,
    bool UsedFallback,
    string? FallbackReason);
