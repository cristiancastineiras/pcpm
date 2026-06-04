using FluentAssertions;
using NuGet.Versioning;
using pcpm.Core.Models;

namespace pcpm.Tests;

public class PackageVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("2.3.4-beta.1+sha.abc", "2.3.4-beta.1")]
    public void Create_parses_valid_semver(string raw, string roundtrip)
    {
        var v = PackageVersion.Create(raw);
        v.ToString().Should().Be(roundtrip);
        v.IsPrerelease.Should().Be(raw.Contains('-'));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void Create_rejects_garbage(string raw)
    {
        var act = () => PackageVersion.Create(raw);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Higher_versions_compare_greater()
    {
        var a = PackageVersion.Create("1.2.3");
        var b = PackageVersion.Create("1.2.4");
        a.CompareTo(b).Should().BeLessThan(0);
    }
}
