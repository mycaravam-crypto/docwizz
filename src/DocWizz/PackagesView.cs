using System.Text;

// The packages view: the inventory behind `docwizz sbom`, for readers. Packages declared at more than one version come first.
partial class Generator
{
    Sbom.Inventory? inventory;
    Sbom.Inventory Inventory => inventory ??= Sbom.Collect(model.Nodes, model.Edges);

    string PackagesView()
    {
        var projects = Inventory.Projects.ToDictionary(p => p.Id);
        string Users(Sbom.Library l) => string.Join(", ", l.Projects.Select(p => SourceLink(projects[p].File, 0, projects[p].Name, sub: "views")));
        string Declared(Sbom.Library l) => l.Declared is null ? "_unspecified_" : $"`{Esc(l.Declared)}`" + (l.Exact ? "" : " _(unresolved)_");
        // One line per declared version, with the projects declaring it, so versions and projects stay paired.
        string Versions(IEnumerable<Sbom.Library> g) => string.Join("<br>", g.Select(l => $"{Declared(l)} in {Users(l)}"));
        var byPackage = Inventory.Libraries.GroupBy(l => l.Package)
            .OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var conflicts = byPackage.Where(g => g.Count() > 1).ToList();

        var sb = new StringBuilder("# Packages\n\n");
        sb.AppendLine("Direct dependencies declared in NuGet, npm, Maven and Gradle manifests: the inventory `docwizz sbom` exports. " +
            "Nothing is restored or resolved and transitive dependencies are not included; ranges and properties stay as declared.\n");
        sb.AppendLine($"{byPackage.Count} packages in {Inventory.Projects.Count} projects; {conflicts.Count} declared at more than one version.\n");
        if (conflicts.Count > 0)
        {
            sb.AppendLine("## Declared at more than one version\n");
            sb.AppendLine("| Package | Ecosystem | Versions |\n|---|---|---|");
            foreach (var g in conflicts)
                sb.AppendLine($"| `{Esc(g.First().Name)}` | {g.First().Ecosystem} | {Versions(g)} |");
            sb.AppendLine();
        }
        sb.AppendLine("## All packages\n");
        sb.AppendLine("| Package | Ecosystem | Declared | Package URL |\n|---|---|---|---|");
        foreach (var g in byPackage)
            sb.AppendLine($"| `{Esc(g.First().Name)}`{(g.Count() > 1 ? " ⚠" : "")} | {g.First().Ecosystem} | {Versions(g)} | " +
                $"{(g.First().Purl is { } purl ? $"`{Esc(purl.Split('@')[0])}`" : "—")} |");
        return sb.ToString();
    }
}
