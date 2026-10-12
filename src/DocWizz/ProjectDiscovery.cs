using System.Text.Json;
using System.Xml.Linq;

// Lightweight, read-only inventory. This deliberately does not call BuildModel or any language scanner.
// It reuses the existing project-manifest parser and solution-folder parser, so their classification
// semantics stay consistent with the generated documentation.
static class ProjectDiscovery
{
    public sealed record Missing(string Path, string Reason);
    public sealed record SolutionProject(string ProjectId, string Folder);
    public sealed record SolutionLink(string Id, string Folder);

    public sealed record SolutionItem(string Id, string Path, List<SolutionProject> Projects, List<Missing> UnresolvedProjects);
    public sealed record ProjectItem(
        string Id, string Path, string Name, string Ecosystem, string Role, string? Sdk,
        List<string> TargetFrameworks, List<SolutionLink> Solutions,
        List<string> References, List<Missing> UnresolvedReferences);
    public sealed record Inventory(List<SolutionItem> Solutions, List<ProjectItem> Projects, List<string> UnassignedSources);

    static readonly string[] SourceExtensions = [".cs", ".java", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".php"];
    static readonly string[] ProjectNames = ["package.json", "pom.xml", "build.gradle", "build.gradle.kts"];

    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    static bool InRoot(string rel) => rel != ".." && !rel.StartsWith("../", StringComparison.Ordinal)
        && !Path.IsPathRooted(rel);

    // Do not read a manifest/solution through a symlink (including one of its ancestor folders).
    // This prevents a repository-controlled symlink from redirecting discovery outside the chosen root.
    static bool SafeFile(string root, string file)
    {
        var relative = Rel(root, file);
        if (!InRoot(relative)) return false;
        var current = root;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    static string? Target(string root, string parentFile, string declared)
    {
        var value = declared.Replace('\\', '/');
        if (Path.IsPathRooted(value) || System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z]:"))
            return null;
        try
        {
            var absolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentFile)!, value));
            var relative = Rel(root, absolute);
            return InRoot(relative) ? relative : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    static string MissingReason(string root, string path, HashSet<string> scanned)
    {
        if (scanned.Contains(path)) return "not-parsed";
        return File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))) ? "not-scanned" : "not-found";
    }

    public static Inventory Scan(string root, IEnumerable<string> files)
    {
        root = Path.GetFullPath(root);
        var accepted = files.Select(Path.GetFullPath).Where(f => SafeFile(root, f))
            .Distinct(StringComparer.Ordinal).OrderBy(f => Rel(root, f), StringComparer.Ordinal).ToList();
        var manifests = accepted.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || ProjectNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
        var solutions = accepted.Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToList();

        var (nodes, edges) = Projects.Scan(root, [.. manifests, .. solutions]);
        var projects = nodes.Where(n => n.Kind == "project")
            .OrderBy(n => n.File, StringComparer.Ordinal)
            .Select(n => new ProjectItem(
                n.Id, n.File, n.Name,
                n.Tags?.FirstOrDefault(t => t is "dotnet" or "npm" or "maven" or "gradle") ?? "unknown",
                n.Tags?.FirstOrDefault(t => t is "test" or "executable" or "library") ?? "unknown",
                n.Tags?.FirstOrDefault(t => t.StartsWith("Microsoft.NET.Sdk", StringComparison.Ordinal)),
                (n.Tags ?? []).Where(t => t.StartsWith("net", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList(),
                [], [], []))
            .ToList();
        var byId = projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var manifestPaths = manifests.Select(f => Rel(root, f)).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in edges.Where(e => e.Kind == "references").OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(edge.From, out var source)) continue;
            if (byId.ContainsKey(edge.To))
                source.References.Add(edge.To);
            else
            {
                var target = edge.To.StartsWith("proj:", StringComparison.Ordinal) ? edge.To[5..] : edge.To;
                var reason = InRoot(target) ? MissingReason(root, target, manifestPaths) : "outside-root";
                source.UnresolvedReferences.Add(new(target, reason));
            }
        }

        var foundSolutions = new List<SolutionItem>();
        foreach (var solution in solutions)
        {
            var rel = Rel(root, solution);
            var id = $"sln:{rel}";
            var members = new List<SolutionProject>();
            var missing = new List<Missing>();
            foreach (var (declared, folder) in Projects.SolutionFolders(solution))
            {
                var target = Target(root, solution, declared);
                if (target is null)
                {
                    missing.Add(new(declared.Replace('\\', '/'), "outside-root-or-invalid"));
                    continue;
                }
                var projectId = $"proj:{target}";
                if (!byId.TryGetValue(projectId, out var project))
                {
                    missing.Add(new(target, MissingReason(root, target, manifestPaths)));
                    continue;
                }
                var normalizedFolder = folder.Trim('/');
                if (!members.Any(m => m.ProjectId == projectId))
                    members.Add(new(projectId, normalizedFolder));
                if (!project.Solutions.Any(s => s.Id == id))
                    project.Solutions.Add(new(id, normalizedFolder));
            }
            foundSolutions.Add(new(id, rel,
                members.OrderBy(m => m.ProjectId, StringComparer.Ordinal).ToList(),
                missing.OrderBy(m => m.Path, StringComparer.Ordinal).ToList()));
        }

        foreach (var project in projects)
        {
            project.References.Sort(StringComparer.Ordinal);
            project.UnresolvedReferences.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
            project.Solutions.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        }

        // Only filenames are inspected: a source without a manifest ancestor must remain visible in discovery.
        var folders = projects.Select(p => Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? "").ToList();
        var unassigned = accepted.Where(f => SourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Rel(root, f)).Where(rel => !folders.Any(folder => folder.Length == 0
                || rel.StartsWith(folder + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();

        return new(foundSolutions, projects, unassigned);
    }

    public static string ConsoleReport(Inventory inventory)
    {
        var lines = new List<string> { $"Solutions: {inventory.Solutions.Count}; projects: {inventory.Projects.Count}" };
        foreach (var solution in inventory.Solutions)
        {
            lines.Add($"Solution {solution.Path} ({solution.Projects.Count} projects)");
            foreach (var member in solution.Projects)
            {
                var project = inventory.Projects.Single(p => p.Id == member.ProjectId);
                lines.Add($"  {project.Name} [{project.Role}] — {project.Path}" +
                    (member.Folder.Length > 0 ? $" (folder: {member.Folder})" : ""));
            }
            foreach (var missing in solution.UnresolvedProjects)
                lines.Add($"  unresolved: {missing.Path} ({missing.Reason})");
        }
        foreach (var project in inventory.Projects.Where(p => p.Solutions.Count == 0))
            lines.Add($"Standalone {project.Name} [{project.Role}] — {project.Path}");
        foreach (var project in inventory.Projects.Where(p => p.UnresolvedReferences.Count > 0))
            foreach (var missing in project.UnresolvedReferences)
                lines.Add($"Reference unresolved: {project.Path} → {missing.Path} ({missing.Reason})");
        lines.Add($"Unassigned source files: {inventory.UnassignedSources.Count}");
        return string.Join(Environment.NewLine, lines);
    }
}
