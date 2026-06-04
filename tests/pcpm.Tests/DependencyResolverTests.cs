using FluentAssertions;
using NuGet.Versioning;
using NSubstitute;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;
using pcpm.Core.Services;

namespace pcpm.Tests;

public class DependencyResolverTests
{
    [Fact]
    public async Task Resolves_a_single_direct_package()
    {
        var feed = Substitute.For<INuGetFeed>();
        var a = PackageId.Create("A");
        feed.ListVersionsAsync(a, Arg.Any<CancellationToken>())
            .Returns(new PackageVersion[] { PackageVersion.Create("1.0.0"), PackageVersion.Create("1.1.0") });
        feed.GetMetadataAsync(a, Arg.Any<PackageVersion>(), Arg.Any<CancellationToken>())
            .Returns(MakeMetadata("A", "1.1.0", deps: Array.Empty<RawDependency>()));

        var resolver = new DependencyResolver();
        var direct = new[] { new PackageDependency(a, VersionRange.Parse("[1.0.0,)")) };

        var result = await resolver.ResolveAsync(direct, "net10.0", feed, CancellationToken.None);

        result.HasConflicts.Should().BeFalse();
        result.Resolved.Should().ContainKey(a);
        result.Resolved[a].Version.ToString().Should().Be("1.1.0");
    }

    [Fact]
    public async Task Resolves_transitive_dependencies()
    {
        var a = PackageId.Create("A");
        var b = PackageId.Create("B");
        var feed = Substitute.For<INuGetFeed>();

        feed.ListVersionsAsync(a, Arg.Any<CancellationToken>())
            .Returns(new PackageVersion[] { PackageVersion.Create("1.0.0") });
        feed.GetMetadataAsync(a, Arg.Any<PackageVersion>(), Arg.Any<CancellationToken>())
            .Returns(MakeMetadata("A", "1.0.0", deps: new[] { new RawDependency("B", "1.0.0") }));

        feed.ListVersionsAsync(b, Arg.Any<CancellationToken>())
            .Returns(new PackageVersion[] { PackageVersion.Create("1.0.0"), PackageVersion.Create("2.0.0") });
        feed.GetMetadataAsync(b, Arg.Any<PackageVersion>(), Arg.Any<CancellationToken>())
            .Returns(MakeMetadata("B", "2.0.0", deps: Array.Empty<RawDependency>()));

        var resolver = new DependencyResolver();
        var direct = new[] { new PackageDependency(a, VersionRange.Parse("[1.0.0]")) };

        var result = await resolver.ResolveAsync(direct, "net10.0", feed, CancellationToken.None);

        result.HasConflicts.Should().BeFalse();
        result.Resolved.Should().ContainKey(a);
        result.Resolved.Should().ContainKey(b);
        result.Resolved[b].Version.ToString().Should().Be("2.0.0"); // highest matching
    }

    [Fact]
    public async Task Reports_a_conflict_when_no_version_satisfies_all_ranges()
    {
        var a = PackageId.Create("A");
        var b = PackageId.Create("B");
        var feed = Substitute.For<INuGetFeed>();

        feed.ListVersionsAsync(a, Arg.Any<CancellationToken>())
            .Returns(new PackageVersion[] { PackageVersion.Create("1.0.0") });
        feed.GetMetadataAsync(a, PackageVersion.Create("1.0.0"), Arg.Any<CancellationToken>())
            .Returns(MakeMetadata("A", "1.0.0", deps: new[] { new RawDependency("B", "[1.0.0]") }));

        feed.ListVersionsAsync(b, Arg.Any<CancellationToken>())
            .Returns(new PackageVersion[] { PackageVersion.Create("2.0.0") });

        // Plus a direct dep on B at 2.0.0 — but the feed only has 1.0.0, so neither path can resolve.
        // To trigger a real conflict we need an overlap where the ranges can't be satisfied.
        // Direct: B [2.0.0]; transitive: B [1.0.0]; available: 1.0.0, 2.0.0
        var direct = new[]
        {
            new PackageDependency(a, VersionRange.Parse("[1.0.0]")),
            new PackageDependency(b, VersionRange.Parse("[2.0.0]")),
        };
        feed.ListVersionsAsync(b, Arg.Any<CancellationToken>())
            .Returns(new PackageVersion[] { PackageVersion.Create("1.0.0") }); // only 1.0.0 available

        var resolver = new DependencyResolver();
        var result = await resolver.ResolveAsync(direct, "net10.0", feed, CancellationToken.None);

        result.HasConflicts.Should().BeTrue();
        result.Conflicts.Should().Contain(c => c.Id.Value == "B");
    }

    private static PackageMetadata MakeMetadata(
        string id, string version, IReadOnlyList<RawDependency> deps) =>
        new(
            Id: PackageId.Create(id),
            Version: PackageVersion.Create(version),
            Description: null,
            Authors: Array.Empty<string>(),
            ProjectUrl: null,
            LicenseUrl: null,
            Published: null,
            PackageContentUrl: "",
            DependencyGroups: new[]
            {
                new DependencyGroup("net10.0", deps),
            });
}
