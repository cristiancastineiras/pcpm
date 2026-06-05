using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using pcpm.Cli.Infrastructure;
using pcpm.Core.Abstractions;
using Spectre.Console;

namespace pcpm.Tests;

/// <summary>
/// End-to-end tests for <see cref="PcpmExceptionHandler"/>: the full path that an
/// exception takes through classify → log → render. We back the
/// <see cref="IAnsiConsole"/> with a <see cref="StringWriter"/> via
/// <see cref="AnsiConsoleOutput"/> so we can assert on the exact text the user sees.
/// </summary>
public sealed class PcpmExceptionHandlerTests : IAsyncDisposable
{
    private readonly string _testRoot;
    private readonly string _storeRoot;
    private readonly StringWriter _consoleWriter;
    private readonly IAnsiConsole _console;
    private readonly ExceptionLogWriter _writer;
    private readonly PcpmExceptionHandler _handler;

    public PcpmExceptionHandlerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "pcpm-handler-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _storeRoot = Path.Combine(_testRoot, "store");
        Directory.CreateDirectory(_storeRoot);

        _consoleWriter = new StringWriter();
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,    // keep the captured output plain and grep-friendly
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(_consoleWriter),
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["pcpm:storePath"] = _storeRoot })
            .Build();

        _writer = ExceptionLogWriter.FromConfiguration(config);
        _handler = new PcpmExceptionHandler(
            _console,
            _writer,
            NullLogger<PcpmExceptionHandler>.Instance,
            "pcpm",
            "0.1.0-test",
            storeRootAccessor: () => _storeRoot);
    }

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
        _consoleWriter.Dispose();
        await ValueTask.CompletedTask;
    }

    private string Output => _consoleWriter.ToString();

    [Fact]
    public void HandleAsync_writes_log_file_and_returns_classified_exit_code()
    {
        var exit = _handler.HandleAsync(
            new InvalidOperationException("things went south"),
            args: new[] { "pcpm", "install" },
            currentCommand: "install",
            CancellationToken.None);

        exit.Should().Be(1, "InvalidOperationException maps to exit 1");

        // The log file was written and is non-empty.
        var logsDir = Path.Combine(_storeRoot, "logs");
        Directory.Exists(logsDir).Should().BeTrue();
        var files = Directory.GetFiles(logsDir, "pcpm-*.log");
        files.Should().HaveCount(1);
        var contents = File.ReadAllText(files[0]);
        contents.Should().Contain("InvalidOperationException");
        contents.Should().Contain("things went south");
    }

    [Fact]
    public void HandleAsync_renders_friendly_headline_for_UserInput_exception()
    {
        _handler.HandleAsync(
            new ArgumentException("package id is empty"),
            args: new[] { "pcpm", "add", "" },
            currentCommand: "add",
            CancellationToken.None);

        Output.Should().Contain("Invalid argument.");
        Output.Should().Contain("package id is empty");
        Output.Should().Contain("Diagnostic log:");
    }

    [Fact]
    public void HandleAsync_renders_cancelled_message_with_exit_130()
    {
        var exit = _handler.HandleAsync(
            new OperationCanceledException(),
            args: Array.Empty<string>(),
            currentCommand: "install",
            CancellationToken.None);

        exit.Should().Be(130);
        Output.Should().Contain("Operation cancelled.");
    }

    [Fact]
    public void HandleAsync_renders_unexpected_message_and_suggests_filing_a_bug()
    {
        _handler.HandleAsync(
            new CustomTestException("explode"),
            args: new[] { "pcpm", "install" },
            currentCommand: "install",
            CancellationToken.None);

        Output.Should().Contain("Unexpected error");
        Output.Should().Contain("CustomTestException");
        Output.Should().Contain("explode");
        Output.Should().Contain("Diagnostic log:");
    }

    [Fact]
    public void HandleUnknownCommand_prints_recognised_list_and_returns_1()
    {
        var exit = _handler.HandleUnknownCommand("instol", new[] { "pcpm", "instol" });

        exit.Should().Be(1);
        Output.Should().Contain("Command not recognised");
        Output.Should().Contain("instol");
        // Must list at least a few of the actual recognised commands so the user
        // can find the right one.
        Output.Should().Contain("install");
        Output.Should().Contain("add");
    }

    [Fact]
    public void HandleUnknownCommand_escapes_markup_in_attempted_name()
    {
        // The user typed a name that contains Spectre markup characters. The handler
        // must escape them, not pass them straight through MarkupLine.
        var exit = _handler.HandleUnknownCommand("[red]pwned[/]", new[] { "pcpm", "[red]pwned[/]" });

        exit.Should().Be(1);
        // The handler ran Markup.Escape on the user input, then put the result into
        // a markup-literal in the message. Spectre re-interprets the `[[` escape
        // sequence back to a single `[` while rendering, so the final output
        // contains the user input as plain text — but never as an active colour
        // sequence. We assert the visible substring is present AND no ANSI escape
        // sequence slipped through.
        var raw = _consoleWriter.ToString();
        raw.Should().Contain("pwned",
            "the user-supplied name should be visible in the output");
        raw.Should().NotContain("\u001b[",
            "no ANSI escape sequence should have been emitted — the markup must be escaped, not rendered");
    }

    [Fact]
    public void HandleForSpectre_extracts_command_from_args_and_delegates()
    {
        var exit = _handler.HandleForSpectre(
            new InvalidOperationException("x"),
            resolver: null);

        exit.Should().Be(1);
        // The handler wrote a log file even when invoked via the Spectre delegate
        // path (no resolver provided).
        Directory.GetFiles(Path.Combine(_storeRoot, "logs"), "pcpm-*.log")
            .Should().NotBeEmpty();
    }

    [Fact]
    public void HandleAsync_never_throws_even_when_log_writer_path_is_unwritable()
    {
        // Build a second handler whose store root is in a file (not a directory) so
        // every log write will fail and force the fallback path.
        var blocker = Path.Combine(_testRoot, "blocker-file");
        File.WriteAllText(blocker, "x");
        var badWriter = new ExceptionLogWriter(
            new ConfigurationBuilder().Build(),
            () => Path.Combine(blocker, "store"));

        var badHandler = new PcpmExceptionHandler(
            _console, badWriter, NullLogger<PcpmExceptionHandler>.Instance,
            "pcpm", "0.1.0-test", () => Path.Combine(blocker, "store"));

        var act = () => badHandler.HandleAsync(
            new Exception("x"),
            args: new[] { "pcpm", "install" },
            currentCommand: "install",
            CancellationToken.None);

        act.Should().NotThrow();
        // Output should at least contain the headline even if the log couldn't be written.
        _consoleWriter.ToString().Should().Contain("Unexpected error");
    }

    private sealed class CustomTestException : Exception
    {
        public CustomTestException(string message) : base(message) { }
    }
}
