using FluentAssertions;
using pcpm.Core.Models;
using pcpm.Infrastructure.Cpm;

namespace pcpm.Tests;

public class CpmFileServiceTests : IAsyncLifetime
{
    private readonly TempWorkspace _ws = new();
    private readonly CpmFileService _cpm = new(new pcpm.Infrastructure.FileSystem.PhysicalFileSystem());

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _ws.DisposeAsync();

    [Fact]
    public async Task Read_returns_empty_when_file_does_not_exist()
    {
        var cpm = await _cpm.ReadAsync(_ws.Root, CancellationToken.None);
        cpm.IsEnabled.Should().BeFalse();
        cpm.PackageVersions.Should().BeEmpty();
    }

    [Fact]
    public async Task SetPackageVersion_creates_file_and_round_trips()
    {
        var id = PackageId.Create("Newtonsoft.Json");
        var v = PackageVersion.Create("13.0.3");

        await _cpm.SetPackageVersionAsync(_ws.Root, id, v, CancellationToken.None);

        var read = await _cpm.ReadAsync(_ws.Root, CancellationToken.None);
        read.IsEnabled.Should().BeTrue();
        read.PackageVersions.Should().ContainKey(id);
        read.PackageVersions[id].ToString().Should().Be("13.0.3");
    }

    [Fact]
    public async Task RemovePackageVersion_clears_the_entry()
    {
        var id = PackageId.Create("X");
        var v = PackageVersion.Create("1.0.0");
        await _cpm.SetPackageVersionAsync(_ws.Root, id, v, CancellationToken.None);
        await _cpm.RemovePackageVersionAsync(_ws.Root, id, CancellationToken.None);

        var read = await _cpm.ReadAsync(_ws.Root, CancellationToken.None);
        read.PackageVersions.Should().NotContainKey(id);
    }

    [Fact]
    public async Task Writing_preserves_user_added_propertygroups()
    {
        // Seed the file with a user-added property.
        await _ws.WriteFileAsync("Directory.Packages.props", """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <MyCustomProp>hello</MyCustomProp>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Existing" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        await _cpm.SetPackageVersionAsync(_ws.Root, PackageId.Create("New"), PackageVersion.Create("2.0.0"), CancellationToken.None);

        var text = await _ws.FileSystem.ReadAllTextAsync(_ws.Path("Directory.Packages.props"), CancellationToken.None);
        text.Should().Contain("<MyCustomProp>hello</MyCustomProp>");
        text.Should().Contain("Existing");
        text.Should().Contain("New");
    }
}
