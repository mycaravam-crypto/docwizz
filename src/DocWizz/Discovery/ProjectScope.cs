// The single source of truth for explicit project scope. Resolve a manifest-level inventory before any
// language scanner; downstream commands must use Files, not filter a finished CodeModel.
sealed class ScopeException(string message) : ArgumentException(message);

// A scoped repository still has shared deployment/CI metadata, even when application sources are selective.
static class ProjectScope
{
    public sealed record Plan(List<string> Files, List<ProjectDiscovery.ProjectItem> MetadataOnly, int SelectedProjects,
        ScopeInventory? Inventory = null);

    static readonly string[] SourceExtensions = [".cs", ".java", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".php"];

    /// <summary>Validates an explicit scope; omitting scope entirely preserves the legacy full-repository scan.</summary>
    public static void Validate(ScopeConfig? scope)
    {
        if (scope is null) return;
        if (scope.Projects?.Include is not { Count: > 0 })
            throw new ScopeException("scope.projects.include: select at least one project, or remove scope: to scan the full repository");
        if (scope.Projects.References is not ("metadata" or "include" or "exclude"))
            throw new ScopeException("scope.projects.references: expected metadata, include or exclude");
        if (scope.RepositoryFiles is not ("include" or "exclude"))
            throw new ScopeException("scope.repository_files: expected include or exclude");
        if (scope.UnassignedSources is not ("include" or "exclude"))
            throw new ScopeException("scope.unassigned_sources: expected include or exclude");
        foreach (var path in scope.Projects.Include)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/')
                || path.EndsWith('/') || path.Split('/').Any(p => p is "" or "." or "..")
                || path.Contains(':') || path.Contains('*') || path.Contains('?')
                || path.Any(char.IsControl) || path.Contains('`'))
                throw new ScopeException($"scope.projects.include: invalid relative project manifest path '{path}' (use '/' separators, no globbing or '..')");
        }
        if (scope.Projects.Include.Count != scope.Projects.Include.Distinct(StringComparer.Ordinal).Count())
            throw new ScopeException("scope.projects.include: duplicate project manifest path");
    }

    /// <summary>Stable identifier for the scope configuration, used to invalidate a model generated under a different selection.</summary>
    public static string? Fingerprint(ScopeConfig? scope)
    {
        if (scope is null) return null;
        Validate(scope);
        var text = string.Join("\n", scope.Projects.Include.Order(StringComparer.Ordinal)) +
            $"\nreferences={scope.Projects.References}\ntest_links={scope.Projects.TestLinks}\nunassigned={scope.UnassignedSources}\nrepository_files={scope.RepositoryFiles}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>Resolves selected manifests, referenced projects and test links, then filters files before source scanning.
    /// <paramref name="historical"/>: a tree as of an older commit, where a selected project may not exist yet; it is
    /// then simply not there instead of an error (the working tree is what validates the configuration).</summary>
    public static Plan Resolve(string root, ScopeConfig? scope, List<string> candidates, bool historical = false)
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
            {
                if (historical) continue;
                throw new ScopeException($"scope.projects.include: '{manifest}' was not discovered below the scan root (check .gitignore, exclude: and paths)");
            }
            selected.Add(id);
        }

        if (scope.Projects.References == "include")
        {
            var remaining = new Queue<string>(selected.Order(StringComparer.Ordinal));
            while (remaining.TryDequeue(out var id))
                foreach (var reference in projects[id].References)
                    if (projects.ContainsKey(reference) && selected.Add(reference)) remaining.Enqueue(reference);
        }

        // Metadata-only stubs reflect production scope, never incidental references from test projects.
        var production = selected.Where(id => projects[id].Role != "test").ToHashSet(StringComparer.Ordinal);
        var metadata = scope.Projects.References == "metadata"
            ? inventory.Projects.Where(p => !selected.Contains(p.Id) &&
                  inventory.Projects.Where(q => production.Contains(q.Id)).Any(q => q.References.Contains(p.Id, StringComparer.Ordinal)))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToList()
            : new List<ProjectDiscovery.ProjectItem>();

        // Reverse ProjectReference from a test project to selected production code: include its test code for
        // traceability. The normal BuildModel test filtering removes test symbols from production documentation.
        if (scope.Projects.TestLinks)
            foreach (var test in inventory.Projects.Where(p => p.Role == "test"))
                if (test.References.Any(selected.Contains)) selected.Add(test.Id);

        // MSBuild can place multiple manifests in one directory. Files cannot be safely assigned to only
        // one of them; require an all-or-none selection instead of leaking symbols from an unselected project.
        foreach (var group in inventory.Projects.GroupBy(p => Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? ""))
        {
            if (group.Count() > 1 && group.Any(p => selected.Contains(p.Id)) && group.Any(p => !selected.Contains(p.Id)))
                throw new ScopeException("scope.projects.include: manifests share directory '" + group.Key
                    + "'; select all projects in that directory or move them to separate directories");
        }

        var testProjects = selected.Where(id => projects[id].Role == "test")
            .Select(id => projects[id].Path).Order(StringComparer.Ordinal).ToList();
        var scoped = new ScopeInventory(
            selected.Select(id => projects[id].Path).Order(StringComparer.Ordinal).ToList(),
            testProjects,
            metadata.Select(p => p.Path).ToList(),
            scope.Projects.References == "exclude"
                ? inventory.Projects.Where(p => production.Contains(p.Id))
                    .SelectMany(p => p.References.Where(id => !selected.Contains(id))
                        .Select(id => ReferenceEdge(p.Path, projects[id].Path)))
                    .Distinct().Order(StringComparer.Ordinal).ToList()
                : [],
            inventory.Projects.Where(p => production.Contains(p.Id))
                .SelectMany(p => p.UnresolvedReferences.Select(r => ReferenceEdge(p.Path, r.Path) + " (" + r.Reason + ")"))
                .Distinct().Order(StringComparer.Ordinal).ToList());

        // Keep project identities separate even when a nested project shares its parent's directory tree.
        var ownerOf = ProjectOwnership.Resolver(inventory.Projects.Select(p => new ProjectOwnership.Candidate(p.Id, p.Path, p.Ecosystem)));

        var solutions = inventory.Solutions.Where(s => s.Projects.Any(p => selected.Contains(p.ProjectId)))
            .Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        var filtered = candidates.Where(path =>
        {
            if (!ProjectDiscovery.SafeFile(root, Path.GetFullPath(path))) return false;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return solutions.Contains(rel);
            var owner = ownerOf(rel);
            if (owner is not null) return selected.Contains(owner);
            // A repository-level descriptor is not unassigned application source. Preserve it separately
            // so scoped generation still reports CI, Kubernetes, compose, IaC and shared configuration.
            if (scope.RepositoryFiles == "include" &&
                (DeploymentDescriptors.Is(root, rel) || Configuration.IsConfigFile(path)))
                return true;
            // A solution or project manifest is never pulled in as an incidental repository file.
            if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(rel) is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts")
                return false;
            return SourceExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase)
                && scope.UnassignedSources == "include";
        }).ToList();

        return new(filtered, metadata, selected.Count, scoped);
    }

    // ScopeInventory keeps reference edges as display strings (they are part of the persisted model.json);
    // they are built and matched only here, so the renderers never depend on the separator.
    const string Arrow = " → ";
    public static string ReferenceEdge(string from, string to) => from + Arrow + to;
    public static bool IsReferenceFrom(string edge, string from) => edge.StartsWith(from + Arrow, StringComparison.Ordinal);

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
