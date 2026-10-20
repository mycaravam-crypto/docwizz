using System.Text.Json;
using System.Text;

// Offline work-item inventory; never conflates organizational area paths with explicit parent/child links.
static class RequirementsCatalog
{
    public const string FileName = "docs/.docwizz/workitem-catalog.json";
    const string InputSchema = "ado.workitem-catalog/v1";

    /// <summary>Import a complete, versioned Azure DevOps work-item catalog and write source-backed requirement pages.</summary>
    public static int Import(string root, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("catalog-import requires a JSON file");
        // An explicitly supplied absolute source path is intentional; joining must never drop the repository root.
        var path = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Join(root, source));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Validate(doc.RootElement);
        var target = Path.Join(Path.GetFullPath(root), "docs", ".docwizz", "workitem-catalog.json");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("invalid catalog destination");
        Directory.CreateDirectory(parent);
        var canonical = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (!File.Exists(target) || File.ReadAllText(target) != canonical) File.WriteAllText(target, canonical);
        Render(doc.RootElement, Path.Join(Path.GetFullPath(root), "docs"));
        Console.WriteLine($"Imported {doc.RootElement.GetProperty("items").GetArrayLength()} requirements from {path}; independent of GitWizz history.");
        return 0;
    }

    /// <summary>Regenerate area requirements pages from the stored catalog, without external service access.</summary>
    public static void RenderIfPresent(string root, string output)
    {
        var input = Path.Join(Path.GetFullPath(root), "docs", ".docwizz", "workitem-catalog.json");
        if (!File.Exists(input)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(input));
        Validate(doc.RootElement);
        Render(doc.RootElement, Path.GetFullPath(output));
    }

    // Reject malformed or incomplete snapshots before writing anything to the repository.
    static void Validate(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.String || schema.GetString() != InputSchema)
            throw new ArgumentException("expected ado.workitem-catalog/v1");
        if (!catalog.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new ArgumentException("catalog is incomplete; refusing a misleading requirements baseline");
        foreach (var name in new[] { "project", "area", "source" })
            if (!catalog.TryGetProperty(name, out var val) || val.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"catalog is missing {name}");
        if (!catalog.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("catalog is missing items array");
        var ids = new HashSet<int>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                !id.TryGetInt32(out var number) || number <= 0 || !ids.Add(number))
                throw new ArgumentException("invalid or duplicate work item id");
            foreach (var name in new[] { "title", "type", "state", "area", "iteration" })
                if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"work item {number} missing {name}");
            if (!item.TryGetProperty("relations", out var links) || links.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"work item {number} missing relations");
            foreach (var link in links.EnumerateArray())
            {
                if (link.ValueKind != JsonValueKind.Object || !link.TryGetProperty("kind", out var kind)
                    || kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("parent" or "child" or "related")
                    || !link.TryGetProperty("id", out var linked) || !linked.TryGetInt32(out var linkId) || linkId <= 0)
                    throw new ArgumentException($"work item {number} has an invalid relation");
            }
        }
    }

    static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("|", "&#124;").Replace("\r", " ").Replace("\n", " ");
    static string Multiline(string? text) => string.Join("\n", (text ?? "").Replace("\r\n", "\n")
        .Split('\n').Select(line => "> " + Escape(line)));
    static string Val(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Escape(value.GetString()) : "";
    static string Parent(JsonElement item) =>
        item.GetProperty("relations").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "parent")
            .Select(e => e.GetProperty("id").GetInt32().ToString()).FirstOrDefault() ?? "";

    static void Write(string path, string content)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("invalid documentation destination");
        Directory.CreateDirectory(parent);
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);
    }

    static void Render(JsonElement catalog, string output)
    {
        var directory = Path.Join(output, "requirements");
        var index = new StringBuilder("# Area requirements catalog\n\n");
        index.AppendLine($"Project: {Escape(catalog.GetProperty("project").GetString())}");
        index.AppendLine($"Area: {Escape(catalog.GetProperty("area").GetString())}\n");
        index.AppendLine("Inventory from Azure DevOps. Work-item state and missing links do not prove implementation or test status.\n");
        index.AppendLine("| ID | Type | Parent | Title | State | Area | Code evidence | Test evidence |");
        index.AppendLine("|---|---|---|---|---|---|---|---|");
        var items = catalog.GetProperty("items").EnumerateArray().OrderBy(e => e.GetProperty("id").GetInt32()).ToArray();
        foreach (var item in items)
        {
            var id = item.GetProperty("id").GetInt32();
            index.AppendLine($"| [{id}](items/{id}.md) | {Val(item,"type")} | {Parent(item)} | {Val(item,"title")} | {Val(item,"state")} | {Val(item,"area")} | Not assessed | Not assessed |");
            var page = new StringBuilder($"# {Escape(item.GetProperty("title").GetString())}\n\n");
            page.AppendLine($"- Work item: {id}");
            page.AppendLine($"- Type: {Val(item,"type")}");
            page.AppendLine($"- State: {Val(item,"state")}");
            page.AppendLine($"- Area: {Val(item,"area")}");
            page.AppendLine($"- Iteration: {Val(item,"iteration")}");
            page.AppendLine($"- Parent: {Parent(item)}\n");
            page.AppendLine("## Description\n");
            page.AppendLine(Multiline(item.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? description.GetString() : null) + "\n");
            page.AppendLine("## Acceptance criteria\n");
            if (item.TryGetProperty("criteria", out var criteria) && criteria.ValueKind == JsonValueKind.Array)
                foreach (var (criterion, number) in criteria.EnumerateArray().Select((v, i) => (v, i + 1)))
                    if (criterion.ValueKind == JsonValueKind.String)
                    {
                        var lines = (criterion.GetString() ?? "").Replace("\r\n", "\n").Split('\n');
                        page.AppendLine($"{number}. {Escape(lines[0])}");
                        foreach (var line in lines.Skip(1)) page.AppendLine("   " + Escape(line));
                    }
            page.AppendLine("\nImplementation and test evidence: not assessed by this catalog.");
            Write(Path.Join(directory, "items", id + ".md"), page.ToString() + "\n");
        }
        Write(Path.Join(directory, "area-catalog.md"), index.ToString());
    }
}
