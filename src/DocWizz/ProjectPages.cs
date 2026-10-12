using System.Text;

// Project and solution entry points are thin navigation pages over the existing folder-based modules.
// They never change the code model or count a project twice when it belongs to multiple solutions.
partial class Generator
{
    ProjectHierarchy? hierarchy;

    string RenderSolution(ProjectHierarchy.Solution solution)
    {
        var sb = new StringBuilder($"# Solution: {solution.Path}\n\n");
        sb.AppendLine("[System overview](../index.md) / Solution\n");
        sb.AppendLine($"Manifest: \x60{solution.Path}\x60. Shared projects link to the same project page across solutions.\n");
        sb.AppendLine("## Projects\n\n| Project | Manifest | Role | Solution folder | Modules |\n|---|---|---|---|---:|");
        foreach (var (id, folder) in solution.Projects)
        {
            if (hierarchy!.ById(id) is not { } p) continue;
            sb.AppendLine($"| [{Esc(p.Name)}](../{p.Page}) | \x60{p.Path}\x60 | " +
                $"{(p.MetadataOnly ? "metadata-only" : p.Role)} | {Esc(folder)} | {p.Modules.Count} |");
        }
        if (solution.Unresolved.Count > 0)
        {
            sb.AppendLine("\n## Unresolved project entries\n");
            foreach (var missing in solution.Unresolved)
                sb.AppendLine($"- \x60{missing.Path}\x60 — {missing.Reason}");
        }
        sb.AppendLine("\n## System-wide views\n\n[Architecture](../architecture.md) · " +
            "[API](../api.md) · [Deployment](../views/deployment.md) · [Quality](../quality.md)\n");
        return sb.ToString();
    }

    string RenderProject(ProjectHierarchy.Project project)
    {
        var sb = new StringBuilder($"# Project: {project.Name}\n\n");
        sb.Append("[System overview](../index.md)");
        foreach (var link in project.Solutions)
        {
            var solution = hierarchy!.Solutions.FirstOrDefault(s => s.Page == link.SolutionPage);
            if (solution is not null) sb.Append($" / [Solution: {solution.Path}](../{solution.Page})");
        }
        sb.AppendLine(" / Project\n");
        sb.AppendLine($"Manifest: \x60{project.Path}\x60. Identity: \x60{project.Id}\x60.\n");
        if (project.MetadataOnly)
        {
            sb.AppendLine("**Metadata-only dependency.** This project is referenced by a selected project, but its source code, " +
                "members and package dependencies have not been analyzed. No documentation coverage is attributed to it.\n");
            return sb.ToString();
        }
        var node = nodes[project.Id];
        sb.AppendLine($"Role: **{project.Role}**.");
        var tfms = node.Tags?.Where(t => t.StartsWith("net", StringComparison.Ordinal)).ToList() ?? [];
        if (tfms.Count > 0) sb.AppendLine($"Target frameworks: {string.Join(", ", tfms.Select(t => "\x60" + t + "\x60"))}.");
        var sdk = node.Tags?.FirstOrDefault(t => t.StartsWith("Microsoft.NET.Sdk", StringComparison.Ordinal));
        if (sdk is not null) sb.AppendLine($"SDK: \x60{sdk}\x60.");
        if (project.Role == "test")
            sb.AppendLine("**Test evidence only.** Test symbols do not contribute to production coverage or API documentation.");
        var sourceFiles = model.Nodes.Where(n => n.Kind is not ("project" or "package" or "external" or "config")
                && hierarchy!.Owner(n.File)?.Id == project.Id)
            .Select(n => n.File).Distinct(StringComparer.Ordinal).Count();
        sb.AppendLine($"Source files represented in the code model: {sourceFiles}.");
        sb.AppendLine();

        if (project.Solutions.Count == 0) sb.AppendLine("This project is not assigned to an inventoried solution.\n");
        else
        {
            sb.AppendLine("## Solution membership\n");
            foreach (var member in project.Solutions)
            {
                var solution = hierarchy!.Solutions.FirstOrDefault(s => s.Page == member.SolutionPage);
                if (solution is not null)
                    sb.AppendLine($"- [{solution.Path}](../{solution.Page})" +
                        (member.Folder.Length > 0 ? $" / {member.Folder}" : ""));
            }
            sb.AppendLine();
        }

        sb.AppendLine($"## Modules ({project.Modules.Count})\n");
        if (project.Modules.Count == 0) sb.AppendLine("No production code modules detected for this project.");
        foreach (var folder in project.Modules.Order(StringComparer.Ordinal))
            sb.AppendLine($"- [{folder}](../modules/{Slug(folder)}.md) — {Layer(folder) ?? "no layer"}");
        sb.AppendLine();

        var tops = model.Nodes.Where(n => TopKinds.Contains(n.Kind) && n.Kind != "module"
                && Top(n.Id) == n.Id && hierarchy!.Owner(n.File)?.Id == project.Id)
            .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (tops.Count > 0)
        {
            sb.AppendLine($"## Components ({tops.Count})\n\n| Component | Kind | Module |\n|---|---|---|");
            foreach (var component in tops)
            {
                var folder = Folder(component.File);
                sb.AppendLine($"| [{Esc(component.Name)}](../modules/{Slug(folder)}.md#{Anchor(component.Id)}) | " +
                    $"{component.Kind} | [{folder}](../modules/{Slug(folder)}.md) |");
            }
            sb.AppendLine();
        }

        var linked = tops.Select(n => (Symbol: n, Tests: testLinks.Of(n.Id)))
            .Where(x => x.Tests.Count > 0).ToList();
        if (linked.Count > 0)
        {
            sb.AppendLine($"## Test traceability ({linked.Count} components)\n");
            sb.AppendLine("Structural links to test code, not measured code coverage.\n");
            sb.AppendLine("| Component | Linked tests |\n|---|---|");
            foreach (var item in linked)
                sb.AppendLine($"| [{Esc(item.Symbol.Name)}](../modules/{Slug(Folder(item.Symbol.File))}.md#{Anchor(item.Symbol.Id)}) | " +
                    $"{Esc(string.Join(", ", item.Tests.Select(t => TestLinks.Short(t.Test))))} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Project references\n");
        var references = model.Edges.Where(e => e.Kind == "references" && e.From == project.Id)
            .Select(e => e.To).Distinct().Order(StringComparer.Ordinal).ToList();
        if (references.Count == 0) sb.AppendLine("No resolved project references in the selected scope.\n");
        foreach (var target in references)
        {
            if (hierarchy!.ById(target) is { } dependency)
                sb.AppendLine($"- [{dependency.Name}](../{dependency.Page}) — \x60{dependency.Path}\x60" +
                    (dependency.MetadataOnly ? " (metadata-only)" : ""));
            else
                sb.AppendLine($"- Unresolved reference: \x60{target}\x60");
        }
        sb.AppendLine();
        var packages = model.Edges.Where(e => e.Kind == "depends-on" && e.From == project.Id)
            .Select(e => nodes.TryGetValue(e.To, out var pkg) ? pkg.Name : e.To)
            .Distinct().Order(StringComparer.Ordinal).ToList();
        if (packages.Count > 0)
            sb.AppendLine($"## Direct dependencies ({packages.Count})\n\n" +
                string.Join("\n", packages.Select(p => "- \x60" + p + "\x60")) + "\n");

        var scope = model.ScopeInventory;
        var exclusions = scope?.ExcludedReferences.Where(e => e.StartsWith(project.Path + " → ", StringComparison.Ordinal)).ToList() ?? [];
        var unresolved = scope?.UnresolvedReferences.Where(e => e.StartsWith(project.Path + " → ", StringComparison.Ordinal)).ToList() ?? [];
        if (exclusions.Count > 0)
            sb.AppendLine($"## Excluded references ({exclusions.Count})\n\nThese edges were removed by the explicit project scope:\n\n" +
                string.Join("\n", exclusions.Select(e => "- \x60" + e.Replace("\x60", "'") + "\x60")) + "\n");
        if (unresolved.Count > 0)
            sb.AppendLine($"## Unresolved references ({unresolved.Count})\n\n" +
                string.Join("\n", unresolved.Select(e => "- \x60" + e.Replace("\x60", "'") + "\x60")) + "\n");

        sb.AppendLine("[System-wide architecture](../architecture.md) · [Quality](../quality.md)\n");
        return sb.ToString();
    }
}
