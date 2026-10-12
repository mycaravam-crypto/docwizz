using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Navigation is built from the scoped model and accepted files. One project can belong to many solutions.
sealed class ProjectHierarchy
{
    public sealed record Membership(string SolutionPage, string Folder);
    public sealed record Project(string Id, string Path, string Name, string Role, bool MetadataOnly, string Page,
        List<Membership> Solutions, List<string> Modules);
    public sealed record Solution(string Path, string Page, List<(string ProjectId, string Folder)> Projects,
        List<ProjectDiscovery.Missing> Unresolved);

    public List<Project> Projects { get; } = [];
    public List<Solution> Solutions { get; } = [];
    public List<string> UnassignedModules { get; } = [];
    readonly Dictionary<string, Project> projectsById = new(StringComparer.Ordinal);
    readonly Dictionary<string, Project> projectPages = new(StringComparer.Ordinal);
    readonly Dictionary<string, Solution> solutionPages = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Project>> modulePages = new(StringComparer.Ordinal);
    readonly Func<string, string?> ownerOf;

    public static string ProjectPage(string manifest) => FilePage("projects", manifest);
    public static string SolutionPage(string solution) => FilePage("solutions", solution);

    static string FilePage(string section, string relativePath)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        if (stem is "package" or "pom" or "build") stem = Path.GetFileName(Path.GetDirectoryName(relativePath)) ?? stem;
        stem = Regex.Replace(stem, @"[^A-Za-z0-9-]+", "-").Trim('-');
        if (stem.Length == 0) stem = "item";
        if (stem.Length > 48) stem = stem[..48].TrimEnd('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..12].ToLowerInvariant();
        return $"{section}/{stem}-{hash}.md";
    }

    public ProjectHierarchy(string root, CodeModel model, IEnumerable<string> acceptedFiles, IReadOnlyCollection<string> moduleFolders)
    {
        // Do not discover any manifests excluded by the project's active scan scope.
        var files = acceptedFiles.Select(f => Path.IsPathRooted(f) ? f : Path.Combine(root, f))
            .Where(f => File.Exists(f) && ProjectDiscovery.SafeFile(root, Path.GetFullPath(f))).ToList();
        var inventory = ProjectDiscovery.Scan(root, files);
        var memberships = inventory.Projects.ToDictionary(p => p.Id, p => p.Solutions, StringComparer.Ordinal);
        foreach (var n in model.Nodes.Where(n => n.Kind == "project").OrderBy(n => n.File, StringComparer.Ordinal))
        {
            var role = n.Tags?.FirstOrDefault(t => t is "test" or "executable" or "library") ?? "unknown";
            var stub = n.Tags?.Contains("metadata-only") == true;
            var links = memberships.GetValueOrDefault(n.Id)?.Select(m => new Membership(SolutionPage(m.Id[4..]), m.Folder))
                .OrderBy(m => m.SolutionPage, StringComparer.Ordinal).ToList() ?? [];
            var p = new Project(n.Id, n.File, n.Name, role, stub, ProjectPage(n.File), links, []);
            Projects.Add(p);
            projectsById.Add(p.Id, p);
            projectPages.Add(p.Page, p);
        }
        ownerOf = ProjectOwnership.Resolver(model.Nodes
            .Where(n => n.Kind == "project" && n.Tags?.Contains("metadata-only") != true)
            .Select(n => new ProjectOwnership.Candidate(n.Id, n.File, ProjectOwnership.Ecosystem(n))));
        foreach (var item in inventory.Solutions.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            var members = item.Projects.Where(m => projectsById.ContainsKey(m.ProjectId))
                .Select(m => (m.ProjectId, m.Folder)).OrderBy(m => m.ProjectId, StringComparer.Ordinal).ToList();
            // A referenced metadata-only project is present in the code model even though its manifest
            // was deliberately excluded from the source scan. Retain its solution membership.
            var slnFile = Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar));
            foreach (var (declared, folder) in global::Projects.SolutionFolders(slnFile))
            {
                string relative;
                try
                {
                    relative = Path.GetRelativePath(root, Path.GetFullPath(
                        Path.Combine(Path.GetDirectoryName(slnFile)!, declared))).Replace('\\', '/');
                }
                catch (ArgumentException) { continue; }
                if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) continue;
                var id = "proj:" + relative;
                if (!projectsById.TryGetValue(id, out var p) || !p.MetadataOnly || members.Any(m => m.ProjectId == id)) continue;
                members.Add((id, folder));
                p.Solutions.Add(new Membership(SolutionPage(item.Path), folder));
            }
            members = members.OrderBy(m => m.ProjectId, StringComparer.Ordinal).ToList();
            if (members.Count == 0) continue;
            var unresolved = item.UnresolvedProjects.Where(m => !members.Any(p => p.ProjectId == "proj:" + m.Path)).ToList();
            var solution = new Solution(item.Path, SolutionPage(item.Path), members, unresolved);
            Solutions.Add(solution);
            solutionPages.Add(solution.Page, solution);
        }
        foreach (var folder in moduleFolders.Order(StringComparer.Ordinal))
        {
            var owners = model.Nodes.Where(n => n.Kind is not ("project" or "package" or "external" or "config")
                    && Generator.Folder(n.File) == folder)
                .Select(n => Owner(n.File)).OfType<Project>().DistinctBy(p => p.Id)
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
            if (owners.Count == 0) UnassignedModules.Add(folder);
            else
            {
                modulePages.Add($"modules/{Generator.Slug(folder)}.md", owners);
                foreach (var p in owners) p.Modules.Add(folder);
            }
        }
    }

    // The same ownership rule as the scope filter (ProjectOwnership), cached per file. Metadata-only stubs own nothing.
    public Project? Owner(string file) => ownerOf(file) is { } id ? projectsById.GetValueOrDefault(id) : null;

    public IReadOnlyList<Project> ForModule(string folder) =>
        modulePages.GetValueOrDefault($"modules/{Generator.Slug(folder)}.md") ?? [];
    public Project? ById(string id) => projectsById.GetValueOrDefault(id);

    public string Index()
    {
        if (Projects.Count == 0) return "";
        var sb = new StringBuilder("## Solutions and projects\n\n");
        sb.AppendLine("Project identity is the relative manifest path; a shared project has one page linked from every solution.\n");
        foreach (var s in Solutions)
        {
            sb.AppendLine($"- **Solution:** [{s.Path}]({s.Page})");
            foreach (var (id, folder) in s.Projects)
                if (ById(id) is { } p)
                    sb.AppendLine($"  - [{p.Name}]({p.Page}) — \x60{p.Path}\x60" +
                        (folder.Length > 0 ? $", folder \x60{folder}\x60" : "") +
                        (p.MetadataOnly ? " (metadata-only)" : ""));
        }
        var standalone = Projects.Where(p => p.Solutions.Count == 0).ToList();
        if (standalone.Count > 0)
        {
            sb.AppendLine("- **Projects without a solution**");
            foreach (var p in standalone)
                sb.AppendLine($"  - [{p.Name}]({p.Page}) — \x60{p.Path}\x60" +
                    (p.MetadataOnly ? " (metadata-only)" : ""));
        }
        if (UnassignedModules.Count > 0)
        {
            sb.AppendLine("- **Unassigned modules**");
            foreach (var m in UnassignedModules)
                sb.AppendLine($"  - [{m}](modules/{Generator.Slug(m)}.md)");
        }
        return sb.AppendLine().ToString();
    }

    public string Sidebar(Func<string, string, string> link)
    {
        if (Projects.Count == 0) return "";
        var b = new StringBuilder("<details open><summary>Solutions &amp; projects</summary><ul>");
        void ProjectLine(Project p)
        {
            b.Append("<li>").Append(link(p.Page, p.Name)).Append(" <small>(")
                .Append(System.Net.WebUtility.HtmlEncode(p.Path)).Append(")</small>");
            if (p.Modules.Count > 0)
            {
                b.Append("<ul>");
                foreach (var module in p.Modules.Order(StringComparer.Ordinal))
                    b.Append("<li>").Append(link($"modules/{Generator.Slug(module)}.md", module)).Append("</li>");
                b.Append("</ul>");
            }
            b.Append("</li>");
        }
        foreach (var solution in Solutions)
        {
            b.Append("<li>").Append(link(solution.Page, solution.Path)).Append("<ul>");
            foreach (var (id, _) in solution.Projects)
                if (ById(id) is { } p) ProjectLine(p);
            b.Append("</ul></li>");
        }
        var standalone = Projects.Where(p => p.Solutions.Count == 0).ToList();
        if (standalone.Count > 0)
        {
            b.Append("<li>Without a solution<ul>");
            foreach (var p in standalone) ProjectLine(p);
            b.Append("</ul></li>");
        }
        if (UnassignedModules.Count > 0)
        {
            b.Append("<li>Unassigned modules<ul>");
            foreach (var folder in UnassignedModules)
                b.Append("<li>").Append(link($"modules/{Generator.Slug(folder)}.md", folder)).Append("</li>");
            b.Append("</ul></li>");
        }
        return b.Append("</ul></details>").ToString();
    }

    public string Breadcrumbs(string rel, Func<string, string, string> link)
    {
        var parts = new List<string>();
        if (solutionPages.TryGetValue(rel, out var sol))
            parts.Add(System.Net.WebUtility.HtmlEncode(sol.Path));
        else
        {
            Project? project = projectPages.GetValueOrDefault(rel);
            if (project is null && modulePages.TryGetValue(rel, out var owners) && owners.Count == 1)
                project = owners[0];
            if (project is not null)
            {
                var membership = project.Solutions.FirstOrDefault(m => solutionPages.ContainsKey(m.SolutionPage));
                if (membership is not null)
                    parts.Add(link(membership.SolutionPage, solutionPages[membership.SolutionPage].Path));
                parts.Add(rel == project.Page ? System.Net.WebUtility.HtmlEncode(project.Name)
                    : link(project.Page, project.Name));
            }
            if (rel.StartsWith("modules/", StringComparison.Ordinal))
                parts.Add(System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(rel)));
        }
        return string.Join("<span aria-hidden=\"true\">/</span>", parts);
    }
}
