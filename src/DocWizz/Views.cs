using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

// Architecture viewpoints: focused views over the same code graph instead of one giant diagram.
// The dependency view is architecture.md, the API view is api.md.
partial class Generator
{
    const int MaxViewNodes = 40;

    IEnumerable<(string Page, string Title, Func<string> Body)> Views() =>
    [
        ("context", "System context", ContextView),
        ("containers", "Containers", ContainerView),
        ("components", "Components", ComponentView),
        ("data", "Data", DataView),
        ("deployment", "Deployment", DeploymentView),
    ];

    string SystemName => Path.GetFileName(Path.GetFullPath(root).TrimEnd('/'));
    List<Node> Projects => model.Nodes.Where(n => n.Kind == "project").ToList();
    List<Node> DbContexts => model.Nodes.Where(n => n.Tags?.Contains("dbcontext") == true).ToList();
    bool HasFrontend => model.Nodes.Any(n => n.Kind is "component" or "route");

    List<Node> ExternalSystems => model.Nodes.Where(n => n.Kind == "external").OrderBy(n => Externals.Category(n)).ThenBy(n => n.Name).ToList();
    ILookup<string, Edge> Connections => connections ??= model.Edges.Where(e => e.Kind == "connects").ToLookup(e => e.To);
    ILookup<string, Edge>? connections;

    IEnumerable<Node> DatabasesOf(Node ctx) => model.Edges.Where(e => e.Kind == "connects" && e.From == ctx.Id)
        .Select(e => nodes.GetValueOrDefault(e.To)).OfType<Node>();

    // "SQL Server _(inferred)_": the name with its certainty unless detected.
    static string ExternalLabel(Node ext) => Externals.Certainty(ext) == "detected" ? ext.Name : $"{ext.Name} ({Externals.Certainty(ext)})";

    // The container (project) a symbol lives in; code outside any project is grouped by language.
    Dictionary<string, string>? containerOf;
    string Container(Node n)
    {
        containerOf ??= [];
        if (containerOf.TryGetValue(n.File, out var c)) return c;
        return containerOf[n.File] = n.Kind == "project" ? n.Name
            : global::Projects.Of(Projects, n.File)?.Name ?? (n.File.EndsWith(".cs") ? "C# code" : "frontend code");
    }

    string ContextView()
    {
        var sb = new StringBuilder("# System context\n\n");
        sb.AppendLine("The system as one box: who uses it and what it relies on outside its own code.\n");
        var edges = new List<(string, string, int)>();
        if (HasFrontend) edges.Add(("Users (browser)", SystemName, 0));
        else if (model.Nodes.Any(n => n.Tags?.Contains("endpoint") == true)) edges.Add(("API clients", SystemName, 0));
        foreach (var ext in ExternalSystems) edges.Add((SystemName, ExternalLabel(ext), 0));
        sb.AppendLine(edges.Count > 0 ? Mermaid("graph LR", edges, x => x) : "_No users, databases or external services detected._\n");

        if (ExternalSystems.Count > 0)
        {
            sb.AppendLine("## External systems\n");
            sb.AppendLine("_Detected_: a call in the code shows it. _Inferred_: only a package reference (or a single candidate) points to it. " +
                "_Unknown_: something is there, but the code doesn't say what.\n");
            sb.AppendLine("| System | Kind | Certainty | Used by | Evidence |\n|---|---|---|---|---|");
            foreach (var ext in ExternalSystems)
                sb.AppendLine($"| {ext.Name} | {Externals.Category(ext)} | {Externals.Certainty(ext)} | " +
                    $"{string.Join(", ", Connections[ext.Id].Where(e => nodes.ContainsKey(e.From)).Select(e => nodes[e.From].Name).Distinct().Order())} | " +
                    $"{SourceLink(ext.File, ext.Line, CodeModel.Location(ext), sub: "views")} |");
            sb.AppendLine();
        }

        var packages = model.Nodes.Where(n => n.Kind == "package").GroupBy(n => n.Tags?.FirstOrDefault() ?? "?").ToList();
        if (packages.Count > 0)
        {
            sb.AppendLine("## External packages\n");
            foreach (var g in packages.OrderBy(g => g.Key))
                sb.AppendLine($"- **{g.Key}** ({g.Count()}): {string.Join(", ", g.Select(p => $"`{p.Name}`").Order())}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    string ContainerView()
    {
        var sb = new StringBuilder("# Containers\n\n");
        sb.AppendLine("Separately built/deployed units (projects) and how they talk to each other.\n");
        var code = model.Nodes.Where(n => n.Kind is not ("project" or "package" or "external")).ToList();
        var edges = new List<(string From, string To, string Label)>();
        foreach (var e in model.Edges)
        {
            if (e.Kind == "references") edges.Add((nodes[e.From].Name, nodes.TryGetValue(e.To, out var p) ? p.Name : e.To, "references"));
            if (!CodeModel.DependencyKinds.Contains(e.Kind) || !nodes.TryGetValue(e.From, out var from)) continue;
            if (nodes.TryGetValue(e.To, out var to) && Container(from) != Container(to))
                edges.Add((Container(from), Container(to), e.Kind == "http" ? "HTTP" : "uses"));
        }
        foreach (var ext in ExternalSystems)
            foreach (var e in Connections[ext.Id].Where(e => nodes.ContainsKey(e.From)))
                edges.Add((Container(nodes[e.From]), ExternalLabel(ext), Externals.Category(ext) == "database" ? "reads/writes" : "uses"));
        var grouped = edges.Distinct().ToList();
        if (grouped.Count > 0)
        {
            var ids = new Dictionary<string, string>();
            string Id(string x) => ids.TryGetValue(x, out var id) ? id : ids[x] = $"c{ids.Count}";
            var lines = grouped.Select(e => $"    {Id(e.From)} -->|{e.Label}| {Id(e.To)}").ToList();
            sb.AppendLine("```mermaid\ngraph LR");
            var databases = ExternalSystems.Where(x => Externals.Category(x) == "database").Select(ExternalLabel).ToHashSet();
            foreach (var (x, id) in ids) sb.AppendLine(databases.Contains(x) ? $"    {id}[(\"{x}\")]" : $"    {id}[\"{x.Replace("\"", "'")}\"]");
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine("```\n");
        }

        sb.AppendLine("| Container | Kind | Source files | Packages |\n|---|---|---|---|");
        var depends = model.Edges.Where(e => e.Kind == "depends-on").ToLookup(e => e.From);
        foreach (var g in code.GroupBy(Container).OrderBy(g => g.Key))
        {
            var project = Projects.FirstOrDefault(p => p.Name == g.Key);
            var packages = project is null ? [] : depends[project.Id].Select(e => nodes.GetValueOrDefault(e.To)?.Name ?? e.To).Order().ToList();
            sb.AppendLine($"| {(project is null ? g.Key : SourceLink(project.File, 0, g.Key, sub: "views"))} | {string.Join(", ", project?.Tags ?? [])} | " +
                $"{g.Select(n => n.File).Distinct().Count()} | {(packages.Count > 0 ? string.Join(", ", packages) : "—")} |");
        }
        return sb.ToString();
    }

    string ComponentView()
    {
        var sb = new StringBuilder("# Components\n\n");
        sb.AppendLine("Top-level building blocks by layer and the dependencies between them" +
            $" (the {MaxViewNodes} most connected; every module page has the full picture).\n");
        var deps = Dependencies().Where(d => nodes.ContainsKey(d.To)).Select(d => (From: Top(d.From), To: Top(d.To)))
            .Where(d => d.From != d.To && TopKinds.Contains(nodes[d.From].Kind) && TopKinds.Contains(nodes[d.To].Kind)
                && nodes[d.From].Kind != "module" && nodes[d.To].Kind != "module")
            .Distinct().ToList();
        var degree = deps.SelectMany(d => new[] { d.From, d.To }).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var shown = degree.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(MaxViewNodes).Select(kv => kv.Key).ToHashSet();
        if (shown.Count == 0) return sb.Append("_No dependencies between components found._\n").ToString();

        var ids = shown.Order().Select((x, i) => (x, i)).ToDictionary(p => p.x, p => $"n{p.i}");
        sb.AppendLine("```mermaid\ngraph LR");
        foreach (var layer in shown.GroupBy(x => Layer(Folder(nodes[x].File)) ?? "(no layer)").OrderBy(g => g.Key))
        {
            sb.AppendLine($"    subgraph {Regex.Replace(layer.Key, @"\W", "_")}[\"{layer.Key}\"]");
            foreach (var x in layer.Order()) sb.AppendLine($"        {ids[x]}[\"{nodes[x].Name}\"]");
            sb.AppendLine("    end");
        }
        foreach (var (from, to) in deps.Where(d => shown.Contains(d.From) && shown.Contains(d.To)))
            sb.AppendLine($"    {ids[from]} --> {ids[to]}");
        sb.AppendLine("```\n");

        sb.AppendLine("| Component | Kind | Layer | Depends on | Used by | Summary |\n|---|---|---|---|---|---|");
        foreach (var x in shown.OrderBy(x => Layer(Folder(nodes[x].File))).ThenBy(x => nodes[x].Name))
        {
            var n = nodes[x];
            sb.AppendLine($"| [{n.Name}](../modules/{Slug(Folder(n.File))}.md#{Anchor(x)}) | {n.Kind} | {Layer(Folder(n.File)) ?? "—"} | " +
                $"{string.Join(", ", deps.Where(d => d.From == x).Select(d => nodes[d.To].Name).Order())} | " +
                $"{string.Join(", ", deps.Where(d => d.To == x).Select(d => nodes[d.From].Name).Order())} | {Esc(Summary(n) ?? "")} |");
        }
        return sb.ToString();
    }

    string DataView()
    {
        var sb = new StringBuilder("# Data\n\n");
        var persists = model.Edges.Where(e => e.Kind == "persists").ToLookup(e => e.From, e => e.To);
        var injectedBy = model.Edges.Where(e => e.Kind == "injects").ToLookup(e => e.To, e => e.From);
        if (DbContexts.Count == 0) sb.AppendLine("No database context (EF Core `DbContext`) found.\n");
        else
        {
            sb.AppendLine("Persisted entities, the context that stores them, and the code that accesses that context.\n");
            var edges = new List<(string, string, int)>();
            foreach (var ctx in DbContexts)
            {
                foreach (var a in injectedBy[ctx.Id].Where(nodes.ContainsKey)) edges.Add((nodes[a].Name, ctx.Name, 0));
                foreach (var db in DatabasesOf(ctx)) edges.Add((ctx.Name, ExternalLabel(db), 0));
                foreach (var en in persists[ctx.Id].Where(nodes.ContainsKey)) edges.Add((ctx.Name, nodes[en].Name, 0));
            }
            sb.AppendLine(Mermaid("graph LR", edges.Distinct(), x => x));
            sb.AppendLine("| Entity | Stored via | Database | Accessed by | Summary |\n|---|---|---|---|---|");
            foreach (var ctx in DbContexts)
                foreach (var en in persists[ctx.Id].Where(nodes.ContainsKey).Select(x => nodes[x]))
                    sb.AppendLine($"| {en.Name} | {ctx.Name} | {string.Join(", ", DatabasesOf(ctx).Select(ExternalLabel))} | {string.Join(", ", injectedBy[ctx.Id].Where(nodes.ContainsKey).Select(a => nodes[a].Name).Distinct().Order())} | {Esc(Summary(en) ?? "")} |");
            sb.AppendLine();
        }

        var stores = model.Nodes.Where(n => n.Kind == "store").ToList();
        if (stores.Count > 0)
        {
            var callers = model.Edges.Where(e => e.Kind == "calls").ToLookup(e => e.To, e => e.From);
            var calls = model.Edges.Where(e => e.Kind == "calls").ToLookup(e => e.From, e => e.To);
            sb.AppendLine("## Client-side state\n\n| Store | Used by | Loads from |\n|---|---|---|");
            foreach (var s in stores)
                sb.AppendLine($"| {s.Name} | {string.Join(", ", callers[s.Id].Where(nodes.ContainsKey).Select(c => nodes[Top(c)].Name).Distinct().Order())} | " +
                    $"{string.Join(", ", calls[s.Id].Where(nodes.ContainsKey).Select(c => nodes[c].Name).Distinct().Order())} |");
        }
        return sb.ToString();
    }

    // Deployment is mostly not in the code: report the descriptors that are, and leave the rest to people.
    static readonly string[] DeploymentGlobs = ["Dockerfile*", "*.Dockerfile", "docker-compose*.yml", "docker-compose*.yaml",
        "compose*.yml", "compose*.yaml", "*.bicep", "*.tf", "Chart.yaml", "azure-pipelines.yml", ".gitlab-ci.yml",
        ".github/workflows/*.yml", ".github/workflows/*.yaml", "Procfile", "fly.toml", "vercel.json", "netlify.toml"];

    string DeploymentView()
    {
        var sb = new StringBuilder("# Deployment\n\n");
        var hosts = Projects.Where(p => p.Tags?.Any(t => t.StartsWith("Microsoft.NET.Sdk.Web") || t == "npm") == true).ToList();
        if (hosts.Count > 0)
        {
            sb.AppendLine("## Deployable units (from project files)\n");
            foreach (var h in hosts) sb.AppendLine($"- {SourceLink(h.File, 0, h.Name, sub: "views")} — {(h.Tags!.Contains("npm") ? "frontend (npm)" : "ASP.NET Core web host")}");
            sb.AppendLine();
        }
        var descriptors = DeploymentFiles().ToList();
        sb.AppendLine("## Deployment descriptors\n");
        if (descriptors.Count == 0) sb.AppendLine("None found (Dockerfile, compose, Bicep/Terraform, Helm, CI pipelines).\n");
        foreach (var f in descriptors)
        {
            sb.AppendLine($"- {SourceLink(f, 0, f, sub: "views")}");
            if (Path.GetFileName(f).Contains("compose") && ComposeServices(f) is { Count: > 0 } services)
                sb.AppendLine($"  - services: {string.Join(", ", services)}");
        }
        sb.AppendLine();
        sb.AppendLine(HumanNote("deployment", "Where and how each unit runs (environments, hosting, scaling) is not derivable from code."));
        return sb.ToString();
    }

    IEnumerable<string> DeploymentFiles()
    {
        string[] skip = [".git", "node_modules", "bin", "obj", "dist"];
        var stack = new Stack<string>([root]);
        while (stack.TryPop(out var dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (DeploymentGlobs.Any(g => FileSystemName.MatchesSimpleExpression(g, g.Contains('/') ? rel : Path.GetFileName(f))))
                    yield return rel;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!skip.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }

    // ponytail: top-level keys under `services:` by indentation; use a YAML parser if compose files get exotic.
    List<string> ComposeServices(string rel)
    {
        var result = new List<string>();
        var inServices = false;
        foreach (var line in File.ReadLines(Path.Combine(root, rel)))
        {
            if (Regex.IsMatch(line, @"^\S")) inServices = line.TrimEnd() == "services:";
            else if (inServices && Regex.Match(line, @"^  ([\w.-]+):\s*$") is { Success: true } m) result.Add(m.Groups[1].Value);
        }
        return result;
    }

    // A pointer to the human-authored page for a section: linked when it exists, requested when it doesn't.
    string HumanNote(string section, string why)
    {
        var human = HumanPage(section);
        return human is not null
            ? $"Human-authored: [{section}](../architecture/{section}.md)\n"
            : $"_{why} Write it in `architecture/{section}.md` next to these docs; docwizz links it and never overwrites it._\n";
    }

    string? HumanPage(string section)
    {
        var f = Path.Combine(outDir, "architecture", $"{section}.md");
        return File.Exists(f) && File.ReadLines(f).FirstOrDefault() != Marker ? f : null;
    }
}
