using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

// Metadata for documented information. This is a declared record, not a verified approval,
// access-control mechanism, or statement of ISO 9001/27001 conformity.
internal sealed record DocumentChange(string Version, string Date, string Author, string Description);
internal sealed record DocumentApproval(string Kind, string Role, string Person, string Date, string Evidence);
internal sealed record ControlledDocument(
    string Id, string Title, string Version, string Author, string Owner, string Created,
    string Classification, string Distribution, string Retention,
    IReadOnlyList<DocumentChange> Changes, IReadOnlyList<DocumentApproval> Approvals, string Hash)
{
    const int MaxMetadataFieldLength = 500; // bounded Markdown cover/table metadata, not full document prose
    static readonly HashSet<string> RootKeys =
        ["schema", "document_id", "title", "version", "author", "owner", "created",
         "classification", "distribution", "retention", "changes", "approvals"];
    static readonly HashSet<string> ChangeKeys = ["version", "date", "author", "description"];
    static readonly HashSet<string> ApprovalKeys = ["kind", "role", "person", "date", "evidence"];

    public static ControlledDocument Load(string path) => Parse(File.ReadAllText(path), path);

    public static ControlledDocument Parse(string content, string file = "document-control.yaml")
    {
        try { return ParseCore(content); }
        catch (ArgumentException e) { throw new ArgumentException($"{file}: {e.Message}", e); }
    }

    static ControlledDocument ParseCore(string content)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(content);
        try { yaml.Load(reader); }
        catch (YamlDotNet.Core.YamlException e) { throw new ArgumentException($"invalid YAML: {e.Message}", e); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("expected one YAML mapping");
        Keys(root, RootKeys, "control");
        // Both schema: 1 and schema: "1" are valid; the value, not YAML quoting, selects the contract.
        if (Field(root, "schema", "control") != "1")
            throw new ArgumentException("schema must be 1");

        var id = Field(root, "document_id", "control");
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$"))
            throw new ArgumentException("document_id must be 2-80 safe characters");
        var version = Field(root, "version", "control");
        var changes = new List<DocumentChange>();
        foreach (var (node, index) in Items(root, "changes", required: true).Select((x, i) => (x, i)))
        {
            var key = $"changes[{index}]";
            var row = Map(node, key);
            Keys(row, ChangeKeys, key);
            changes.Add(new(Field(row, "version", key), Date(Field(row, "date", key), key),
                Field(row, "author", key), Field(row, "description", key)));
        }
        if (!changes.Any(x => x.Version == version))
            throw new ArgumentException("changes must contain current version");

        var approvals = new List<DocumentApproval>();
        foreach (var (node, index) in Items(root, "approvals", required: false).Select((x, i) => (x, i)))
        {
            var key = $"approvals[{index}]";
            var row = Map(node, key);
            Keys(row, ApprovalKeys, key);
            var kind = Field(row, "kind", key);
            if (kind is not ("review" or "approval"))
                throw new ArgumentException($"{key}.kind must be review or approval");
            var evidence = Field(row, "evidence", key);
            if (!Regex.IsMatch(evidence, @"^docs/[A-Za-z0-9._/-]+\.md$") || evidence.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException($"{key}.evidence must be a safe docs/*.md path");
            approvals.Add(new(kind, Field(row, "role", key), Field(row, "person", key),
                Date(Field(row, "date", key), key), evidence));
        }
        // The evidence fingerprint represents YAML content, not the host platform's line ending convention.
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return new(id, Field(root, "title", "control"), version,
            Field(root, "author", "control"), Field(root, "owner", "control"),
            Date(Field(root, "created", "control"), "created"),
            Field(root, "classification", "control"), Field(root, "distribution", "control"),
            Field(root, "retention", "control"), changes, approvals, hash);
    }

    static YamlMappingNode Map(YamlNode node, string key) =>
        node as YamlMappingNode ?? throw new ArgumentException($"{key} must be a mapping");

    static IEnumerable<YamlNode> Items(YamlMappingNode map, string key, bool required)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
        {
            if (required) throw new ArgumentException($"{key} must be a list");
            return [];
        }
        return node is YamlSequenceNode seq ? seq.Children : throw new ArgumentException($"{key} must be a list");
    }

    static string Field(YamlMappingNode map, string name, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(name), out var node) ||
            node is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{key}.{name} is required");
        // Metadata is rendered in Markdown table cells and cover fields, never as free-form prose.
        // Bound cells to prevent oversized documents; require one line to preserve table integrity.
        // Detailed policies and decisions belong in versioned, cited docs rather than inline metadata.
        if (value.Length > MaxMetadataFieldLength || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException($"{key}.{name} must be one line (max. {MaxMetadataFieldLength} chars)");
        return value.Trim();
    }

    static string Date(string date, string key)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException($"{key}: date must be yyyy-MM-dd");
        return date;
    }

    static void Keys(YamlMappingNode node, HashSet<string> known, string key)
    {
        foreach (var entry in node.Children.Keys)
            if (entry is not YamlScalarNode { Value: { } name } || !known.Contains(name))
                throw new ArgumentException($"{key}: unknown key {entry}");
    }

    // Escape untrusted metadata before interpolating it into Markdown headings, tables or list items.
    static string Esc(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    // Logical cover, document control and ToC in Markdown, not physical pages or a Word ToC.
    public string Render(ProductTemplate template, string draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + Esc(Title));
        sb.AppendLine();
        sb.AppendLine($"> Gelenkter **Entwurf** ({Esc(Id)}, Revision {Esc(Version)}). DocWizz bestätigt keine Freigabe oder Normkonformität.");
        sb.AppendLine();
        sb.AppendLine("## Deckblatt");
        sb.AppendLine();
        sb.AppendLine("| Merkmal | Wert |");
        sb.AppendLine("|---|---|");
        foreach (var (name, value) in new (string, string)[] {
            ("Dokument-ID", Id), ("Produkt", template.ProductId), ("Titel", Title),
            ("Revision", Version), ("Status", "Entwurf – Genehmigung nicht verifiziert"),
            ("Verfasser", Author), ("Dokumentverantwortung", Owner),
            ("Erstellungsdatum", Created), ("Klassifizierung (Deklaration)", Classification) })
            sb.AppendLine($"| {name} | {Esc(value)} |");
        sb.AppendLine();
        sb.AppendLine("## Dokumentenlenkung");
        sb.AppendLine();
        sb.AppendLine("### Änderungsstand");
        sb.AppendLine();
        sb.AppendLine("| Revision | Datum | Autor | Beschreibung |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in Changes)
            sb.AppendLine($"| {Esc(c.Version)} | {c.Date} | {Esc(c.Author)} | {Esc(c.Description)} |");
        sb.AppendLine();
        sb.AppendLine("### Prüf- und Freigabenachweise");
        sb.AppendLine();
        sb.AppendLine("> Deklarierte Nachweisreferenzen, keine Identitäts-, Signatur- oder Genehmigungsprüfung.");
        sb.AppendLine();
        if (Approvals.Count == 0) sb.AppendLine("OFFEN – keine Prüf- oder Freigabenachweise hinterlegt.");
        else
        {
            sb.AppendLine("| Art | Rolle | Person | Datum | Quelle |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var approval in Approvals)
                sb.AppendLine($"| {approval.Kind} | {Esc(approval.Role)} | {Esc(approval.Person)} | {approval.Date} | {approval.Evidence} |");
        }
        sb.AppendLine();
        sb.AppendLine("### Verteilung, Schutz und Aufbewahrung");
        sb.AppendLine();
        sb.AppendLine($"- Verteilung und Zugriff (organisatorische Vorgabe): {Esc(Distribution)}");
        sb.AppendLine($"- Schutzklasse (nicht technisch durchgesetzt): {Esc(Classification)}");
        sb.AppendLine($"- Aufbewahrung und Aussonderung (organisatorische Vorgabe): {Esc(Retention)}");
        sb.AppendLine();
        sb.AppendLine("## Inhaltsverzeichnis");
        sb.AppendLine();
        sb.AppendLine("- [Deckblatt](#deckblatt)");
        sb.AppendLine("- [Dokumentenlenkung](#dokumentenlenkung)");
        sb.AppendLine("- [Produktinformation](#produktinformation)");
        foreach (var section in template.Sections)
            sb.AppendLine($"  - [{Esc(section.Title)}](#{section.Id})");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Produktinformation");
        sb.AppendLine();
        // ProductDraft emits anchored sections in controlled mode; avoid parsing prose/heading text.
        sb.Append(draft);
        if (!draft.EndsWith("\n", StringComparison.Ordinal)) sb.AppendLine();
        return sb.ToString();
    }

    public string AddToReport(string report)
    {
        var json = JsonNode.Parse(report)?.AsObject() ??
            throw new ArgumentException("invalid product report");
        json["document_control"] = new JsonObject {
            ["document_id"] = Id, ["sha256"] = Hash
        };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public List<ProductCheck.Finding> Check(string root, string reportPath)
    {
        var findings = new List<ProductCheck.Finding>();
        if (!File.Exists(reportPath))
        {
            findings.Add(new("DOC-001", "error", null, "evidence report missing"));
            return findings;
        }
        JsonDocument report;
        try { report = JsonDocument.Parse(File.ReadAllText(reportPath)); }
        catch (Exception e) when (e is IOException or JsonException)
        {
            findings.Add(new("DOC-001", "error", null, "evidence report unreadable"));
            return findings;
        }
        using (report)
        {
            if (!report.RootElement.TryGetProperty("document_control", out var obj) ||
                !obj.TryGetProperty("sha256", out var oldHash) || oldHash.GetString() != Hash)
                findings.Add(new("DOC-001", "error", null, "document-control data stale or absent; regenerate"));
        }
        // Paths were validated as repository-relative docs/*.md during parsing.
        foreach (var approval in Approvals.Where(a => !File.Exists(Path.Join(root, a.Evidence))))
            findings.Add(new("DOC-002", "error", null, $"approval evidence missing: {approval.Evidence}"));
        if (!Approvals.Any(x => x.Kind == "approval"))
            findings.Add(new("DOC-003", "warning", null, "no external approval evidence declared"));
        return findings;
    }

}
