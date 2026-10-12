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
    readonly Dictionary<string, List<Node>> nodesByProject = new(StringComparer.Ordinal);
    readonly ILookup<string, Edge> projectEdgesBySource;
    readonly Func<string, string?> ownerOf;

    // Observable invariant for regression tests: constructing the hierarchy resolves ownership no more
    // than once per eligible model node, independent of the project/module count.
    internal int OwnershipLookups { get; private set; }

    public static string ProjectPage(string manifest) => FilePage("projects", manifest);
    public static string SolutionPage(string solution) => FilePage("solutions", solution);

    static string FilePage(string section, string relativePath)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        if (stem is "package" or "pom" or "build") stem = Path.GetFileName(Path.GetDirectoryName(relativePath)) ?? stem;
        stem = Regex.Replace(Naming.Ascii(stem), @"[^A-Za-z0-9-]+", "-").Trim('-');
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
        projectEdgesBySource = model.Edges.Where(e => e.Kind is "references" or "depends-on")
            .ToLookup(e => e.From, StringComparer.Ordinal);
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
        // Index every model node once. The former per-module scan revisited every node for each folder;
        // per-project rendering then scanned the whole model again for each project (O(modules*nodes +
        // projects*nodes)). This index makes both paths linear in node count plus rendered output.
        var ownersByFolder = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in model.Nodes.Where(n => n.Kind is not ("project" or "package" or "external" or "config")))
        {
            if (Owner(node.File) is not { } project) continue;
            if (!nodesByProject.TryGetValue(project.Id, out var owned))
                nodesByProject[project.Id] = owned = [];
            owned.Add(node);
            var folder = Naming.Folder(node.File);
            if (!ownersByFolder.TryGetValue(folder, out var ids))
                ownersByFolder[folder] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(project.Id);
        }
        foreach (var folder in moduleFolders.Order(StringComparer.Ordinal))
        {
            var owners = ownersByFolder.GetValueOrDefault(folder)?.Select(id => projectsById[id])
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToList() ?? [];
            if (owners.Count == 0) UnassignedModules.Add(folder);
            else
            {
                modulePages.Add($"modules/{Naming.Slug(folder)}.md", owners);
                foreach (var p in owners) p.Modules.Add(folder);
            }
        }
    }

    // The same ownership rule as the scope filter (ProjectOwnership), cached per file. Metadata-only stubs own nothing.
    public Project? Owner(string file)
    {
        OwnershipLookups++;
        return ownerOf(file) is { } id ? projectsById.GetValueOrDefault(id) : null;
    }

    /// <summary>Production nodes assigned to a project, indexed once when the hierarchy is constructed.</summary>
    public IReadOnlyList<Node> NodesFor(string projectId) => nodesByProject.GetValueOrDefault(projectId) ?? [];

    /// <summary>Direct project reference and package edges, indexed by source once for all project pages.</summary>
    public IEnumerable<Edge> EdgesFrom(string projectId) => projectEdgesBySource[projectId];

    /// <summary>Retrieves a solution navigation entry by stable relative page, without scanning the solution list.</summary>
    public Solution? SolutionByPage(string page) => solutionPages.GetValueOrDefault(page);

    public IReadOnlyList<Project> ForModule(string folder) =>
        modulePages.GetValueOrDefault($"modules/{Naming.Slug(folder)}.md") ?? [];
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
                sb.AppendLine($"  - [{m}](modules/{Naming.Slug(m)}.md)");
        }
        return sb.AppendLine().ToString();
    }

    // The sidebar tree: solution → project → module, then projects without a solution and unassigned modules.
    // Applications before libraries before tests, then by name; module labels drop the folder prefix their project's
    // modules share, so `backend/Api` reads `Api` under its project (the full folder stays in the hint).
    public IReadOnlyList<NavNode> Navigation()
    {
        if (Projects.Count == 0) return [];
        var duplicateNames = Projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static int Rank(Project p) => p.Role switch { "executable" => 0, "library" => 1, "test" => 3, _ => 2 };
        IEnumerable<Project> Sorted(IEnumerable<Project> projects) => projects.OrderBy(Rank)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Path, StringComparer.Ordinal);
        NavNode ProjectNode(Project p)
        {
            var dir = Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? "";
            var label = duplicateNames.Contains(p.Name) && dir.Length > 0 ? $"{p.Name} ({dir})" : p.Name;
            var badge = p.MetadataOnly ? "stub" : p.Role switch { "test" => "test", "executable" => "app", _ => null };
            return new NavNode(label, p.Page, "project", badge, p.Path, ModuleNodes(p.Modules));
        }
        var roots = new List<NavNode>();
        foreach (var solution in Solutions.OrderBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.Path, StringComparer.Ordinal))
        {
            var members = Sorted(solution.Projects.Select(m => ById(m.ProjectId)).OfType<Project>()).Select(ProjectNode).ToList();
            roots.Add(new NavNode(Path.GetFileName(solution.Path), solution.Page, "solution", null, solution.Path, members));
        }
        // Alone in the tree, projects need no folder of their own.
        var standalone = Sorted(Projects.Where(p => p.Solutions.Count == 0)).Select(ProjectNode).ToList();
        if (roots.Count == 0 && UnassignedModules.Count == 0) roots.AddRange(standalone);
        else if (standalone.Count > 0) roots.Add(new NavNode("Projects without a solution", null, "folder", null, null, standalone));
        if (UnassignedModules.Count > 0)
            roots.Add(new NavNode("Unassigned modules", null, "folder", null, "Folders no project owns", ModuleNodes(UnassignedModules)));
        return roots;
    }

    // Labels relative to the folder the modules share; the module at that folder itself keeps its full name and comes first.
    static List<NavNode> ModuleNodes(IReadOnlyCollection<string> folders)
    {
        var common = folders.Count < 2 ? "" : folders.Select(f => f.Split('/')).Aggregate((a, b) =>
            a.Zip(b).TakeWhile(x => x.First == x.Second).Select(x => x.First).ToArray()) is { Length: > 0 } shared
            ? string.Join('/', shared) : "";
        string Label(string folder) =>
            common.Length == 0 || folder == common ? (folder == "." ? "(root)" : folder)
            : folder.StartsWith(common + "/", StringComparison.Ordinal) ? folder[(common.Length + 1)..] : folder;
        return folders.Select(f => new NavNode(Label(f), $"modules/{Naming.Slug(f)}.md", "module", null, f))
            .OrderBy(n => n.Hint == common ? 0 : 1).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string Breadcrumbs(string rel, Func<string, string, string> link, string? title = null)
    {
        var parts = new List<string>();
        if (solutionPages.TryGetValue(rel, out var sol))
            parts.Add(Html.Crumb(sol.Path));
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
                parts.Add(rel == project.Page ? Html.Crumb(project.Name)
                    : link(project.Page, project.Name));
            }
            if (rel.StartsWith("modules/", StringComparison.Ordinal))
                parts.Add(Html.Crumb(title ?? Path.GetFileNameWithoutExtension(rel)));
        }
        return string.Join(Html.CrumbSeparator, parts);
    }
}
