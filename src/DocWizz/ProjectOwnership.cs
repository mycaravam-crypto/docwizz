// Which project manifest owns a repository file. Scope filtering (ProjectScope) and project navigation
// (ProjectHierarchy) both use this, so a file is never scanned for one project and listed under another.
// The nearest manifest folder wins; a manifest at the repository root owns its own manifest and only the
// otherwise unclaimed source files of its language, never siblings such as Dockerfiles or other ecosystems.
static class ProjectOwnership
{
    public sealed record Candidate(string Id, string Path, string Ecosystem);

    public static string Ecosystem(Node project) =>
        project.Tags?.FirstOrDefault(t => t is "dotnet" or "npm" or "maven" or "gradle") ?? "unknown";

    /// <summary>Returns a cached resolver from a root-relative, '/'-separated file path to the owning project id.</summary>
    public static Func<string, string?> Resolver(IEnumerable<Candidate> projects)
    {
        var owners = projects
            .Select(p => (p.Id, p.Path, p.Ecosystem, Folder: System.IO.Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? ""))
            .OrderByDescending(p => p.Folder.Length).ThenBy(p => p.Path, StringComparer.Ordinal).ToList();
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        return relative => cache.TryGetValue(relative, out var hit) ? hit : cache[relative] = Find(relative);

        string? Find(string relative)
        {
            foreach (var project in owners)
            {
                if (relative == project.Path) return project.Id;
                if (project.Folder.Length > 0)
                {
                    if (relative.StartsWith(project.Folder + "/", StringComparison.Ordinal)) return project.Id;
                    continue;
                }
                if (RootOwns(project.Ecosystem, relative)) return project.Id;
            }
            return null;
        }
    }

    static bool RootOwns(string ecosystem, string relative)
    {
        var ext = System.IO.Path.GetExtension(relative).ToLowerInvariant();
        return ecosystem == "dotnet" && ext == ".cs"
            || ecosystem == "npm" && ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".vue"
            || ecosystem is "maven" or "gradle" && ext == ".java";
    }
}
