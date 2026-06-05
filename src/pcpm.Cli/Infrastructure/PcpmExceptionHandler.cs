using Microsoft.Extensions.Logging;
using pcpm.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli.Infrastructure;

/// <summary>
/// Central chokepoint for unhandled exceptions inside pcpm. All callers (Spectre's
/// <c>SetExceptionHandler</c>, the <c>try/catch</c> around <c>CommandApp.RunAsync</c>,
/// and the "command not recognised" pre-flight) route through
/// <see cref="HandleAsync"/> so the user always sees the same shape of message and
/// every exception is captured to a log file in the store.
///
/// <para>The handler is intentionally a plain object with an <see cref="IAnsiConsole"/>
/// and an <see cref="IExceptionLogWriter"/> injected — not a static — so the test
/// suite can substitute both and assert on what was written.</para>
/// </summary>
public sealed class PcpmExceptionHandler
{
    private readonly IAnsiConsole _console;
    private readonly IExceptionLogWriter _logWriter;
    private readonly ILogger<PcpmExceptionHandler> _logger;
    private readonly string _applicationName;
    private readonly string _applicationVersion;
    private readonly Func<string> _storeRootAccessor;

    public PcpmExceptionHandler(
        IAnsiConsole console,
        IExceptionLogWriter logWriter,
        ILogger<PcpmExceptionHandler> logger,
        string applicationName,
        string applicationVersion,
        Func<string> storeRootAccessor)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationName = applicationName;
        _applicationVersion = applicationVersion;
        _storeRootAccessor = storeRootAccessor;
    }

    /// <summary>
    /// Handle <paramref name="exception"/>, writing a diagnostic log, printing a friendly
    /// message, and returning the appropriate process exit code.
    /// </summary>
    /// <param name="exception">The exception that was caught. Must not be null.</param>
    /// <param name="args">The raw command-line argument vector, for the log context.</param>
    /// <param name="currentCommand">
    /// The name of the command that was being executed when the exception fired
    /// (e.g. <c>"install"</c>). May be null if the failure happened before command dispatch.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the log write.</param>
    public int HandleAsync(
        Exception exception,
        IReadOnlyList<string> args,
        string? currentCommand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 1. Classify
        var classified = ExceptionClassifier.Classify(exception);

        // 2. Write the diagnostic log (best-effort: never throws)
        ExceptionLogWriteResult? logResult = null;
        try
        {
            logResult = _logWriter.Write(
                exception,
                new ExceptionLogContext(
                    ApplicationName: _applicationName,
                    ApplicationVersion: _applicationVersion,
                    CommandLine: string.Join(' ', args ?? Array.Empty<string>()),
                    CurrentCommand: currentCommand,
                    WorkingDirectory: Directory.GetCurrentDirectory(),
                    StoreRoot: SafeStoreRoot()),
                cancellationToken);
        }
        catch (Exception logEx)
        {
            // The writer promises not to throw, but if it does (e.g. a bug in our
            // code), we degrade to a console-only error.
            _logger.LogError(logEx, "Exception log writer threw unexpectedly");
        }

        // 3. Render and print
        var rendered = ExceptionMessageBuilder.Build(classified, exception, logResult);
        try
        {
            _console.MarkupLine(rendered);
        }
        catch
        {
            // The console itself blew up (broken pipe, redirected handle, …).
            // Fall back to plain Console.Error so the user still sees *something*.
            try
            {
                Console.Error.WriteLine(classified.Headline);
                if (!string.IsNullOrWhiteSpace(classified.Details))
                    Console.Error.WriteLine(classified.Details);
                if (logResult is { LogFilePath.Length: > 0 })
                    Console.Error.WriteLine($"Diagnostic log: {logResult.LogFilePath}");
            }
            catch
            {
                // Last resort: swallow. We are literally the last line of defence.
            }
        }

        return classified.ExitCode;
    }

    /// <summary>
    /// Convenience entry point for the "command not recognised" pre-flight check.
    /// This is not technically an exception, so it doesn't go through the classifier
    /// — it just renders the standard "unknown command" message and exits non-zero.
    /// </summary>
    public int HandleUnknownCommand(string attempted, IReadOnlyList<string> args)
    {
        var recognised = string.Join(", ", KnownCommands.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        var message =
            $"[red]Command not recognised:[/] [yellow]'{ExceptionMessageBuilder.EscapeMarkup(attempted)}'[/]\n" +
            $"Run [yellow]pcpm --help[/] for the full list. Recognised commands: {ExceptionMessageBuilder.EscapeMarkup(recognised)}.";
        _console.MarkupLine(message);
        return 1;
    }

    /// <summary>
    /// Spectre entry point. Wraps the typed <see cref="HandleAsync"/> overload so the
    /// delegate signature expected by <c>c.SetExceptionHandler(...)</c> is satisfied.
    /// </summary>
    public int HandleForSpectre(Exception exception, ITypeResolver? resolver)
    {
        // Spectre doesn't tell us the raw args back, so we reach into Environment.
        // (We still get the original args from the caller — Program.Main captures
        // them — and feeds them via the typed overload; this overload is a defensive
        // safety net in case Spectre calls the handler with no other context.)
        var args = Environment.GetCommandLineArgs();
        var currentCommand = TryExtractCommandName(args);
        return HandleAsync(exception, args, currentCommand, CancellationToken.None);
    }

    private static string? TryExtractCommandName(IReadOnlyList<string> args) =>
        KnownCommands.ExtractCommandName(args);

    private string SafeStoreRoot()
    {
        try { return _storeRootAccessor(); }
        catch { return "(unavailable)"; }
    }
}
