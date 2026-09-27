using System.Text;

// Architecture description structured after ISO/IEC/IEEE 42010 concepts (stakeholders, concerns, viewpoints, views,
// rationale). What the code shows is derived; what it can't show is left to human-authored pages under
// architecture/, which docwizz links but never writes. This is a structure, not a conformance claim.
partial class Generator
{
    public static readonly string[] HumanSections = ["stakeholders", "concerns", "decisions", "deployment", "security"];
    static readonly string[] AdrFolders = ["docs/adr", "doc/adr", "docs/decisions", "adr", "docs/architecture/decisions"];

    string DescriptionPage()
    {
        var sb = new StringBuilder("# Architecture description\n\n");
        sb.AppendLine("Organised after the concepts of ISO/IEC/IEEE 42010. Sections marked _derived_ are generated from the code; " +
            "_human-authored_ sections live in `architecture/` next to these pages and are never overwritten. " +
            "This is a documentation structure, not a claim of conformance with the standard.\n");

        var required = config.ArchitectureSections.Count > 0 ? config.ArchitectureSections : [.. HumanSections];
        sb.AppendLine("## Human-authored sections\n\n| Section | Status |\n|---|---|");
        foreach (var s in required)
            sb.AppendLine($"| {s} | {(HumanPage(s) is not null ? $"[present](architecture/{s}.md)" : "missing")} |");
        sb.AppendLine();

        sb.AppendLine("## Stakeholders and concerns\n");
        sb.AppendLine(HumanLink("stakeholders", "Who has an interest in the system") + HumanLink("concerns", "What they need the architecture to address"));

        sb.AppendLine("## Viewpoints and views\n\n| Viewpoint | Addresses | View |\n|---|---|---|");
        foreach (var (view, concern) in new[] {
            ("[System context](views/context.md)", "scope, external actors and systems"),
            ("[Containers](views/containers.md)", "deployable units and how they communicate"),
            ("[Components](views/components.md)", "building blocks per layer and their dependencies"),
            ("[Dependencies](architecture.md)", "layering rules, violations, cycles"),
            ("[API](api.md)", "interfaces offered to clients"),
            ("[Data](views/data.md)", "persisted entities and state"),
            ("[Deployment](views/deployment.md)", "where the units run") })
            sb.AppendLine($"| {view.Split(']')[0].TrimStart('[')} | {concern} | {view} |");
        sb.AppendLine();

        sb.AppendLine("## Architecture _(derived)_\n");
        if (archConfig.Layers.Count == 0) sb.AppendLine("No layers configured.\n");
        else
        {
            sb.AppendLine("| Layer | Files | May depend on |\n|---|---|---|");
            foreach (var layer in archConfig.Layers.Keys)
                sb.AppendLine($"| {layer} | {arch.LayerFiles.GetValueOrDefault(layer)} | " +
                    $"{(archConfig.Allow.TryGetValue(layer, out var a) ? (a.Count > 0 ? string.Join(", ", a) : "nothing") : "anything")} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Components _(derived)_\n");
        var tops = model.Nodes.Where(n => TopKinds.Contains(n.Kind) && n.Kind != "module" && !parent.ContainsKey(n.Id)).ToList();
        sb.AppendLine(string.Join(", ", tops.GroupBy(n => Layer(Folder(n.File)) ?? "no layer").OrderBy(g => g.Key)
            .Select(g => $"{g.Key}: {g.Count()}")) + $" — see [components](views/components.md).\n");

        sb.AppendLine("## Interfaces _(derived)_\n");
        var endpoints = model.Nodes.Where(n => n.Tags?.Contains("endpoint") == true).ToList();
        var interfaces = tops.Where(n => n.Kind == "interface").ToList();
        sb.AppendLine($"- {endpoints.Count} HTTP endpoints — see [API](api.md)");
        if (interfaces.Count > 0) sb.AppendLine($"- {interfaces.Count} code interfaces: {string.Join(", ", interfaces.Select(i => $"`{i.Name}`").Order())}");
        var routes = model.Nodes.Count(n => n.Kind == "route");
        if (routes > 0) sb.AppendLine($"- {routes} frontend routes — see [frontend](frontend.md)");
        sb.AppendLine();

        sb.AppendLine("## Dependencies _(derived)_\n");
        sb.AppendLine($"{arch.LayerDependencies.Count} layer dependencies ({arch.LayerDependencies.Count(d => !d.Allowed)} not allowed), " +
            $"{arch.Violations.Count} violations, {arch.Cycles.Count} module cycles, {model.Nodes.Count(n => n.Kind == "package")} external packages — " +
            "see [dependencies](architecture.md) and [context](views/context.md).\n");

        sb.AppendLine("## Data flow _(derived)_\n");
        var flow = arch.LayerDependencies.Where(d => d.Allowed).Select(d => (d.From, d.To, d.Count)).ToList();
        sb.AppendLine(flow.Count > 0 ? "Allowed dependencies actually used, i.e. the paths requests and data take through the layers.\n\n" +
            Mermaid("graph TD", flow, x => x) : "No layer dependencies found.\n");

        sb.AppendLine("## Deployment\n");
        sb.AppendLine("_Derived:_ [deployment view](views/deployment.md).\n");
        sb.AppendLine(HumanLink("deployment", "Environments, hosting and operations"));

        sb.AppendLine("## Security\n");
        var auth = endpoints.GroupBy(e => e.Tags!.Contains("anonymous") ? "anonymous" : e.Tags.Contains("authorize") ? "authorization required" : "none declared")
            .OrderBy(g => g.Key).ToList();
        if (auth.Count > 0)
        {
            sb.AppendLine("_Derived:_ endpoint authorization as declared in code — " + string.Join(", ", auth.Select(g => $"{g.Key}: {g.Count()}")) + ".\n");
            var open = auth.FirstOrDefault(g => g.Key == "none declared")?.ToList() ?? [];
            if (open.Count > 0) sb.AppendLine("Endpoints without declared authorization: " + string.Join(", ", open.Select(e => $"`{EndpointLabel(e)}`").Order()) + "\n");
        }
        sb.AppendLine(HumanLink("security", "Threat model, authentication scheme, data protection"));

        sb.AppendLine("## Architecture decisions\n");
        var adrs = AdrFolders.Select(f => Path.Combine(root, f)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.md")).Order().ToList();
        foreach (var adr in adrs) sb.AppendLine($"- {SourceLink(Path.GetRelativePath(root, adr).Replace('\\', '/'), 0, Path.GetFileNameWithoutExtension(adr))}");
        if (adrs.Count > 0) sb.AppendLine();
        sb.AppendLine(HumanLink("decisions", "Decisions and their rationale (or keep ADRs in docs/adr/)"));
        return sb.ToString();
    }

    string HumanLink(string section, string what) => HumanPage(section) is not null
        ? $"_Human-authored:_ [{section}](architecture/{section}.md)\n\n"
        : $"_Human-authored, missing:_ {what} — write `architecture/{section}.md`.\n\n";
}
