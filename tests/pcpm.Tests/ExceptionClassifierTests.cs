using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using pcpm.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace pcpm.Tests;

/// <summary>
/// Asserts the <see cref="ExceptionClassifier"/> maps each known exception type to the
/// right <see cref="ErrorCategory"/>, headline, and exit code. These are the
/// user-visible behaviour guarantees: a regression here means pcpm shows a wrong
/// message for a class of failures.
/// </summary>
public sealed class ExceptionClassifierTests
{
    [Fact]
    public void OperationCanceledException_is_Cancelled_with_exit_130()
    {
        var c = ExceptionClassifier.Classify(new OperationCanceledException());
        c.Category.Should().Be(ErrorCategory.Cancelled);
        c.ExitCode.Should().Be(130);
        c.Headline.Should().Contain("cancelled");
    }

    [Fact]
    public void TaskCanceledException_is_Cancelled_with_exit_130()
    {
        var c = ExceptionClassifier.Classify(new TaskCanceledException());
        c.Category.Should().Be(ErrorCategory.Cancelled);
        c.ExitCode.Should().Be(130);
    }

    [Fact]
    public void TaskCanceledException_wrapping_HttpRequestException_is_Network_with_timeout_message()
    {
        var inner = new HttpRequestException("connection refused");
        var outer = new TaskCanceledException("request timed out", inner);

        var c = ExceptionClassifier.Classify(outer);
        c.Category.Should().Be(ErrorCategory.Network);
        c.Headline.Should().Contain("timed out");
    }

    [Fact]
    public void CommandParseException_is_UserInput_with_exit_2()
    {
        // Spectre's CommandParseException has an internal ctor; we instantiate it
        // without running a constructor and set Message via reflection. This is the
        // standard .NET pattern for testing code that handles sealed third-party
        // exception types.
        var ex = MakeShellException<CommandParseException>("missing value for --version");
        var c = ExceptionClassifier.Classify(ex);
        c.Category.Should().Be(ErrorCategory.UserInput);
        c.ExitCode.Should().Be(2);
    }

    [Fact]
    public void CommandRuntimeException_is_UserInput()
    {
        var ex = MakeShellException<CommandRuntimeException>("the command crashed");
        var c = ExceptionClassifier.Classify(ex);
        c.Category.Should().Be(ErrorCategory.UserInput);
    }

    [Fact]
    public void ArgumentException_is_UserInput()
    {
        var c = ExceptionClassifier.Classify(new ArgumentException("x must be > 0"));
        c.Category.Should().Be(ErrorCategory.UserInput);
        c.Details.Should().Contain("x must be > 0");
    }

    [Fact]
    public void JsonException_is_Configuration()
    {
        var json = new System.Text.Json.JsonException("bad token", null, 0, 0);
        var c = ExceptionClassifier.Classify(json);
        c.Category.Should().Be(ErrorCategory.Configuration);
        c.Details.Should().Contain("line: 0");
    }

    [Fact]
    public void UnauthorizedAccessException_is_Environment()
    {
        var c = ExceptionClassifier.Classify(new UnauthorizedAccessException("read-only"));
        c.Category.Should().Be(ErrorCategory.Environment);
        c.Headline.Should().Contain("Permission");
    }

    [Fact]
    public void DirectoryNotFoundException_is_Environment()
    {
        var c = ExceptionClassifier.Classify(new DirectoryNotFoundException("/nope"));
        c.Category.Should().Be(ErrorCategory.Environment);
        c.Details.Should().Contain("/nope");
    }

    [Fact]
    public void FileNotFoundException_is_Environment()
    {
        var c = ExceptionClassifier.Classify(new FileNotFoundException("missing.txt"));
        c.Category.Should().Be(ErrorCategory.Environment);
    }

    [Fact]
    public void PathTooLongException_is_Environment()
    {
        var c = ExceptionClassifier.Classify(new PathTooLongException());
        c.Category.Should().Be(ErrorCategory.Environment);
        c.Headline.Should().Contain("Path too long");
    }

    [Fact]
    public void IOException_is_Environment()
    {
        var c = ExceptionClassifier.Classify(new IOException("disk gone"));
        c.Category.Should().Be(ErrorCategory.Environment);
        c.Details.Should().Contain("disk gone");
    }

    [Fact]
    public void HttpRequestException_is_Network()
    {
        var c = ExceptionClassifier.Classify(new HttpRequestException("503"));
        c.Category.Should().Be(ErrorCategory.Network);
    }

    [Fact]
    public void InvalidOperationException_is_UserInput()
    {
        var c = ExceptionClassifier.Classify(new InvalidOperationException("bad state"));
        c.Category.Should().Be(ErrorCategory.UserInput);
    }

    [Fact]
    public void NotImplementedException_is_ExpectedGap()
    {
        var c = ExceptionClassifier.Classify(new NotImplementedException());
        c.Category.Should().Be(ErrorCategory.ExpectedGap);
        c.Headline.Should().Contain("not implemented");
    }

    [Fact]
    public void Unknown_exception_type_is_Unexpected_and_keeps_exit_1()
    {
        var c = ExceptionClassifier.Classify(new CustomTestException("weird"));
        c.Category.Should().Be(ErrorCategory.Unexpected);
        c.Headline.Should().Contain("CustomTestException");
        c.ExitCode.Should().Be(1);
    }

    [Fact]
    public void Classify_throws_on_null()
    {
        var act = () => ExceptionClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Allocate a sealed exception type without invoking its (internal) constructor,
    /// then set <c>Message</c> via reflection. <see cref="RuntimeHelpers.GetUninitializedObject"/>
    /// is the modern .NET pattern for testing handlers of sealed third-party exception
    /// types whose constructors are not public.
    /// </summary>
    private static T MakeShellException<T>(string message) where T : Exception
    {
        var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        var messageField = typeof(Exception).GetField("_message", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        messageField?.SetValue(instance, message);
        return instance;
    }

    private sealed class CustomTestException : Exception
    {
        public CustomTestException(string message) : base(message) { }
    }
}
