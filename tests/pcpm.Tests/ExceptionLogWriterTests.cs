using FluentAssertions;
using Microsoft.Extensions.Configuration;
using pcpm.Cli.Infrastructure;
using pcpm.Core.Abstractions;

namespace pcpm.Tests;

/// <summary>
/// Behavioural tests for <see cref="ExceptionLogWriter"/>: verifies that a log file
/// is actually written into the store, that its content captures the exception chain
/// and the supplied context, and that the fallback path works when the store
/// directory cannot be created.
/// </summary>
public sealed class ExceptionLogWriterTests : IAsyncDisposable
{
    private readonly string _storeRoot;
    private readonly string _testRoot;
    private readonly IConfiguration _config;
    private readonly ExceptionLogWriter _writer;

    public ExceptionLogWriterTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "pcpm-log-writer-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _storeRoot = Path.Combine(_testRoot, "store");
        Directory.CreateDirectory(_storeRoot);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["pcpm:storePath"] = _storeRoot,
            })
            .Build();

        _writer = new ExceptionLogWriter(_config, () => _storeRoot);
    }

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
        await ValueTask.CompletedTask;
    }

    [Fact]
    public void Write_creates_log_file_under_store_logs_directory()
    {
        var result = _writer.Write(
            new InvalidOperationException("boom"),
            BuildContext("install"),
            CancellationToken.None);

        File.Exists(result.LogFilePath).Should().BeTrue();
        var expectedDir = Path.Combine(_storeRoot, "logs");
        Path.GetDirectoryName(result.LogFilePath).Should().Be(expectedDir);
        result.UsedFallback.Should().BeFalse();
        result.FallbackReason.Should().BeNull();
    }

    [Fact]
    public void Write_filename_uses_pinned_utc_timestamp_pattern()
    {
        var fixedInstant = new DateTimeOffset(2026, 06, 05, 09, 18, 32, 123, TimeSpan.Zero);
        var name = ExceptionLogWriter.BuildLogFileName(fixedInstant);
        name.Should().Be("pcpm-20260605-091832-123.log");
    }

    [Fact]
    public void Write_records_command_line_command_cwd_and_store_path()
    {
        var result = _writer.Write(
            new Exception("hello"),
            new ExceptionLogContext(
                ApplicationName: "pcpm",
                ApplicationVersion: "0.1.0",
                CommandLine: "pcpm add Newtonsoft.Json -v 13.0.3",
                CurrentCommand: "add",
                WorkingDirectory: "/tmp/repo",
                StoreRoot: _storeRoot),
            CancellationToken.None);

        var text = File.ReadAllText(result.LogFilePath);
        text.Should().Contain("application:   pcpm 0.1.0");
        text.Should().Contain("cwd:           /tmp/repo");
        text.Should().Contain("store_root:    " + _storeRoot);
        text.Should().Contain("command_line:  pcpm add Newtonsoft.Json -v 13.0.3");
        text.Should().Contain("command:       add");
    }

    [Fact]
    public void Write_includes_type_message_and_stack_for_outer_and_inner_exceptions()
    {
        var inner = new ArgumentException("inner detail");
        var outer = new InvalidOperationException("outer detail", inner);

        var result = _writer.Write(outer, BuildContext("install"), CancellationToken.None);
        var text = File.ReadAllText(result.LogFilePath);

        text.Should().Contain("--- exception #0 (outer) ---");
        text.Should().Contain("type:        System.InvalidOperationException");
        text.Should().Contain("message:     outer detail");

        text.Should().Contain("--- exception #1 (inner) ---");
        text.Should().Contain("type:        System.ArgumentException");
        text.Should().Contain("message:     inner detail");

        // stack_trace field is present (even if empty) for both levels
        text.Should().Contain("stack_trace:");
    }

    [Fact]
    public void Write_uses_fallback_path_when_store_cannot_be_written_to()
    {
        // Point the writer at a path that cannot be created (file in the way
        // where the directory should be).
        var blocker = Path.Combine(_testRoot, "blocker");
        File.WriteAllText(blocker, "x");
        var badStoreRoot = Path.Combine(blocker, "store");

        var localWriter = new ExceptionLogWriter(_config, () => badStoreRoot);
        var result = localWriter.Write(
            new Exception("x"),
            BuildContext("install"),
            CancellationToken.None);

        result.UsedFallback.Should().BeTrue();
        result.FallbackReason.Should().NotBeNullOrEmpty();
        // The fallback should still produce a usable file under %TEMP%/pcpm-logs.
        File.Exists(result.LogFilePath).Should().BeTrue();
        Path.GetDirectoryName(result.LogFilePath).Should().StartWith(
            Path.Combine(Path.GetTempPath(), "pcpm-logs"));
    }

    [Fact]
    public void Write_atomic_write_leaves_no_half_written_file()
    {
        var result = _writer.Write(
            new Exception("x"),
            BuildContext("install"),
            CancellationToken.None);

        // After a successful write, no .tmp sibling should be left around.
        var dir = Path.GetDirectoryName(result.LogFilePath)!;
        Directory.EnumerateFiles(dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Write_never_throws_even_with_pathological_context()
    {
        var act = () => _writer.Write(
            new Exception("x"),
            new ExceptionLogContext(
                ApplicationName: "pcpm",
                ApplicationVersion: "0.1.0",
                CommandLine: "with\nnewlines\tand\0nulls",
                CurrentCommand: "install",
                WorkingDirectory: "/",
                StoreRoot: _storeRoot),
            CancellationToken.None);

        act.Should().NotThrow();
    }

    [Fact]
    public void FromConfiguration_falls_back_to_default_store_path_when_key_missing()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var writer = ExceptionLogWriter.FromConfiguration(emptyConfig);
        // We can't easily observe the resolved path without exposing it, but we can
        // at least assert that the factory doesn't throw and that Write succeeds
        // against the resolved default.
        var result = writer.Write(new Exception("x"), BuildContext("install"), CancellationToken.None);
        File.Exists(result.LogFilePath).Should().BeTrue();
    }

    private ExceptionLogContext BuildContext(string command) =>
        new(
            ApplicationName: "pcpm",
            ApplicationVersion: "0.1.0",
            CommandLine: $"pcpm {command}",
            CurrentCommand: command,
            WorkingDirectory: "/tmp/test",
            StoreRoot: _storeRoot);
}
