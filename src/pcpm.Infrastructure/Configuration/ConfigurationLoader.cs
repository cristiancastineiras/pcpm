using System.Text.Json;
using System.Text.Json.Serialization;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Configuration;

/// <summary>
/// Loads and persists <see cref="PcpmConfig"/> from <c>pcpm.json</c> at the workspace root.
/// Uses source-generated JSON for AOT-friendly deserialisation (no reflection at runtime).
/// </summary>
public sealed class ConfigurationLoader
{
    private const string ConfigFileName = "pcpm.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IFileSystem _fs;

    public ConfigurationLoader(IFileSystem fs) => _fs = fs;

    public async Task<PcpmConfig> LoadOrDefaultAsync(string workspaceRoot, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, ConfigFileName);
        if (!_fs.FileExists(path)) return new PcpmConfig();
        var text = await _fs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PcpmConfig>(text, Options) ?? new PcpmConfig();
    }

    public async Task SaveAsync(string workspaceRoot, PcpmConfig config, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, ConfigFileName);
        var text = JsonSerializer.Serialize(config, Options);
        await _fs.AtomicReplaceAsync(path, text, ct).ConfigureAwait(false);
    }
}
