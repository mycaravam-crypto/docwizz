using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// TTY-only interaction for the setup wizard. Keeping the input/output streams explicit makes it
// possible to test the full interaction without allocating a pseudo-terminal or prompting in CI.
static class SetupProjectSelection
{
    public sealed record Choice(List<string> Projects, bool Cancelled = false);
    public static bool Interactive => CanPrompt(Console.IsInputRedirected, Console.IsOutputRedirected,
        Environment.UserInteractive, Environment.GetEnvironmentVariable("CI"));

    /// <summary>Never prompt in CI, even when the CI runner allocates a pseudo-terminal.</summary>
    public static bool CanPrompt(bool inputRedirected, bool outputRedirected, bool userInteractive, string? ci) =>
        !inputRedirected && !outputRedirected && userInteractive && string.IsNullOrEmpty(ci);

    /// <summary>Returns a project selection using numbered projects, whole solutions and select all/none commands.</summary>
    public static Choice Prompt(ProjectDiscovery.Inventory inventory, IReadOnlyCollection<string> initial, TextReader input, TextWriter output,
        Action<IReadOnlyCollection<string>>? validate = null)
    {
        var items = inventory.Projects.OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        if (items.Count == 0) throw new ScopeException("setup: no project manifests discovered; nothing to select");
        var selected = initial.ToHashSet(StringComparer.Ordinal);
        var solutions = inventory.Solutions.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
        output.WriteLine();
        output.WriteLine("DocWizz project selection (no source code has been scanned yet)");
        for (var i = 0; i < solutions.Count; i++)
            output.WriteLine($"  s{i + 1}  solution {solutions[i].Path} ({solutions[i].Projects.Count} projects)");
        for (var i = 0; i < items.Count; i++)
        {
            var p = items[i];
            var dep = p.References.Count == 0 ? "" : $", {p.References.Count} project references";
            var sln = p.Solutions.Count == 0 ? "standalone" : string.Join(", ", p.Solutions.Select(s => s.Id[4..]));
            output.WriteLine($"  {i + 1,3} [{(selected.Contains(p.Path) ? 'x' : ' ')}] {p.Name} [{p.Role}] ({sln}{dep}) — {p.Path}");
        }
        output.WriteLine("Commands: 1,3 toggle projects; s1 toggle a solution; a all; n none; d done; q cancel");
        output.WriteLine("Test projects are labelled; test_links also discovers tests referenced by selected production projects.");
        while (true)
        {
            output.Write($"Selected {selected.Count}/{items.Count} > ");
            output.Flush();
            var line = input.ReadLine();
            if (line is null) return new([], Cancelled: true);
            var tokens = line.Split([',', ' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            var current = selected.ToHashSet(StringComparer.Ordinal);
            var invalid = false;
            foreach (var token in tokens)
            {
                switch (token.ToLowerInvariant())
                {
                    case "q" or "quit":
                        return new([], Cancelled: true);
                    case "d" or "done":
                        if (current.Count == 0)
                        {
                            output.WriteLine("Select at least one project, or press q to cancel.");
                            invalid = true;
                            break;
                        }
                        var confirmed = current.Order(StringComparer.Ordinal).ToList();
                        try { validate?.Invoke(confirmed); }
                        catch (ScopeException e)
                        {
                            output.WriteLine("Invalid selection: " + e.Message);
                            output.WriteLine("Change the selection and enter d again, or q to cancel.");
                            selected = current; // keep the proposed choices for correction
                            invalid = true;
                            break;
                        }
                        return new(confirmed);
                    case "a" or "all":
                        current = items.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
                        break;
                    case "n" or "none":
                        current.Clear();
                        break;
                    default:
                        if (token.StartsWith('s') && int.TryParse(token[1..], out var sn) && sn > 0 && sn <= solutions.Count)
                        {
                            var paths = solutions[sn - 1].Projects
                                .Select(member => items.FirstOrDefault(p => p.Id == member.ProjectId)?.Path)
                                .OfType<string>().ToList();
                            if (paths.All(current.Contains))
                                foreach (var path in paths) current.Remove(path);
                            else
                                foreach (var path in paths) current.Add(path);
                        }
                        else if (int.TryParse(token, out var number) && number > 0 && number <= items.Count)
                        {
                            var path = items[number - 1].Path;
                            if (!current.Add(path)) current.Remove(path);
                        }
                        else
                        {
                            output.WriteLine($"Unknown selection '{token}'. Choose a listed number, sN, a, n, d or q.");
                            invalid = true;
                        }
                        break;
                }
                if (invalid) break;
            }
            if (!invalid)
            {
                selected = current;
                output.WriteLine($"Selected {selected.Count}: {string.Join(", ", selected.Order(StringComparer.Ordinal))}");
            }
        }
    }

    /// <summary>Suggests initial production project selection, prioritizing solutions at the selected repository root.</summary>
    public static List<string> Initial(ProjectDiscovery.Inventory inventory, ScopeConfig? existing)
    {
        if (existing?.Projects.Include is { Count: > 0 } saved)
        {
            var present = inventory.Projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
            return saved.Where(present.Contains).Order(StringComparer.Ordinal).ToList();
        }
        var rootSolutions = inventory.Solutions.Where(s => !s.Path.Contains('/')).ToList();
        var ids = rootSolutions.SelectMany(s => s.Projects).Select(p => p.ProjectId).ToHashSet(StringComparer.Ordinal);
        var initial = inventory.Projects.Where(p => p.Role != "test" && ids.Contains(p.Id)).Select(p => p.Path).ToList();
        if (initial.Count == 0)
            initial = inventory.Projects.Where(p => p.Role != "test").Select(p => p.Path).ToList();
        if (initial.Count == 0)
            initial = inventory.Projects.Select(p => p.Path).ToList();
        return initial.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Creates a valid scope from the selection, retaining an existing policy when one is defined.</summary>
    public static ScopeConfig Scope(IReadOnlyCollection<string> projects, ScopeConfig? original)
    {
        var scope = new ScopeConfig
        {
            Projects = new ScopeProjectsConfig
            {
                Include = projects.Order(StringComparer.Ordinal).ToList(),
                References = original?.Projects.References ?? "metadata",
                TestLinks = original?.Projects.TestLinks ?? true
            },
            UnassignedSources = original?.UnassignedSources ?? "exclude",
            RepositoryFiles = original?.RepositoryFiles ?? "include"
        };
        ProjectScope.Validate(scope);
        return scope;
    }

    /// <summary>Changes only the top-level scope YAML block; all other configuration lines are preserved byte for byte.</summary>
    public static string WithScope(string yaml, ScopeConfig? scope)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<(int From, int End, string Text)>();
        var index = 0;
        while (index < yaml.Length)
        {
            var next = yaml.IndexOf('\n', index);
            var end = next < 0 ? yaml.Length : next + 1;
            lines.Add((index, end, yaml[index..end]));
            index = end;
        }

        // Only an unindented mapping key can delimit scope. Do not misidentify nested "scope:"
        // under checks, or a comment mentioning that name.
        var positions = lines.Select((l, i) => (l, i))
            .Where(x => Regex.IsMatch(x.l.Text, @"^scope\s*:")).Select(x => x.i).ToList();
        if (positions.Count > 1)
            throw new ScopeException("docwizz.yaml: multiple top-level scope: blocks; remove duplicates before selection");

        var rendered = scope is null ? "" : string.Join(newline, (List<string>)[

            "scope:",
            "  projects:",
            "    include:",
            .. scope.Projects.Include.Order(StringComparer.Ordinal).Select(p => "      - " + JsonSerializer.Serialize(p)),
            "    references: " + scope.Projects.References,
            "    test_links: " + scope.Projects.TestLinks.ToString().ToLowerInvariant(),
            "  unassigned_sources: " + scope.UnassignedSources,
            "  repository_files: " + scope.RepositoryFiles,
            ""
        ]);
        if (positions.Count == 0)
        {
            if (rendered.Length == 0) return yaml;
            return yaml + (yaml.Length > 0 && !yaml.EndsWith('\n') ? newline : "") + rendered;
        }

        var first = positions[0];
        var last = first + 1;
        while (last < lines.Count)
        {
            var text = lines[last].Text;
            if (text.Length > 0 && text[0] is not (' ' or '\t' or '\r' or '\n')) break;
            last++;
        }
        // Trailing blank lines separate top-level YAML sections; they are not part of scope.
        while (last > first + 1 && string.IsNullOrWhiteSpace(lines[last - 1].Text)) last--;
        return yaml[..lines[first].From] + rendered + (last < lines.Count ? yaml[lines[last].From..] : "");
    }

    /// <summary>Writes a validated scope update atomically, avoiding changes to unrelated YAML keys or comments.</summary>
    public static string Save(string root, ScopeConfig? scope, string? profile = null)
    {
        var file = Path.Combine(root, "docwizz.yaml");
        if (!File.Exists(file)) throw new ScopeException("setup: cannot update project selection before docwizz.yaml exists");
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new ScopeException("setup: refusing to replace a symlinked docwizz.yaml");
        var original = File.ReadAllText(file);
        var updated = WithScope(original, scope);
        Config.Parse(updated, root, profile, file);  // all existing rules remain valid
        if (updated == original) return "kept";
        var temp = Path.Combine(root, ".docwizz-scope-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (OperatingSystem.IsWindows()) File.WriteAllText(temp, updated);
            else
            {
                // Create the temporary file with the original permissions, not a permissive umask first.
                var mode = File.GetUnixFileMode(file);
                using (var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = mode
                }))
                using (var writer = new StreamWriter(stream))
                    writer.Write(updated);
                File.SetUnixFileMode(temp, mode);
            }
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return "scope-updated";
    }
}
