using System.Text;

// Deterministic product draft. Only explicitly supported sections consume verified CodeModel facts.
internal static class ProductDraft
{
    // Render sections from a validated template; unsupported claims remain visibly open.
    public static string Render(ProductTemplate template, CodeModel? model = null)
    {
        var output = new StringBuilder();
        output.AppendLine("# " + template.ProductId);
        output.AppendLine();
        output.AppendLine("> Entwurf – keine V-Modell-XT-Konformitäts- oder Freigabeaussage.");
        output.AppendLine();
        foreach (var section in template.Sections)
        {
            output.AppendLine("## " + section.Title);
            output.AppendLine();
            var facts = model is not null && section.Sources.Contains("code")
                ? Facts(section.Id, model) : [];
            if (facts.Count == 0)
                output.AppendLine("OFFEN – Quelle und fachliche Prüfung erforderlich.");
            else
                foreach (var fact in facts) output.AppendLine(fact);
            output.AppendLine();
        }
        return output.ToString();
    }

    static List<string> Facts(string sectionId, CodeModel model)
    {
        // Explicit mappings only. An arbitrary template heading must not trigger guessed facts.
        if (sectionId == "structure")
            return model.Nodes.Where(n => n.Kind is "class" or "interface" or "component" or "module")
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .Select(n => $"- {n.Kind}: `{n.Name}` ([Quelle]({n.File}#L{n.Line}))")
                .ToList();

        if (sectionId == "interfaces")
        {
            var nodes = model.Nodes.ToDictionary(n => n.Id);
            return model.Edges.Where(e => e.Kind is "implements" or "http" or "references")
                .Where(e => nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To))
                .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
                .Select(e => $"- `{nodes[e.From].Name}` → `{nodes[e.To].Name}` ({e.Kind}; [Quelle]({nodes[e.From].File}#L{nodes[e.From].Line}))")
                .ToList();
        }
        return [];
    }
}
