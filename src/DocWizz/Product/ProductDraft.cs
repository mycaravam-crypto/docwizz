using System.Text;

// An evidence-only draft. Known section IDs are reserved by schema v1; other sections remain open.
internal static class ProductDraft
{
    const int MaxFacts = 40;

    // The marker of a required section without evidence. Earlier drafts said OFFEN; both are recognized where it matters.
    public const string Open = "OPEN";
    internal const string VModelDisclaimer = "Draft – no statement of V-Modell XT conformity or approval.";
    internal const string OperationsDisclaimer = "Draft – no statement of operational readiness or approval.";
    internal const string PublishedVModelDisclaimer = "Published after signature verification – no statement of V-Modell XT conformity.";
    internal const string PublishedOperationsDisclaimer = "Published after signature verification – no statement of operational readiness.";

    // One piece of evidence for a section: a code fact (detected) or a project statement (human-maintained), where it is
    // from, and a document-wide id (`E1`, `E2`, ...) that synthesized sentences cite.
    // Line: how the deterministic draft shows it (unchanged by ids or synthesis).
    internal sealed record Evidence(string Id, string Kind, string Text, string Source, string Line);

    // Every section with its evidence, in template order; ids are numbered across the document, so the same inputs give
    // the same ids.
    public static List<(ProductSection Section, List<Evidence> Evidence)> Catalog(ProductTemplate template, CodeModel? model = null, ProjectContext? project = null)
    {
        project?.Validate(template);
        var result = new List<(ProductSection, List<Evidence>)>();
        var next = 0;
        foreach (var section in template.Sections)
        {
            var items = model is not null && section.Sources.Contains("code") ? Facts(section.Id, model) : [];
            if (project is not null && section.Sources.Contains("project"))
                items.AddRange(project.Statements.Where(x => x.SectionId == section.Id)
                    .Select(x => ("project", Escape(x.Text), x.Source, $"{Escape(x.Text)} (source: `{x.Source}`)")));
            result.Add((section, [.. items.Select(x => new Evidence($"E{++next}", x.Kind, x.Text, x.Source, x.Line))]));
        }
        return result;
    }

    // Render a template without inventing project intent or statements of compliance. With `synthesis` (validated AI
    // sentences per section, ProductSynthesis), each section opens with them, marked as a draft, and every piece of
    // evidence shows the id the sentences cite. Without it, the output is the deterministic draft.
    public static string Render(ProductTemplate template, CodeModel? model = null, ProjectContext? project = null,
        IReadOnlyDictionary<string, List<ProductSynthesis.Sentence>>? synthesis = null, bool controlled = false)
    {
        var output = new StringBuilder();
        if (!controlled)
        {
            output.AppendLine("# " + template.ProductId);
            output.AppendLine();
        }
        output.AppendLine(template.IsVModel
            ? $"V-Modell: {template.XtVariant} {template.XtVersion}; Tailoring: {template.Tailoring}"
            : $"Document type: {template.DocumentType}");
        output.AppendLine($"Product dependencies: {(template.Dependencies.Count == 0 ? "none declared" : string.Join(", ", template.Dependencies))}");
        if (model is not null) output.AppendLine($"Code baseline: {model.Commit ?? "unknown"}");
        output.AppendLine();
        output.AppendLine(template.IsVModel
            ? "> " + VModelDisclaimer
            : "> " + OperationsDisclaimer);
        if (synthesis is not null)
            output.AppendLine(">\n> 🤖 AI draft: sentences marked [E…] were worded by a local model from the cited evidence and have not been reviewed. The evidence is authoritative.");
        output.AppendLine();
        foreach (var (section, evidence) in Catalog(template, model, project))
        {
            if (controlled) output.AppendLine($"<a id=\"{section.Id}\"></a>");
            output.AppendLine("## " + section.Title);
            output.AppendLine();
            if (evidence.Count == 0)
            {
                output.AppendLine(section.Required ? $"{Open} – needs a source and expert review." : "Optional – no evidenced content.");
                output.AppendLine();
                continue;
            }
            if (synthesis?.GetValueOrDefault(section.Id) is { Count: > 0 } sentences)
            {
                output.AppendLine("🤖 " + string.Join(" ", sentences.Select(x => $"{x.Text} [{string.Join(", ", x.Evidence)}]")));
                output.AppendLine();
            }
            foreach (var e in evidence)
                output.AppendLine($"- {(synthesis is null ? "" : $"[{e.Id}] ")}{e.Line}");
            output.AppendLine();
        }
        return output.ToString();
    }

    // The machine-readable evidence and gap report next to the draft: per section whether it is filled from evidence, or
    // open (required, no evidence) or empty (optional), with every piece of evidence and any synthesized sentence and
    // the ids it cites. Nothing in it is a statement of review, approval or conformity, and the report says so.
    public static string Report(ProductTemplate template, CodeModel? model = null, ProjectContext? project = null,
        IReadOnlyDictionary<string, List<ProductSynthesis.Sentence>>? synthesis = null)
    {
        var sections = Catalog(template, model, project).Select(c => new
        {
            id = c.Section.Id, title = c.Section.Title, required = c.Section.Required, sources = c.Section.Sources,
            status = c.Evidence.Count > 0 ? "evidenced" : c.Section.Required ? "open" : "empty",
            evidence = c.Evidence.Select(e => new { id = e.Id, kind = e.Kind, origin = e.Kind == "code" ? "detected" : "human", text = e.Text, source = e.Source }),
            synthesized = synthesis?.GetValueOrDefault(c.Section.Id)?.Select(x => new { text = x.Text, evidence = x.Evidence, origin = "ai-drafted" }),
        }).ToList();
        var required = sections.Where(x => x.required).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            generator = "docwizz product",
            product = template.ProductId, document_type = template.DocumentType,
            variant = template.IsVModel ? template.XtVariant : null,
            version = template.IsVModel ? template.XtVersion : null,
            tailoring = template.IsVModel ? template.Tailoring : null,
            dependencies = template.Dependencies,
            commit = model?.Commit,
            note = template.IsVModel
                ? "evidence inventory and gaps of a draft; not a statement of review, approval or V-Modell XT conformity"
                : "evidence inventory and gaps of a draft; not a statement of review, approval or operational readiness",
            required = new { total = required.Count, evidenced = required.Count(x => x.status == "evidenced"), open = required.Where(x => x.status == "open").Select(x => x.id) },
            sections,
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n") + "\n";
    }

    static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);

    static List<(string Kind, string Text, string Source, string Line)> Facts(string sectionId, CodeModel model)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var childIds = model.Edges.Where(e => e.Kind == "contains").Select(e => e.To).ToHashSet();
        // CodeModel provided by the normal scan excludes test declarations; ignore explicit test projects as well.
        var allowed = nodes.Values.Where(n => n.Tags?.Contains("test") != true).ToDictionary(n => n.Id);
        if (sectionId == "runtime")
        {
            // Project manifests describe target runtimes, not the servers on which software actually runs.
            return allowed.Values.Where(n => n.Kind == "project")
                .OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal)
                .Take(MaxFacts).Select(n =>
                {
                    var role = (n.Tags ?? []).FirstOrDefault(t => t is "executable" or "library" or "test") ?? "unknown";
                    var targets = (n.Tags ?? []).Where(t => t.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                        && t.Any(char.IsDigit)).Order(StringComparer.Ordinal).ToList();
                    var label = $"Project: {Escape(n.Name)} (role: {role}"
                        + (targets.Count == 0 ? ")" : $"; target framework: {string.Join(", ", targets)})");
                    return ("code", label, CodeModel.Location(n), $"{label} (source: {CodeModel.Location(n)})");
                }).ToList();
        }
        if (sectionId == "configuration")
        {
            // Configuration nodes contain only keys; never emit tags, which may contain URL hosts.
            return allowed.Values.Where(n => n.Kind == "config")
                .OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.File, StringComparer.Ordinal)
                .Take(MaxFacts).Select(n => ("code", $"Configuration key: {Escape(n.Name)}", CodeModel.Location(n),
                    $"Configuration key: {Escape(n.Name)} (source: {CodeModel.Location(n)})"))
                .ToList();
        }
        if (sectionId == "deployment-descriptors")
        {
            // Only manifest *presence* is detected; no claim that services were deployed or rollback was tested.
            return allowed.Values.Where(n => n.Kind == "deployment-descriptor")
                .OrderBy(n => n.File, StringComparer.Ordinal)
                .Take(MaxFacts).Select(n => ("code", $"Found in the repository: {Escape(n.Name)} – `{Escape(n.File)}`",
                    CodeModel.Location(n), $"Found in the repository: {Escape(n.Name)} – `{Escape(n.File)}` (source: `{CodeModel.Location(n)}`)"))
                .ToList();
        }
        if (sectionId == "structure")
        {
            return allowed.Values.Where(n => Generator.TopKinds.Contains(n.Kind) && n.Kind != "module" && !childIds.Contains(n.Id))
                .OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal)
                .Take(MaxFacts).Select(n => ("code", $"{n.Kind}: `{n.Name}`", CodeModel.Location(n), $"{n.Kind}: `{n.Name}` (source: `{CodeModel.Location(n)}`)"))
                .ToList();
        }
        if (sectionId == "interfaces")
        {
            return model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && allowed.ContainsKey(e.From) && allowed.ContainsKey(e.To))
                .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
                .Take(MaxFacts)
                .Select(e => ("code", $"`{allowed[e.From].Name}` → `{allowed[e.To].Name}` ({e.Kind})", CodeModel.Location(allowed[e.From]),
                    $"`{allowed[e.From].Name}` → `{allowed[e.To].Name}` ({e.Kind}; source: `{CodeModel.Location(allowed[e.From])}`)"))
                .ToList();
        }
        return [];
    }
}
