using System.Text.Json;

// Manifest-derived inventory only: no restore, network, lockfile resolution or inferred transitives.
static class Sbom
{
    public static string Export(string root, IEnumerable<string> files)
    {
        var (nodes, edges) = Projects.Scan(root, files.Where(f =>
            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f) is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts"));
        var projects = nodes.Where(n => n.Kind == "project").OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var packages = nodes.Where(n => n.Kind == "package").ToDictionary(n => n.Id);
        var refs = edges.Where(e => e.Kind == "depends-on" && packages.ContainsKey(e.To))
            .Select(e => (e.From, e.To, Version: e.Label))
            .Distinct().OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Version, StringComparer.Ordinal).ToList();

        static string Ref(string id, string? version = null) => version is null
            ? id : id + ":" + Uri.EscapeDataString(version);
        static bool Exact(string? version) => version is { Length: > 0 } &&
            !version.Any(c => "^~*<>[]() ${}".Contains(c)) && !version.StartsWith("latest", StringComparison.OrdinalIgnoreCase);
        var components = new List<object>();
        foreach (var project in projects)
            components.Add(new
            {
                type = "application", name = project.Name, bom_ref = Ref(project.Id),
                properties = new[] { new { name = "docwizz:manifest", value = project.File } }
            });
        foreach (var item in refs.Select(r => (r.To, r.Version)).Distinct()
            .OrderBy(r => r.To, StringComparer.Ordinal).ThenBy(r => r.Version, StringComparer.Ordinal))
        {
            var package = packages[item.To];
            var ecosystem = package.Tags?.FirstOrDefault() ?? "unknown";
            components.Add(new
            {
                type = "library", name = package.Name, version = Exact(item.Version) ? item.Version : null,
                bom_ref = Ref(item.To, item.Version ?? "unresolved"),
                properties = new[]
                {
                    new { name = "docwizz:ecosystem", value = ecosystem },
                    new { name = "docwizz:declaredVersion", value = item.Version ?? "(unspecified)" },
                    new { name = "docwizz:versionStatus", value = Exact(item.Version) ? "literal (not resolved)" : "unresolved" }
                }
            });
        }
        var dependencies = projects.Select(p => new
        {
            @ref = Ref(p.Id),
            dependsOn = refs.Where(r => r.From == p.Id).Select(r => Ref(r.To, r.Version ?? "unresolved"))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray()
        }).ToArray();
        var result = new
        {
            bomFormat = "CycloneDX", specVersion = "1.6", version = 1,
            metadata = new
            {
                tools = new { components = new[] { new { type = "application", name = "docwizz" } } },
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
