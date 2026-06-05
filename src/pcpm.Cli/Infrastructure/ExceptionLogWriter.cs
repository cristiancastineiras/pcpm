using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using pcpm.Core.Abstractions;
using pcpm.Infrastructure.Store;

namespace pcpm.Cli.Infrastructure;

/// <summary>
/// Default <see cref="IExceptionLogWriter"/> implementation. Writes a single plain-text
/// file per invocation to <c>&lt;storeRoot&gt;/logs/pcpm-YYYYMMDD-HHmmss-fff.log</c>.
///
/// <para>The file is overwritten in place rather than appended to, because pcpm is a
/// short-lived CLI process — we only ever write one log per run. The format is deliberately
/// grep-friendly: one field per line, no JSON, no XML, so the user can <c>cat</c> / <c>tail</c>
/// / <c>grep</c> it without tooling.</para>
///
/// <para>If the store directory cannot be created or written to (locked, read-only, no
/// permission, …) the writer falls back to <c>%TEMP%/pcpm-logs/</c> and reports the
/// fallback via <see cref="ExceptionLogWriteResult.UsedFallback"/>. It never throws —
/// the caller is always a top-level exception handler that must be able to render its
/// own error message after the writer has had its turn.</para>
/// </summary>
public sealed class ExceptionLogWriter : IExceptionLogWriter
{
    /// <summary>Subdirectory of the store root where log files are written.</summary>
    public const string LogsSubdirectory = "logs";

    private readonly IConfiguration _configuration;
    private readonly Func<string> _storePathAccessor;

    public ExceptionLogWriter(IConfiguration configuration, Func<string> storePathAccessor)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _storePathAccessor = storePathAccessor ?? throw new ArgumentNullException(nameof(storePathAccessor));
    }

    /// <summary>
    /// Convenience factory: resolves the store root from the same configuration key the
    /// package store uses (<c>pcpm:storePath</c>), falling back to
    /// <see cref="PackageStore.DefaultStorePath"/>.
    /// </summary>
    public static ExceptionLogWriter FromConfiguration(IConfiguration configuration) =>
        new(configuration, () =>
        {
            var fromConfig = configuration["pcpm:storePath"];
            return string.IsNullOrWhiteSpace(fromConfig) ? PackageStore.DefaultStorePath() : fromConfig;
        });

    public ExceptionLogWriteResult Write(Exception exception, ExceptionLogContext context, CancellationToken cancellationToken = default)
    {
        // We never throw out of this method. The whole point is that we're already
        // inside the error path, and throwing here would mask the original error.
        try
        {
            var (primaryDir, primaryError) = TryPrepareDirectory(_storePathAccessor());
            string chosenDir;
            string? fallbackReason = null;

            if (primaryDir is not null)
            {
                chosenDir = primaryDir;
            }
            else
            {
                fallbackReason = primaryError;
                var (fallbackDir, fallbackError) = TryPrepareDirectory(GetTempFallback());
                if (fallbackDir is null)
                {
                    // Last resort: return a synthetic result so the caller still has
                    // something to report. We still never throw.
                    return new ExceptionLogWriteResult(
                        LogFilePath: string.Empty,
                        UsedFallback: true,
                        FallbackReason: $"Could not create the store log directory ({primaryError}) and the temp fallback ({fallbackError}).");
                }

                chosenDir = fallbackDir;
            }

            var fileName = BuildLogFileName(DateTimeOffset.UtcNow);
            var fullPath = Path.Combine(chosenDir, fileName);
            var contents = BuildLogContents(exception, context);

            // Write atomically: write to a sibling .tmp first, then move. If the process
            // is killed mid-write the store is never left with a half-written log.
            var tempPath = fullPath + ".tmp";
            File.WriteAllText(tempPath, contents, Encoding.UTF8);
            File.Move(tempPath, fullPath, overwrite: true);

            return new ExceptionLogWriteResult(fullPath, UsedFallback: fallbackReason is not null, FallbackReason: fallbackReason);
        }
        catch (Exception ex)
        {
            // The compiler suggests a discard warning for the inner exception details;
            // surface them via the result so the caller can mention them in their own
            // error message ("log write also failed: …").
            return new ExceptionLogWriteResult(
                LogFilePath: string.Empty,
                UsedFallback: true,
                FallbackReason: $"Log writer failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Builds <c>pcpm-YYYYMMDD-HHmmss-fff.log</c> from a UTC instant.</summary>
    public static string BuildLogFileName(DateTimeOffset utcNow) =>
        $"pcpm-{utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.log";

    private static (string? Directory, string? Error) TryPrepareDirectory(string candidate)
    {
        try
        {
            var logsDir = Path.Combine(candidate, LogsSubdirectory);
            Directory.CreateDirectory(logsDir);
            return (logsDir, null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetTempFallback()
    {
        var tempRoot = Path.GetTempPath();
        return Path.Combine(tempRoot, "pcpm-logs");
    }

    private static string BuildLogContents(Exception exception, ExceptionLogContext context)
    {
        var sb = new StringBuilder(capacity: 4096);
        var utcNow = DateTimeOffset.UtcNow;

        // ---- Header (grep-friendly, one field per line) ----
        sb.AppendLine("# pcpm exception log");
        sb.Append("timestamp_utc: ").AppendLine(utcNow.ToString("o", CultureInfo.InvariantCulture));
        sb.Append("application:   ").Append(context.ApplicationName).Append(' ').AppendLine(context.ApplicationVersion);
        sb.Append("os:            ").AppendLine(GetOsDescription());
        sb.Append("runtime:       ").AppendLine(GetRuntimeDescription());
        sb.Append("cwd:           ").AppendLine(context.WorkingDirectory);
        sb.Append("store_root:    ").AppendLine(context.StoreRoot);
        sb.Append("command_line:  ").AppendLine(string.IsNullOrEmpty(context.CommandLine) ? "(empty)" : context.CommandLine);
        sb.Append("command:       ").AppendLine(string.IsNullOrEmpty(context.CurrentCommand) ? "(none)" : context.CurrentCommand);
        sb.AppendLine();

        // ---- Exception chain (outermost first) ----
        var depth = 0;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            sb.Append("--- exception #").Append(depth).Append(" (").Append(depth == 0 ? "outer" : "inner").AppendLine(") ---");
            sb.Append("type:        ").AppendLine(current.GetType().FullName ?? current.GetType().Name);
            sb.Append("message:     ").AppendLine(string.IsNullOrEmpty(current.Message) ? "(no message)" : current.Message);
            sb.Append("source:      ").AppendLine(string.IsNullOrEmpty(current.Source) ? "(none)" : current.Source);
            sb.Append("hresult:     ").AppendLine("0x" + current.HResult.ToString("X8", CultureInfo.InvariantCulture));

            if (current.Data is { Count: > 0 } data)
            {
                sb.AppendLine("data:");
                foreach (var key in data.Keys)
                {
                    var value = data[key] ?? "(null)";
                    sb.Append("  ").Append(key).Append(" = ").AppendLine(value.ToString());
                }
            }

            sb.Append("stack_trace:").AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(current.StackTrace) ? "  (no stack trace available)" : current.StackTrace);
            sb.AppendLine();
            depth++;
        }

        return sb.ToString();
    }

    private static string GetOsDescription() =>
        $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version} ({RuntimeInformation.OSDescription})";

    private static string GetRuntimeDescription() =>
        $".NET {Environment.Version} ({RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier})";

    /// <summary>
    /// Returns the running assembly's <see cref="AssemblyInformationalVersionAttribute"/>
    /// (set by Directory.Build.props / csproj <c>Version</c>), falling back to the
    /// assembly version, then <c>"unknown"</c>.
    /// </summary>
    public static string GetApplicationVersion()
    {
        var asm = typeof(ExceptionLogWriter).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info;
        var v = asm.GetName().Version;
        return v is null ? "unknown" : v.ToString();
    }
}
