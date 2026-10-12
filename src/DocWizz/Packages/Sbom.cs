using System.Text.Json;
using System.Text.RegularExpressions;

// Manifest-derived inventory only: no restore, network, lockfile resolution or inferred transitives.
// One inventory feeds both the CycloneDX export and the packages view, so the two cannot disagree.
static class Sbom
{
    // A package as declared at one version (or expression); `Projects` are the projects declaring it so.
    public sealed record Library(string Package, string Name, string Ecosystem, string? Declared, bool Exact, string? Purl,
        string BomRef, List<string> Projects);

    public sealed record Inventory(List<Node> Projects, List<Library> Libraries, List<(string From, string To)> References);

    // Projects, the declared packages per version, and project → project references, from the Projects scan
    // (or a code model that contains it). Ordered, so everything built from it is deterministic.
    public static Inventory Collect(IEnumerable<Node> nodes, IEnumerable<Edge> edges)
    {
        var projects = nodes.Where(n => n.Kind == "project").OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var packages = nodes.Where(n => n.Kind == "package").GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var refs = edges.Where(e => e.Kind == "depends-on" && projectIds.Contains(e.From) && packages.ContainsKey(e.To))
            .Select(e => (e.From, e.To, Version: e.Label)).Distinct().ToList();
        var libraries = refs.GroupBy(r => (r.To, r.Version))
            .OrderBy(g => g.Key.To, StringComparer.Ordinal).ThenBy(g => g.Key.Version, StringComparer.Ordinal)
            .Select(g =>
            {
                var package = packages[g.Key.To];
                var ecosystem = package.Tags?.FirstOrDefault() ?? "unknown";
                var exact = Exact(g.Key.Version);
                return new Library(g.Key.To, package.Name, ecosystem, g.Key.Version, exact,
                    ecosystem is "nuget" or "npm" or "maven" ? Purl(ecosystem, package.Name, exact ? g.Key.Version : null) : null,
                    Ref(g.Key.To, g.Key.Version ?? "unresolved"),
                    g.Select(r => r.From).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
            }).ToList();
        var references = edges.Where(e => e.Kind == "references" && projectIds.Contains(e.From) && projectIds.Contains(e.To))
            .Select(e => (e.From, e.To)).Distinct().ToList();
        return new Inventory(projects, libraries, references);
    }

    // CycloneDX 1.6 JSON for the projects and direct declared packages in the given manifests, byte-identical for identical inputs.
    public static string Export(string root, IEnumerable<string> files)
    {
        var (nodes, edges) = Projects.Scan(root, files.Where(f =>
            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f) is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts"));
        var inventory = Collect(nodes, edges);
        var components = new List<object>();
        foreach (var project in inventory.Projects)
            components.Add(new Dictionary<string, object?>
            {
                ["type"] = "application", ["name"] = project.Name, ["bom-ref"] = Ref(project.Id),
                ["properties"] = new[] { new { name = "docwizz:manifest", value = project.File } }
            });
        foreach (var item in inventory.Libraries)
        {
            // Dictionary entries ignore JsonIgnoreCondition, so an unresolved version is left out rather than written as null.
            var library = new Dictionary<string, object?> { ["type"] = "library", ["name"] = item.Name };
            if (item.Exact) library["version"] = item.Declared;
            if (item.Purl is not null) library["purl"] = item.Purl;
            library["bom-ref"] = item.BomRef;
            library["properties"] = new[]
            {
                new { name = "docwizz:ecosystem", value = item.Ecosystem },
                new { name = "docwizz:declaredVersion", value = item.Declared ?? "(unspecified)" },
                new { name = "docwizz:versionStatus", value = item.Exact ? "literal (not resolved)" : "unresolved" }
            };
            components.Add(library);
        }
        var dependencies = inventory.Projects.Select(p => new
        {
            @ref = Ref(p.Id),
            dependsOn = inventory.Libraries.Where(l => l.Projects.Contains(p.Id)).Select(l => l.BomRef)
                .Concat(inventory.References.Where(r => r.From == p.Id).Select(r => Ref(r.To)))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray()
        }).ToArray();
        var subject = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var result = new
        {
            bomFormat = "CycloneDX", specVersion = "1.6", version = 1,
            metadata = new
            {
                tools = new { components = new[] { new { type = "application", name = "docwizz" } } },
                component = new Dictionary<string, object?>
                {
                    ["type"] = "application", ["name"] = subject is { Length: > 0 } ? subject : "root", ["bom-ref"] = "root"
                },
                properties = new[]
                {
                    new { name = "docwizz:scope", value = "manifest-declared direct dependencies only" },
                    new { name = "docwizz:resolution", value = "not resolved or installed; transitive dependencies not included" }
                }
            },
            components, dependencies
        };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }) + "\n";
    }

    static string Ref(string id, string? version = null) => version is null
        ? id : id + ":" + Uri.EscapeDataString(version);

    // Only a plain literal (1.2.3, 1.0.0-beta.1, 5.3.20.RELEASE) counts as a version; ranges, wildcards (1.x),
    // properties, tags and git/file/alias specs stay declared expressions.
    static bool Exact(string? version) => version is not null &&
        Regex.IsMatch(version, @"^\d+(\.\d+)*([.\-+][0-9A-Za-z][0-9A-Za-z.\-+]*)?$") &&
        !version.Split('.').Any(s => s is "x" or "X");

    // Package URL (https://github.com/package-url/purl-spec), versioned only when the version is a literal.
    static string Purl(string ecosystem, string name, string? version)
    {
        var path = ecosystem switch
        {
            "maven" when name.Split(':') is [var group, var artifact] =>
                Uri.EscapeDataString(group) + "/" + Uri.EscapeDataString(artifact),
            "npm" when name.StartsWith('@') && name.IndexOf('/') is > 1 and var slash =>
                "%40" + Uri.EscapeDataString(name[1..slash]) + "/" + Uri.EscapeDataString(name[(slash + 1)..]),
            _ => Uri.EscapeDataString(name)
        };
        return $"pkg:{ecosystem}/{path}" + (version is null ? "" : "@" + Uri.EscapeDataString(version));
    }
}
