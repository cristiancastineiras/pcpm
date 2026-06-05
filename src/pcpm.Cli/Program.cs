using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using pcpm.Cli.Commands;
using pcpm.Core.Abstractions;
using pcpm.Core.Services;
using pcpm.Infrastructure.Configuration;
using pcpm.Infrastructure.Conversion;
using pcpm.Infrastructure.Cpm;
using pcpm.Infrastructure.FileSystem;
using pcpm.Infrastructure.Lockfiles;
using pcpm.Infrastructure.MsBuild;
using pcpm.Infrastructure.NuGet;
using pcpm.Infrastructure.Process;
using pcpm.Infrastructure.Project;
using pcpm.Infrastructure.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace pcpm.Cli;

/// <summary>
/// Entry point. Wires the pcpm services into a plain <see cref="IServiceCollection"/>, then
/// hands control to a Spectre.Console.Cli <see cref="CommandApp"/>. We don't use the generic
/// host's <c>Build()</c> because that seals the service collection, which Spectre then trips
/// over when it lazily registers command types during <c>RunAsync</c>. The lazy provider in
/// <see cref="TypeRegistrar"/> sidesteps that cleanly.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();

        // ---- Configuration: read pcpm.json + PCPM_* env vars, both optional ----
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("pcpm.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "PCPM_")
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // ---- Logging ----
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSimpleConsole(opts =>
            {
                opts.SingleLine = true;
                opts.TimestampFormat = "HH:mm:ss ";
            });
            b.SetMinimumLevel(LogLevel.Warning);
        });

        // ---- Infrastructure ----
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IConvertService, ConvertService>();
#pragma warning disable CA1416 // HardlinkCreator is Windows-only at this time
        services.AddSingleton<IHardlinkCreator, HardlinkCreator>();
#pragma warning restore CA1416
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ILockfileService, LockfileService>();
        services.AddSingleton<ICpmFileService, CpmFileService>();
        services.AddSingleton<IProjectFileService, ProjectFileService>();
        services.AddSingleton<IProjectDiscovery, ProjectDiscoveryService>();
        services.AddSingleton<IDependencyResolver, DependencyResolver>();
        services.AddSingleton<ConfigurationLoader>();

        // ---- NuGet feed (single or multi-feed with auth, from pcpm.json) ----
        services.AddSingleton<INuGetFeed>(sp =>
        {
            var feedLogger = sp.GetRequiredService<ILogger<NuGetFeed>>();
            var multiLogger = sp.GetRequiredService<ILogger<MultiFeedNuGetFeed>>();
            var cfgLoader = sp.GetRequiredService<ConfigurationLoader>();
            var pcpmCfg = cfgLoader.LoadOrDefaultAsync(Directory.GetCurrentDirectory(), CancellationToken.None)
                              .GetAwaiter().GetResult();
            return MultiFeedNuGetFeed.Create(pcpmCfg, feedLogger, multiLogger);
        });

        services.AddSingleton<IPackageStore>(sp =>
        {
            var fs = sp.GetRequiredService<IFileSystem>();
            var hl = sp.GetRequiredService<IHardlinkCreator>();
            var log = sp.GetRequiredService<ILogger<PackageStore>>();
            var cfg = sp.GetRequiredService<IConfiguration>();
            return new PackageStore(
                fs,
                hl,
                log,
                storeRootOverride: cfg["pcpm:storePath"],
                globalPackagesOverride: cfg["pcpm:globalPackagesPath"]);
        });

        services.AddSingleton<IWorkspaceLocator>(_ => new WorkspaceLocator());
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        services.AddSingleton<MsBuildTargetsWriter>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(c =>
        {
            c.SetApplicationName("pcpm");
            c.SetApplicationVersion("0.1.0");
            c.Settings.ApplicationName = "pcpm";
            c.PropagateExceptions();
            c.AddCommand<InitCommand>("init")
                .WithDescription("Initialise a pcpm workspace (CPM, pcpm.json, store).")
                .WithExample(["init"]);
            c.AddCommand<AddCommand>("add")
                .WithDescription("Add a package to CPM and to one or more projects.")
                .WithExample(["add", "Newtonsoft.Json"])
                .WithExample(["add", "Newtonsoft.Json", "-v", "13.0.3"])
                .WithExample(["add", "Serilog", "-p", "src/MyApp/MyApp.csproj"]);
            c.AddCommand<InstallCommand>("install")
                .WithAlias("i")
                .WithDescription("Resolve the dependency graph, populate the store and link to ~/.nuget/packages.")
                .WithExample(["install"]);
            c.AddCommand<ListCommand>("list")
                .WithAlias("ls")
                .WithDescription("List packages resolved in pcpm.lock.")
                .WithExample(["list"]);
            c.AddCommand<RemoveCommand>("remove")
                .WithAlias("rm")
                .WithDescription("Remove a package from CPM and all referencing projects.")
                .WithExample(["remove", "Newtonsoft.Json"]);
            c.AddCommand<WhyCommand>("why")
                .WithDescription("Show why a package is in the dependency tree.")
                .WithExample(["why", "Newtonsoft.Json"]);
            c.AddCommand<OutdatedCommand>("outdated")
                .WithDescription("Show packages with a newer version available on the feed.")
                .WithExample(["outdated"]);
            c.AddCommand<StoreCommand>("store")
                .WithDescription("Inspect or manage the global content-addressable store.")
                .WithExample(["store", "status"]);
            c.AddCommand<ConvertCommand>("convert")
                .WithDescription("Convert a workspace to Central Package Management (or revert with --revert).")
                .WithExample(["convert"])
                .WithExample(["convert", "--dry-run"])
                .WithExample(["convert", "--revert"]);
            c.AddCommand<DoctorCommand>("doctor")
                .WithDescription("Check workspace health: CPM correctness, CVEs, orphaned entries, lockfile sync.")
                .WithExample(["doctor"])
                .WithExample(["doctor", "--no-cve"]);
            c.AddCommand<AuditCommand>("audit")
                .WithDescription("Security and license audit with optional SBOM generation.")
                .WithExample(["audit"])
                .WithExample(["audit", "--no-sbom"])
                .WithExample(["audit", "--output", "./reports"]);
            c.AddCommand<CiCommand>("ci")
                .WithDescription("CI-optimised install: verifies lockfile sync, restores from store, runs dotnet restore.")
                .WithExample(["ci"])
                .WithExample(["ci", "--locked-mode"]);
        });

        return await app.RunAsync(args).ConfigureAwait(false);
    }
}
