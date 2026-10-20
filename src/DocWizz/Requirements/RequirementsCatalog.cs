using System.Text.Json;
using System.Text;

// Offline requirements catalog import; deliberately separate from commit-derived requirement history.
static class RequirementsCatalog
{
    const string InputSchema = "ado.workitem-catalog/v1";
    public const string FileName = "docs/.docwizz/workitem-catalog.json";

    public static int Import(string root, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("catalog-import requires a JSON file");
        var path = File.Exists(source) ? source : Path.Combine(root, source);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("schema", out var schema)
            || schema.GetString() != InputSchema)
            throw new ArgumentException("expected ado.workitem-catalog/v1");
        if (!obj.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("catalog is missing items array");
        if (!obj.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new ArgumentException("catalog is incomplete; refusing a misleading requirements baseline");
        var ids = new HashSet<int>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number <= 0 || !ids.Add(number))
                throw new ArgumentException("invalid or duplicate work item id");
            foreach (var name in new[] { "title", "type", "state", "area" })
                if (!item.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"work item {number} missing {name}");
        }
        var target = Path.Combine(root, FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var canonical = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (!File.Exists(target) || File.ReadAllText(target) != canonical) File.WriteAllText(target, canonical);
        Render(root, obj);
        Console.WriteLine($"Imported {ids.Count} requirements from {path}; work-item catalog is independent of GitWizz history.");
        return 0;
    }

    // Static, escaped Markdown inventory; no claims about unverified implementation or tests.
    static void Render(string root, JsonElement catalog)
    {
        var path = Path.Combine(root, "docs", "requirements", "area-catalog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder("# Area requirements catalog\n\n");
        sb.AppendLine("Source: Azure DevOps area-path export. Work-item state is not evidence of implementation or test completion.\n");
        sb.AppendLine("| ID | Type | Parent | Title | State | Area | Code evidence | Test evidence |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        var rows = catalog.GetProperty("items").EnumerateArray().OrderBy(x => x.GetProperty("id").GetInt32());
        foreach (var item in rows)
        {
            static string Read(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()!.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;") : "";
            string parent = "";
            if (item.TryGetProperty("relations", out var links) && links.ValueKind == JsonValueKind.Array)
                parent = links.EnumerateArray().Where(e => Read(e, "kind") == "parent")
                    .Select(e => e.GetProperty("id").ToString()).FirstOrDefault() ?? "";
            sb.AppendLine($"| {item.GetProperty("id")} | {Read(item,"type")} | {parent} | {Read(item,"title")} | {Read(item,"state")} | {Read(item,"area")} | Not assessed | Not assessed |");
        }
        var text = sb.ToString();
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
    }
}
