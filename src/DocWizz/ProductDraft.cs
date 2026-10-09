using System.Text;

// An evidence-only draft. Known section IDs are reserved by schema v1; other sections remain open.
internal static class ProductDraft
{
    const int MaxFacts = 40;

    // Render a template without inventing project intent or statements of compliance.
    public static string Render(ProductTemplate template, CodeModel? model = null, ProjectContext? project = null)
    {
        project?.Validate(template);
        var output = new StringBuilder();
        output.AppendLine("# " + template.ProductId);
        output.AppendLine();
        output.AppendLine($"V-Modell: {template.XtVariant} {template.XtVersion}; Tailoring: {template.Tailoring}");
        output.AppendLine($"Produktabhängigkeiten: {(template.Dependencies.Count == 0 ? "keine angegeben" : string.Join(", ", template.Dependencies))}");
        if (model is not null) output.AppendLine($"Code-Stand: {model.Commit ?? "unbekannt"}");
        output.AppendLine();
        output.AppendLine("> Entwurf – keine V-Modell-XT-Konformitäts- oder Freigabeaussage.");
        output.AppendLine();
        foreach (var section in template.Sections)
        {
            output.AppendLine("## " + section.Title);
            output.AppendLine();
            var facts = model is not null && section.Sources.Contains("code") ? Facts(section.Id, model) : [];
            if (project is not null && section.Sources.Contains("project"))
                facts.AddRange(project.Statements.Where(x => x.SectionId == section.Id)
                    .Select(x => $"- {Escape(x.Text)} (Quelle: `{x.Source}`)"));
            if (facts.Count == 0)
                output.AppendLine(section.Required ? "OFFEN – Quelle und fachliche Prüfung erforderlich." : "Optional – keine belegten Angaben.");
            else
                foreach (var fact in facts) output.AppendLine(fact);
            output.AppendLine();
        }
        return output.ToString();
    }

    static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("&#", "&amp;#", StringComparison.Ordinal)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);

    static List<string> Facts(string sectionId, CodeModel model)
    {
        var nodes = model.Nodes.ToDictionary(n => n.Id);
        var childIds = model.Edges.Where(e => e.Kind == "contains").Select(e => e.To).ToHashSet();
        // CodeModel provided by the normal scan excludes test declarations; ignore explicit test projects as well.
        var allowed = nodes.Values.Where(n => n.Tags?.Contains("test") != true).ToDictionary(n => n.Id);
        if (sectionId == "structure")
        {
            return allowed.Values.Where(n => Generator.TopKinds.Contains(n.Kind) && n.Kind != "module" && !childIds.Contains(n.Id))
                .OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal)
                .Take(MaxFacts).Select(n => $"- {n.Kind}: `{n.Name}` (Quelle: `{CodeModel.Location(n)}`)")
                .ToList();
        }
        if (sectionId == "interfaces")
        {
            return model.Edges.Where(e => CodeModel.DependencyKinds.Contains(e.Kind) && allowed.ContainsKey(e.From) && allowed.ContainsKey(e.To))
                .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
                .Take(MaxFacts)
                .Select(e => $"- `{allowed[e.From].Name}` → `{allowed[e.To].Name}` ({e.Kind}; Quelle: `{CodeModel.Location(allowed[e.From])}`)")
                .ToList();
        }
        return [];
    }
}
