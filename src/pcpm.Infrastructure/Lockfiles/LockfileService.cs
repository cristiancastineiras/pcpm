using System.Text.Json;
using System.Text.Json.Serialization;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Lockfiles;

/// <summary>
/// Persists <see cref="Lockfile"/> to <c>pcpm.lock</c> at the workspace root.
/// Uses a stable JSON shape (camelCase, indented) so the file is diff-friendly in PRs.
/// </summary>
public sealed class LockfileService : ILockfileService
{
    public const string LockfileFileName = "pcpm.lock";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IFileSystem _fs;

    public LockfileService(IFileSystem fs) => _fs = fs;

    public async Task<Lockfile> ReadOrEmptyAsync(string workspaceRoot, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, LockfileFileName);
        if (!_fs.FileExists(path))
        {
            return new Lockfile
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                PcpmVersion = "0.1.0",
                Packages = Array.Empty<LockedPackage>(),
                Projects = Array.Empty<LockedProject>(),
            };
        }

        var text = await _fs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<LockfileDto>(text, Options)
            ?? throw new InvalidOperationException("pcpm.lock is empty or unparseable.");

        return dto.ToDomain();
    }

    public async Task WriteAsync(string workspaceRoot, Lockfile lockfile, CancellationToken ct)
    {
        var path = Path.Combine(workspaceRoot, LockfileFileName);
        var dto = LockfileDto.FromDomain(lockfile);
        var text = JsonSerializer.Serialize(dto, Options);
        await _fs.AtomicReplaceAsync(path, text, ct).ConfigureAwait(false);
    }

    // -- DTO shim so the wire format is decoupled from the domain records' ToString() output --

    private sealed record LockfileDto(
        int LockfileVersion,
        DateTimeOffset GeneratedAt,
        string PcpmVersion,
        IReadOnlyList<LockedPackageDto> Packages,
        IReadOnlyList<LockedProjectDto> Projects)
    {
        public static LockfileDto FromDomain(Lockfile l) => new(
            l.LockfileVersion,
            l.GeneratedAt,
            l.PcpmVersion,
            l.Packages.Select(LockedPackageDto.FromDomain).ToList(),
            l.Projects.Select(LockedProjectDto.FromDomain).ToList());

        public Lockfile ToDomain() => new()
        {
            LockfileVersion = LockfileVersion,
            GeneratedAt = GeneratedAt,
            PcpmVersion = PcpmVersion,
            Packages = Packages.Select(p => p.ToDomain()).ToList(),
            Projects = Projects.Select(p => p.ToDomain()).ToList(),
        };
    }

    private sealed record LockedPackageDto(
        string Id,
        string Version,
        string ContentHash,
        IReadOnlyList<LockedDependencyDto> Dependencies)
    {
        public static LockedPackageDto FromDomain(LockedPackage p) => new(
            p.Id.Value,
            p.Version.ToString(),
            p.ContentHash,
            p.Dependencies.Select(LockedDependencyDto.FromDomain).ToList());

        public LockedPackage ToDomain() => new(
            PackageId.Create(Id),
            PackageVersion.Create(Version),
            ContentHash,
            Dependencies.Select(d => d.ToDomain()).ToList());
    }

    private sealed record LockedDependencyDto(string Id, string Version)
    {
        public static LockedDependencyDto FromDomain(LockedDependency d) =>
            new(d.Id.Value, d.Version.ToString());

        public LockedDependency ToDomain() =>
            new(PackageId.Create(Id), PackageVersion.Create(Version));
    }

    private sealed record LockedProjectDto(string ProjectPath, IReadOnlyList<LockedDependencyDto> DirectDependencies)
    {
        public static LockedProjectDto FromDomain(LockedProject p) => new(
            p.ProjectPath,
            p.DirectDependencies.Select(LockedDependencyDto.FromDomain).ToList());

        public LockedProject ToDomain() => new(
            ProjectPath,
            DirectDependencies.Select(d => d.ToDomain()).ToList());
    }
}
