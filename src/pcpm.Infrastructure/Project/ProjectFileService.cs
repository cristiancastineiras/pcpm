using System.Xml.Linq;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Project;

/// <summary>
/// Reads and writes &lt;PackageReference&gt; entries in a .csproj file. In CPM mode the project
/// declares the id only (no Version attribute — versions live in <c>Directory.Packages.props</c>).
/// We never touch the project file outside the &lt;ItemGroup&gt; that contains &lt;PackageReference&gt;.
/// </summary>
public sealed class ProjectFileService : IProjectFileService
{
    private const string ProjectElement = "Project";
    private const string PropertyGroupElement = "PropertyGroup";
    private const string ItemGroupElement = "ItemGroup";
    private const string PackageReferenceElement = "PackageReference";
    private const string TargetFrameworkElement = "TargetFramework";
    private const string TargetFrameworksElement = "TargetFrameworks";
    private const string AssemblyNameElement = "AssemblyName";

    private static readonly XNamespace MsbuildNs = "http://schemas.microsoft.com/developer/msbuild/2003";

    private readonly IFileSystem _fs;

    public ProjectFileService(IFileSystem fs) => _fs = fs;

    public async Task<ProjectInfo> ReadAsync(string projectPath, CancellationToken ct)
    {
        var text = await _fs.ReadAllTextAsync(projectPath, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"Project file '{projectPath}' has no root.");

        var name = ReadAssemblyName(root) ?? Path.GetFileNameWithoutExtension(projectPath);
        var tfms = ReadTargetFrameworks(root);
        var refs = ReadPackageReferences(root);

        return new ProjectInfo
        {
            ProjectPath = projectPath,
            Name = name,
            TargetFrameworks = tfms,
            PackageReferences = refs,
        };
    }

    public async Task AddPackageReferenceAsync(string projectPath, PackageId id, CancellationToken ct)
    {
        var text = await _fs.ReadAllTextAsync(projectPath, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"Project file '{projectPath}' has no root.");

        if (HasPackageReference(root, id.Value)) return; // idempotent

        var ig = EnsurePackageReferenceItemGroup(root);
        ig.Add(new XElement(ig.Name.Namespace + PackageReferenceElement,
            new XAttribute("Include", id.Value)));

        await _fs.AtomicReplaceAsync(projectPath, Serialize(doc), ct).ConfigureAwait(false);
    }

    public async Task RemovePackageReferenceAsync(string projectPath, PackageId id, CancellationToken ct)
    {
        var text = await _fs.ReadAllTextAsync(projectPath, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"Project file '{projectPath}' has no root.");

        var removed = root.Elements()
            .Where(e => e.Name.LocalName == ItemGroupElement)
            .SelectMany(ig => ig.Elements())
            .Where(e => e.Name.LocalName == PackageReferenceElement
                && string.Equals((string?)e.Attribute("Include"), id.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (removed.Count == 0) return;

        removed.Remove();
        await _fs.AtomicReplaceAsync(projectPath, Serialize(doc), ct).ConfigureAwait(false);
    }

    // -- helpers --

    private static string? ReadAssemblyName(XElement root) =>
        root.Elements()
            .Where(e => e.Name.LocalName == PropertyGroupElement)
            .SelectMany(pg => pg.Elements())
            .FirstOrDefault(e => e.Name.LocalName == AssemblyNameElement)?.Value?.Trim();

    private static IReadOnlyList<string> ReadTargetFrameworks(XElement root)
    {
        // Prefer single-TFM TargetFramework; otherwise parse the semicolon-separated TargetFrameworks.
        var single = root.Elements()
            .Where(e => e.Name.LocalName == PropertyGroupElement)
            .SelectMany(pg => pg.Elements())
            .FirstOrDefault(e => e.Name.LocalName == TargetFrameworkElement)?.Value?.Trim();
        if (!string.IsNullOrEmpty(single)) return new[] { single };

        var multi = root.Elements()
            .Where(e => e.Name.LocalName == PropertyGroupElement)
            .SelectMany(pg => pg.Elements())
            .FirstOrDefault(e => e.Name.LocalName == TargetFrameworksElement)?.Value?.Trim();
        if (string.IsNullOrEmpty(multi)) return Array.Empty<string>();

        return multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<PackageId> ReadPackageReferences(XElement root) =>
        root.Elements()
            .Where(e => e.Name.LocalName == ItemGroupElement)
            .SelectMany(ig => ig.Elements())
            .Where(e => e.Name.LocalName == PackageReferenceElement)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => PackageId.Create(s!))
            .ToList();

    private static bool HasPackageReference(XElement root, string id) =>
        root.Elements()
            .Where(e => e.Name.LocalName == ItemGroupElement)
            .SelectMany(ig => ig.Elements())
            .Any(e => e.Name.LocalName == PackageReferenceElement
                && string.Equals((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase));

    private static XElement EnsurePackageReferenceItemGroup(XElement root)
    {
        var ig = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName == ItemGroupElement
                && e.Elements().Any(c => c.Name.LocalName == PackageReferenceElement));
        if (ig is null)
        {
            ig = new XElement(root.Name.Namespace + ItemGroupElement);
            root.Add(ig);
        }
        return ig;
    }

    private static string Serialize(XDocument doc) =>
        doc.Declaration is null
            ? doc.ToString()
            : doc.Declaration + Environment.NewLine + doc.Root!.ToString();
}
