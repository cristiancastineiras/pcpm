using FluentAssertions;
using pcpm.Infrastructure.Project;

namespace pcpm.Tests;

public class ProjectDiscoveryServiceTests : IAsyncLifetime
{
    private readonly TempWorkspace _ws = new();
    private readonly ProjectDiscoveryService _svc = new(new pcpm.Infrastructure.FileSystem.PhysicalFileSystem());

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _ws.DisposeAsync();

    [Fact]
    public async Task Default_pattern_finds_csproj_at_root()
    {
        await _ws.WriteFileAsync("App.csproj", "<Project/>");
        var projects = await _svc.FindProjectsAsync(_ws.Root, CancellationToken.None);
        projects.Should().ContainSingle()
            .Which.Should().EndWith("App.csproj");
    }

    [Fact]
    public async Task Default_pattern_finds_csproj_in_subdirectories()
    {
        await _ws.WriteFileAsync("src/App/App.csproj", "<Project/>");
        await _ws.WriteFileAsync("tests/App.Tests/App.Tests.csproj", "<Project/>");
        var projects = await _svc.FindProjectsAsync(_ws.Root, CancellationToken.None);
        projects.Should().HaveCount(2);
    }

    [Fact]
    public async Task Workspace_yaml_overrides_default_pattern()
    {
        await _ws.WriteFileAsync("src/App/App.csproj", "<Project/>");
        await _ws.WriteFileAsync("README.md", "ignore me");
        await _ws.WriteFileAsync("pcpm-workspace.yaml", """
            packages:
              - 'src/**/*.csproj'
            """);
        var projects = await _svc.FindProjectsAsync(_ws.Root, CancellationToken.None);
        projects.Should().ContainSingle()
            .Which.Should().EndWith("App.csproj");
    }

    [Fact]
    public async Task Returns_empty_when_no_projects()
    {
        var projects = await _svc.FindProjectsAsync(_ws.Root, CancellationToken.None);
        projects.Should().BeEmpty();
    }
}
