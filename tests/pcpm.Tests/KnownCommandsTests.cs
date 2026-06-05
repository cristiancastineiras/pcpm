using FluentAssertions;
using pcpm.Cli.Infrastructure;

namespace pcpm.Tests;

/// <summary>
/// Locks down the pre-flight command-recognition list. If anyone adds a new command
/// in <c>Program.cs</c>'s <c>CommandApp</c> registration without adding it to
/// <see cref="KnownCommands"/>, the corresponding pre-flight check will silently
/// start treating it as "unknown" — these tests catch that drift.
/// </summary>
public sealed class KnownCommandsTests
{
    [Theory]
    [InlineData("init")]
    [InlineData("add")]
    [InlineData("install")]
    [InlineData("list")]
    [InlineData("remove")]
    [InlineData("why")]
    [InlineData("outdated")]
    [InlineData("store")]
    [InlineData("convert")]
    [InlineData("doctor")]
    [InlineData("audit")]
    [InlineData("ci")]
    public void All_main_command_names_are_recognised(string name)
    {
        KnownCommands.IsKnown(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("i")]
    [InlineData("ls")]
    [InlineData("rm")]
    public void Aliases_are_recognised(string alias)
    {
        KnownCommands.IsKnown(alias).Should().BeTrue();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    [InlineData("-V")]
    public void Global_flags_are_recognised(string flag)
    {
        KnownCommands.IsKnown(flag).Should().BeTrue();
    }

    [Theory]
    [InlineData("foobar")]
    [InlineData("Instal")]    // typo, missing trailing l
    [InlineData("list-all")]  // not a real command
    [InlineData("--unknown")]
    public void Unknown_names_are_not_recognised(string name)
    {
        KnownCommands.IsKnown(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("INSTALL", true)]   // case-insensitive
    [InlineData("Install", true)]
    [InlineData("iNsTaLl", true)]
    [InlineData("rm", true)]
    [InlineData("RM", true)]
    public void Lookup_is_case_insensitive(string name, bool expected)
    {
        KnownCommands.IsKnown(name).Should().Be(expected);
    }

    [Fact]
    public void ExtractCommandName_returns_first_positional_argument()
    {
        KnownCommands.ExtractCommandName(new[] { "install", "--no-restore" })
            .Should().Be("install");
    }

    [Fact]
    public void ExtractCommandName_skips_options()
    {
        KnownCommands.ExtractCommandName(new[] { "--no-restore", "add", "Foo" })
            .Should().Be("add");
    }

    [Fact]
    public void ExtractCommandName_returns_null_when_no_positional()
    {
        KnownCommands.ExtractCommandName(Array.Empty<string>())
            .Should().BeNull();
        KnownCommands.ExtractCommandName(new[] { "--help" })
            .Should().BeNull();
        KnownCommands.ExtractCommandName(new[] { "-V", "--version" })
            .Should().BeNull();
    }

    [Fact]
    public void No_command_name_duplicates_names_and_aliases()
    {
        // A name in both sets is a configuration smell — pick one or the other.
        var overlap = KnownCommands.Names.Intersect(KnownCommands.Aliases).ToList();
        overlap.Should().BeEmpty();
    }
}
