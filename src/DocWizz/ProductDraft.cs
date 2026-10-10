using System.Text;

// An evidence-only draft. Known section IDs are reserved by schema v1; other sections remain open.
internal static class ProductDraft
{
    const int MaxFacts = 40;

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
                    .Select(x => ("project", Escape(x.Text), x.Source, $"{Escape(x.Text)} (Quelle: `{x.Source}`)")));
            result.Add((section, [.. items.Select(x => new Evidence($"E{++next}", x.Kind, x.Text, x.Source, x.Line))]));
        }
        return result;
    }

    // Render a template without inventing project intent or statements of compliance. With `synthesis` (validated AI
    // sentences per section, ProductSynthesis), each section opens with them, marked as a draft, and every piece of
    // evidence shows the id the sentences cite. Without it, the output is the deterministic draft.
    public static string Render(ProductTemplate template, CodeModel? model = null, ProjectContext? project = null,
        IReadOnlyDictionary<string, List<ProductSynthesis.Sentence>>? synthesis = null)
    {
        var output = new StringBuilder();
        output.AppendLine("# " + template.ProductId);
        output.AppendLine();
        output.AppendLine($"V-Modell: {template.XtVariant} {template.XtVersion}; Tailoring: {template.Tailoring}");
        output.AppendLine($"Produktabhängigkeiten: {(template.Dependencies.Count == 0 ? "keine angegeben" : string.Join(", ", template.Dependencies))}");
        if (model is not null) output.AppendLine($"Code-Stand: {model.Commit ?? "unbekannt"}");
        output.AppendLine();
        output.AppendLine("> Entwurf – keine V-Modell-XT-Konformitäts- oder Freigabeaussage.");
        if (synthesis is not null)
            output.AppendLine(">\n> 🤖 KI-Entwurf: Sätze mit [E…] sind von einem lokalen Modell aus den genannten Belegen formuliert und nicht geprüft. Maßgeblich sind die Belege.");
        output.AppendLine();
        foreach (var (section, evidence) in Catalog(template, model, project))
        {
            output.AppendLine("## " + section.Title);
            output.AppendLine();
            if (evidence.Count == 0)
            {
                output.AppendLine(section.Required ? "OFFEN – Quelle und fachliche Prüfung erforderlich." : "Optional – keine belegten Angaben.");
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
        if (sectionId == "structure")
        {
            return allowed.Values.Where(n => Generator.TopKinds.Contains(n.Kind) && n.Kind != "module" && !childIds.Contains(n.Id))
                .OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal)
                .Take(MaxFacts).Select(n => ("code", $"{n.Kind}: `{n.Name}`", CodeModel.Location(n), $"{n.Kind}: `{n.Name}` (Quelle: `{CodeModel.Location(n)}`)"))
                .ToList();
        }
        if (sectionId == "interfaces")
        {
            return model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && allowed.ContainsKey(e.From) && allowed.ContainsKey(e.To))
                .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
                .Take(MaxFacts)
                .Select(e => ("code", $"`{allowed[e.From].Name}` → `{allowed[e.To].Name}` ({e.Kind})", CodeModel.Location(allowed[e.From]),
                    $"`{allowed[e.From].Name}` → `{allowed[e.To].Name}` ({e.Kind}; Quelle: `{CodeModel.Location(allowed[e.From])}`)"))
                .ToList();
        }
        return [];
    }
}
