using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using pcpm.Cli.Commands;
using pcpm.Cli.Infrastructure;
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
///
/// <para>Global exception handling is layered:</para>
/// <list type="number">
///   <item>A pre-flight check in <c>Main</c> catches "command not recognised" before
///         Spectre ever sees the args, so we control the user-facing message 100%.</item>
///   <item>Spectre's <c>SetExceptionHandler</c> catches anything that escapes a command's
///         <c>ExecuteAsync</c> (or a parse/runtime error) and routes it through
///         <see cref="PcpmExceptionHandler"/>, which classifies the error, writes a
///         diagnostic log to the store, and prints a friendly message.</item>
///   <item>A last-resort <c>try/catch</c> wraps <c>app.RunAsync</c> for the small set of
///         failures that escape Spectre itself (e.g. the host crashing during command
///         construction).</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>Application name, used in log headers and the user-facing banner.</summary>
    public const string ApplicationName = "pcpm";

    /// <summary>Application version, read from assembly metadata at startup.</summary>
    public static readonly string ApplicationVersion = ExceptionLogWriter.GetApplicationVersion();

    public static async Task<int> Main(string[] args)
    {
        // ---- Pre-flight: catch "command not recognised" before Spectre does ----
        // This gives us 100% control over the wording for the most common user mistake,
        // and it lets us route it through the same console + DI container so the look
        // matches the rest of pcpm's output.
        if (ShouldPreFlightCheck(args))
        {
            var candidate = KnownCommands.ExtractCommandName(args);
            if (candidate is not null && !KnownCommands.IsKnown(candidate))
            {
                // Defer construction of the handler until here so we only pay the
                // cost of the DI graph on the (rare) error path. We still need the
                // IAnsiConsole + IExceptionLogWriter that the handler depends on, so
                // we build a minimal container.
                return await HandleUnknownCommandAsync(args, candidate).ConfigureAwait(false);
            }
        }

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

        // ---- Exception handling ----
        // The handler depends on IAnsiConsole + the log writer; both are already
        // registered above. The store-root accessor reads pcpm:storePath the same
        // way the package store does, so the two agree on where logs go.
        services.AddSingleton<IExceptionLogWriter>(sp =>
            ExceptionLogWriter.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<PcpmExceptionHandler>(sp => new PcpmExceptionHandler(
            sp.GetRequiredService<IAnsiConsole>(),
            sp.GetRequiredService<IExceptionLogWriter>(),
            sp.GetRequiredService<ILogger<PcpmExceptionHandler>>(),
            ApplicationName,
            ApplicationVersion,
            storeRootAccessor: () =>
            {
                var fromConfig = sp.GetRequiredService<IConfiguration>()["pcpm:storePath"];
                return string.IsNullOrWhiteSpace(fromConfig) ? PackageStore.DefaultStorePath() : fromConfig;
            }));

        services.AddSingleton<MsBuildTargetsWriter>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(c =>
        {
            c.SetApplicationName(ApplicationName);
            c.SetApplicationVersion(ApplicationVersion);
            c.Settings.ApplicationName = ApplicationName;

            // Spectre-level exception handler: anything that escapes a command's
            // ExecuteAsync (or a parse/runtime error) routes through our handler,
            // which classifies the error, writes a log, and returns the exit code.
            // We resolve the handler from the registrar's container so it picks up
            // the same DI graph as everything else.
            c.SetExceptionHandler((ex, resolver) =>
            {
                if (resolver?.Resolve(typeof(PcpmExceptionHandler)) is PcpmExceptionHandler handler)
                    return handler.HandleForSpectre(ex, resolver);

                // Defensive fallback: if the container couldn't even build the
                // handler, render a minimal message and return a non-zero code.
                Console.Error.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
                return 1;
            });

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

        // ---- Last-resort safety net ----
        // If Spectre itself blows up (e.g. while constructing the DI graph) we still
        // want a friendly error and a log file. Everything inside the try/catch
        // is allowed to fail — outside of it, the process is about to die.
        try
        {
            return await app.RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // We don't have a built container at this point if the failure happened
            // during services.AddSingleton registrations, so build a minimal one
            // just for the handler.
            using var fallbackScope = BuildFallbackServices().BuildServiceProvider();
            var handler = fallbackScope.GetRequiredService<PcpmExceptionHandler>();
            return handler.HandleAsync(ex, args, currentCommand: KnownCommands.ExtractCommandName(args));
        }
    }

    /// <summary>
    /// Build a minimal <see cref="IServiceCollection"/> with just the dependencies
    /// <see cref="PcpmExceptionHandler"/> needs. Used by the last-resort <c>try/catch</c>
    /// when the main DI graph failed to build.
    /// </summary>
    private static IServiceCollection BuildFallbackServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSimpleConsole(opts => opts.SingleLine = true);
            b.SetMinimumLevel(LogLevel.Warning);
        });

        // Minimal config so the log writer can resolve pcpm:storePath (or its default).
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("pcpm.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "PCPM_")
            .Build();
        services.AddSingleton<IConfiguration>(config);

        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        services.AddSingleton<IExceptionLogWriter>(sp =>
            ExceptionLogWriter.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<PcpmExceptionHandler>(sp => new PcpmExceptionHandler(
            sp.GetRequiredService<IAnsiConsole>(),
            sp.GetRequiredService<IExceptionLogWriter>(),
            sp.GetRequiredService<ILogger<PcpmExceptionHandler>>(),
            ApplicationName,
            ApplicationVersion,
            storeRootAccessor: () =>
            {
                var fromConfig = sp.GetRequiredService<IConfiguration>()["pcpm:storePath"];
                return string.IsNullOrWhiteSpace(fromConfig) ? PackageStore.DefaultStorePath() : fromConfig;
            }));
        return services;
    }

    /// <summary>
    /// Decide whether the pre-flight "command not recognised" check should run. We skip
    /// it when the user passed only flags (e.g. <c>pcpm --help</c>), an empty arg vector,
    /// or no candidate at all — those should fall through to Spectre so the user gets
    /// the proper help text.
    /// </summary>
    private static bool ShouldPreFlightCheck(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return false;
        return KnownCommands.ExtractCommandName(args) is not null;
    }

    /// <summary>
    /// Build a one-shot <see cref="IServiceProvider"/> to render the "command not
    /// recognised" message. We intentionally reuse the same code path as the last-resort
    /// handler so the wording and the log file match.
    /// </summary>
    private static async Task<int> HandleUnknownCommandAsync(string[] args, string attempted)
    {
        using var sp = BuildFallbackServices().BuildServiceProvider();
        var handler = sp.GetRequiredService<PcpmExceptionHandler>();
        return await Task.FromResult(handler.HandleUnknownCommand(attempted, args)).ConfigureAwait(false);
    }
}
