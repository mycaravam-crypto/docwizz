// The single source of truth for explicit project scope. Resolve a manifest-level inventory before any
// language scanner; downstream commands must use Files, not filter a finished CodeModel.
static class ProjectScope
{
    public sealed record Plan(List<string> Files, List<ProjectDiscovery.ProjectItem> MetadataOnly, int SelectedProjects);

    /// <summary>Validates an explicit scope; omitting scope entirely preserves the legacy full-repository scan.</summary>
    public static void Validate(ScopeConfig? scope)
    {
        if (scope is null) return;
        if (scope.Projects?.Include is not { Count: > 0 })
            throw new ArgumentException("scope.projects.include: select at least one project, or remove scope: to scan the full repository");
        if (scope.Projects.References is not ("metadata" or "include" or "exclude"))
            throw new ArgumentException("scope.projects.references: expected metadata, include or exclude");
        if (scope.UnassignedSources is not ("include" or "exclude"))
            throw new ArgumentException("scope.unassigned_sources: expected include or exclude");
        foreach (var path in scope.Projects.Include)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/')
                || path.EndsWith('/') || path.Split('/').Any(p => p is "" or "." or "..")
                || path.Contains(':') || path.Contains('*') || path.Contains('?')
                || path.Any(char.IsControl) || path.Contains('`'))
                throw new ArgumentException($"scope.projects.include: invalid relative project manifest path '{path}' (use '/' separators, no globbing or '..')");
        }
        if (scope.Projects.Include.Count != scope.Projects.Include.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("scope.projects.include: duplicate project manifest path");
    }

    /// <summary>Resolves selected manifests, referenced projects and test links, then filters files before source scanning.</summary>
    public static Plan Resolve(string root, ScopeConfig? scope, List<string> candidates)
    {
        if (scope is null) return new(candidates, [], 0);
        Validate(scope);
        root = Path.GetFullPath(root);
        var inventory = ProjectDiscovery.Scan(root, candidates);
        var projects = inventory.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in scope.Projects.Include)
        {
            var id = $"proj:{manifest}";
            if (!projects.ContainsKey(id))
                throw new ArgumentException($"scope.projects.include: '{manifest}' was not discovered below the scan root (check .gitignore, exclude: and paths)");
            selected.Add(id);
        }

        // MSBuild can place multiple manifests in one directory. Files cannot be safely assigned to only
        // one of them; require an all-or-none selection instead of leaking symbols from an unselected project.
        foreach (var group in inventory.Projects.GroupBy(p => Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? ""))
        {
            if (group.Count() > 1 && group.Any(p => selected.Contains(p.Id)) && group.Any(p => !selected.Contains(p.Id)))
                throw new ArgumentException("scope.projects.include: manifests share directory '" + group.Key
                    + "'; select all projects in that directory or move them to separate directories");
        }

        if (scope.Projects.References == "include")
        {
            var remaining = new Queue<string>(selected.Order(StringComparer.Ordinal));
            while (remaining.TryDequeue(out var id))
                foreach (var reference in projects[id].References)
                    if (projects.ContainsKey(reference) && selected.Add(reference)) remaining.Enqueue(reference);
        }

        // Reverse ProjectReference from a test project to selected production code: include its test code for
        // traceability. The normal BuildModel test filtering removes test symbols from production documentation.
        if (scope.Projects.TestLinks)
            foreach (var test in inventory.Projects.Where(p => p.Role == "test"))
                if (test.References.Any(selected.Contains)) selected.Add(test.Id);

        // Keep project identities separate even when a nested project shares its parent's directory tree.
        var owners = inventory.Projects
            .Select(p => (p.Id, p.Path, Folder: Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? ""))
            .OrderByDescending(p => p.Folder.Length).ThenBy(p => p.Path, StringComparer.Ordinal).ToList();
        string? Owner(string relative)
        {
            foreach (var project in owners)
                if (relative == project.Path || project.Folder.Length == 0
                    || relative.StartsWith(project.Folder + "/", StringComparison.Ordinal))
                    return project.Id;
            return null;
        }

        var solutions = inventory.Solutions.Where(s => s.Projects.Any(p => selected.Contains(p.ProjectId)))
            .Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        var filtered = candidates.Where(path =>
        {
            if (!ProjectDiscovery.SafeFile(root, Path.GetFullPath(path))) return false;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return solutions.Contains(rel);
            var owner = Owner(rel);
            return owner is null ? scope.UnassignedSources == "include" : selected.Contains(owner);
        }).ToList();

        var metadata = scope.Projects.References == "metadata"
            ? inventory.Projects.Where(p => !selected.Contains(p.Id) &&
                  inventory.Projects.Where(q => selected.Contains(q.Id)).Any(q => q.References.Contains(p.Id, StringComparer.Ordinal)))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToList()
            : [];
        return new(filtered, metadata, selected.Count);
    }

    /// <summary>Adds dependency-only stubs for excluded referenced manifests, then drops unresolved project edges.</summary>
    public static void CompleteReferences(Plan plan, List<Node> projects, List<Edge> edges)
    {
        foreach (var item in plan.MetadataOnly)
        {
            if (projects.Any(p => p.Id == item.Id)) continue;
            projects.Add(new Node(item.Id, "project", item.Name, item.Path, 1,
                Tags: [item.Ecosystem, item.Role, "metadata-only"]));
        }
        var ids = projects.Where(p => p.Kind == "project").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        edges.RemoveAll(e => e.Kind == "references" && !ids.Contains(e.To));
    }
}
