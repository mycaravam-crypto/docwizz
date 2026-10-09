using System.Text;

// Deterministic draft skeleton: a product section never becomes a factual assertion without evidence.
internal static class ProductDraft
{
    public static string Render(ProductTemplate template)
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
            output.AppendLine("OFFEN – Quelle und fachliche Prüfung erforderlich.");
            output.AppendLine();
        }
        return output.ToString();
    }
}
