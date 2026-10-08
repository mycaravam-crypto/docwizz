using System.Text.Json;
using System.Text.RegularExpressions;

// Manifest-derived inventory only: no restore, network, lockfile resolution or inferred transitives.
static class Sbom
{
    public static string Export(string root, IEnumerable<string> files)
    {
        var (nodes, edges) = Projects.Scan(root, files.Where(f =>
            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f) is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts"));
        var projects = nodes.Where(n => n.Kind == "project").OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var packages = nodes.Where(n => n.Kind == "package").ToDictionary(n => n.Id);
        var refs = edges.Where(e => e.Kind == "depends-on" && packages.ContainsKey(e.To))
            .Select(e => (e.From, e.To, Version: e.Label))
            .Distinct().OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Version, StringComparer.Ordinal).ToList();
        var projectRefs = edges.Where(e => e.Kind == "references" && projectIds.Contains(e.From) && projectIds.Contains(e.To))
            .Select(e => (e.From, e.To)).ToList();

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
        var components = new List<object>();
        foreach (var project in projects)
            components.Add(new Dictionary<string, object?>
            {
                ["type"] = "application", ["name"] = project.Name, ["bom-ref"] = Ref(project.Id),
                ["properties"] = new[] { new { name = "docwizz:manifest", value = project.File } }
            });
        foreach (var item in refs.Select(r => (r.To, r.Version)).Distinct()
            .OrderBy(r => r.To, StringComparer.Ordinal).ThenBy(r => r.Version, StringComparer.Ordinal))
        {
            var package = packages[item.To];
            var ecosystem = package.Tags?.FirstOrDefault() ?? "unknown";
            var exact = Exact(item.Version);
            // Dictionary entries ignore JsonIgnoreCondition, so an unresolved version is left out rather than written as null.
            var library = new Dictionary<string, object?> { ["type"] = "library", ["name"] = package.Name };
            if (exact) library["version"] = item.Version;
            if (ecosystem is "nuget" or "npm" or "maven") library["purl"] = Purl(ecosystem, package.Name, exact ? item.Version : null);
            library["bom-ref"] = Ref(item.To, item.Version ?? "unresolved");
            library["properties"] = new[]
            {
                new { name = "docwizz:ecosystem", value = ecosystem },
                new { name = "docwizz:declaredVersion", value = item.Version ?? "(unspecified)" },
                new { name = "docwizz:versionStatus", value = exact ? "literal (not resolved)" : "unresolved" }
            };
            components.Add(library);
        }
        var dependencies = projects.Select(p => new
        {
            @ref = Ref(p.Id),
            dependsOn = refs.Where(r => r.From == p.Id).Select(r => Ref(r.To, r.Version ?? "unresolved"))
                .Concat(projectRefs.Where(r => r.From == p.Id).Select(r => Ref(r.To)))
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
}
