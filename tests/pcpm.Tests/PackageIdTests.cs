using FluentAssertions;
using pcpm.Core.Models;

namespace pcpm.Tests;

public class PackageIdTests
{
    [Theory]
    [InlineData("Newtonsoft.Json")]
    [InlineData("serilog")]
    [InlineData("My-Package.Name_v2")]
    public void Create_accepts_valid_ids(string raw)
    {
        var id = PackageId.Create(raw);
        id.Value.Should().Be(raw);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".starts-with-dot")]
    [InlineData("ends-with-dot.")]
    [InlineData("has spaces")]
    public void Create_rejects_invalid_ids(string raw)
    {
        var act = () => PackageId.Create(raw);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryCreate_returns_false_for_invalid()
    {
        PackageId.TryCreate("not valid", out var id).Should().BeFalse();
        id.Value.Should().BeNullOrEmpty();
    }
}
