using System.Xml.Linq;
using pcpm.Core.Abstractions;
using pcpm.Core.Models;

namespace pcpm.Infrastructure.Cpm;

/// <summary>
/// Reads and writes <c>Directory.Packages.props</c>. The XML shape we produce and consume:
///
/// <code>
/// &lt;Project&gt;
///   &lt;PropertyGroup&gt;
///     &lt;ManagePackageVersionsCentrally&gt;true&lt;/ManagePackageVersionsCentrally&gt;
///   &lt;/PropertyGroup&gt;
///   &lt;ItemGroup&gt;
///     &lt;PackageVersion Include="X" Version="1.0.0" /&gt;
///     ...
///   &lt;/ItemGroup&gt;
/// &lt;/Project&gt;
/// </code>
///
/// We never touch the file outside the &lt;PropertyGroup&gt; for the flag and the &lt;ItemGroup&gt;
/// for &lt;PackageVersion&gt; entries. Any user-added &lt;GlobalPackageReference&gt; items are preserved.
/// </summary>
public sealed class CpmFileService : ICpmFileService
{
    public string CpmFileName => "Directory.Packages.props";

    private const string ProjectElement = "Project";
    private const string PropertyGroupElement = "PropertyGroup";
    private const string ItemGroupElement = "ItemGroup";
    private const string ManageCpmElement = "ManagePackageVersionsCentrally";
    private const string PackageVersionElement = "PackageVersion";
    private const string GlobalPackageReferenceElement = "GlobalPackageReference";

    private readonly IFileSystem _fs;

    public CpmFileService(IFileSystem fs) => _fs = fs;

    public async Task<CentralPackageManagement> ReadAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, CpmFileName);
        if (!_fs.FileExists(path)) return Empty();

        var doc = await LoadAsync(path, ct).ConfigureAwait(false);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != ProjectElement) return Empty();

        var isEnabled = ReadManageCpm(root);
        var packageVersions = ReadPackageVersions(root);
        var globals = ReadGlobalPackageReferences(root);

        return new CentralPackageManagement
        {
            IsEnabled = isEnabled,
            PackageVersions = packageVersions,
            GlobalPackageReferences = globals,
        };
    }

    public async Task SetPackageVersionAsync(string directory, PackageId id, PackageVersion version, CancellationToken ct)
    {
        var path = Path.Combine(directory, CpmFileName);
        var doc = _fs.FileExists(path) ? await LoadAsync(path, ct).ConfigureAwait(false) : NewDocument();

        var versions = ReadPackageVersions(doc.Root!);
        versions[id] = version;
        WritePackageVersions(doc.Root!, versions);

        if (!ReadManageCpm(doc.Root!)) WriteManageCpm(doc.Root!, true);

        await _fs.AtomicReplaceAsync(path, Serialize(doc), ct).ConfigureAwait(false);
    }

    public async Task RemovePackageVersionAsync(string directory, PackageId id, CancellationToken ct)
    {
        var path = Path.Combine(directory, CpmFileName);
        if (!_fs.FileExists(path)) return;

        var doc = await LoadAsync(path, ct).ConfigureAwait(false);
        var versions = ReadPackageVersions(doc.Root!);
        if (!versions.Remove(id)) return; // nothing to do
        WritePackageVersions(doc.Root!, versions);

        await _fs.AtomicReplaceAsync(path, Serialize(doc), ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string directory, CentralPackageManagement cpm, CancellationToken ct)
    {
        var path = Path.Combine(directory, CpmFileName);
        var doc = NewDocument();
        WriteManageCpm(doc.Root!, cpm.IsEnabled);
        WritePackageVersions(doc.Root!, cpm.PackageVersions);
        WriteGlobalPackageReferences(doc.Root!, cpm.GlobalPackageReferences);
        await _fs.AtomicReplaceAsync(path, Serialize(doc), ct).ConfigureAwait(false);
    }

    // -- helpers --

    private static CentralPackageManagement Empty() => new()
    {
        IsEnabled = false,
        PackageVersions = new Dictionary<PackageId, PackageVersion>(),
    };

    private static XDocument NewDocument()
    {
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        return new XDocument(
            new XElement(ns + ProjectElement,
                new XElement(ns + PropertyGroupElement,
                    new XElement(ns + ManageCpmElement, "true"))));
    }

    private async Task<XDocument> LoadAsync(string path, CancellationToken ct)
    {
        var text = await _fs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
    }

    private static string Serialize(XDocument doc) =>
        doc.Declaration is null
            ? doc.ToString()
            : doc.Declaration + Environment.NewLine + doc.Root!.ToString();

    private static bool ReadManageCpm(XElement root) =>
        root.Elements().Where(e => e.Name.LocalName == PropertyGroupElement)
            .SelectMany(pg => pg.Elements())
            .FirstOrDefault(e => e.Name.LocalName == ManageCpmElement) is { } el
        && string.Equals(el.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static void WriteManageCpm(XElement root, bool value)
    {
        var pg = EnsurePropertyGroup(root);
        var el = pg.Elements().FirstOrDefault(e => e.Name.LocalName == ManageCpmElement);
        if (el is null) pg.Add(new XElement(pg.Name.Namespace + ManageCpmElement, value ? "true" : "false"));
        else el.Value = value ? "true" : "false";
    }

    private static XElement EnsurePropertyGroup(XElement root)
    {
        var pg = root.Elements().FirstOrDefault(e => e.Name.LocalName == PropertyGroupElement);
        if (pg is null)
        {
            pg = new XElement(root.Name.Namespace + PropertyGroupElement);
            root.AddFirst(pg);
        }
        return pg;
    }

    private static XElement EnsurePackageVersionItemGroup(XElement root)
    {
        // Prefer the first existing ItemGroup that already has a PackageVersion; else create one.
        var ig = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName == ItemGroupElement
                && e.Elements().Any(c => c.Name.LocalName == PackageVersionElement));
        if (ig is null)
        {
            ig = new XElement(root.Name.Namespace + ItemGroupElement);
            root.Add(ig);
        }
        return ig;
    }

    private static Dictionary<PackageId, PackageVersion> ReadPackageVersions(XElement root)
    {
        var result = new Dictionary<PackageId, PackageVersion>();
        foreach (var el in root.Elements()
                     .Where(e => e.Name.LocalName == ItemGroupElement)
                     .SelectMany(ig => ig.Elements())
                     .Where(e => e.Name.LocalName == PackageVersionElement))
        {
            var include = (string?)el.Attribute("Include");
            var version = (string?)el.Attribute("Version");
            if (string.IsNullOrEmpty(include) || string.IsNullOrEmpty(version)) continue;
            if (!PackageId.TryCreate(include, out var id)) continue;
            if (!PackageVersion.TryCreate(version, out var v)) continue;
            result[id] = v;
        }
        return result;
    }

    private static void WritePackageVersions(XElement root, IReadOnlyDictionary<PackageId, PackageVersion> versions)
    {
        var ig = EnsurePackageVersionItemGroup(root);
        // Remove existing entries to keep the file deterministic (sorted below).
        ig.Elements().Where(e => e.Name.LocalName == PackageVersionElement).Remove();
        foreach (var (id, v) in versions.OrderBy(kv => kv.Key.Value, StringComparer.OrdinalIgnoreCase))
        {
            ig.Add(new XElement(ig.Name.Namespace + PackageVersionElement,
                new XAttribute("Include", id.Value),
                new XAttribute("Version", v.ToString())));
        }
    }

    private static IReadOnlyList<PackageId> ReadGlobalPackageReferences(XElement root) =>
        root.Elements()
            .Where(e => e.Name.LocalName == ItemGroupElement)
            .SelectMany(ig => ig.Elements())
            .Where(e => e.Name.LocalName == GlobalPackageReferenceElement)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => PackageId.Create(s!))
            .ToList();

    private static void WriteGlobalPackageReferences(XElement root, IReadOnlyList<PackageId> refs)
    {
        if (refs.Count == 0) return;
        var ig = new XElement(root.Name.Namespace + ItemGroupElement);
        foreach (var id in refs)
        {
            ig.Add(new XElement(ig.Name.Namespace + GlobalPackageReferenceElement,
                new XAttribute("Include", id.Value)));
        }
        root.Add(ig);
    }
}
