using Microsoft.VisualBasic.FileIO;
using System.Text;
using System.Text.Json;

// Format adapters only. The schema-v1 YAML validator remains the single authority.
internal static class ProjectEvidenceImport
{
    static readonly string[] Header = ["id", "kind", "text", "source", "version"];

    public static ProjectEvidenceCatalog Load(string file)
    {
        using var reader = File.OpenText(file);
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".yaml" or ".yml" => ProjectEvidenceCatalog.Parse(reader, file),
            ".csv" => ParseCsv(reader, file),
            ".md" => ParseMarkdown(reader, file),
            _ => throw new ArgumentException($"{file}: expected .yaml, .yml, .csv or .md")
        };
    }

    public static ProjectEvidenceCatalog ParseCsv(TextReader reader, string source = "project evidence CSV")
    {
        using var parser = new TextFieldParser(reader);
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        var rows = new List<string[]>();
        try
        {
            while (!parser.EndOfData) rows.Add(parser.ReadFields() ?? []);
        }
        catch (MalformedLineException ex) { throw new ArgumentException($"{source}: invalid CSV: {ex.Message}", ex); }
        return FromRows(rows, source);
    }

    public static ProjectEvidenceCatalog ParseMarkdown(TextReader reader, string source = "project evidence Markdown")
    {
        var rows = new List<string[]>();
        var lines = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var (line, i) in lines.Select((x, i) => (x, i)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith('|') || !line.EndsWith('|'))
                throw new ArgumentException($"{source}: line {i + 1} must be a pipe-delimited table row");
            var cells = line[1..^1].Split('|').Select(x => x.Trim()).ToArray();
            if (rows.Count == 1 && cells.Length == Header.Length && cells.All(x => x.Length >= 3 && x.All(c => c == '-' || c == ':')))
                continue;
            rows.Add(cells);
        }
        return FromRows(rows, source);
    }

    static ProjectEvidenceCatalog FromRows(List<string[]> rows, string source)
    {
        if (rows.Count == 0 || !rows[0].SequenceEqual(Header, StringComparer.Ordinal))
            throw new ArgumentException($"{source}: expected header: {string.Join(",", Header)}");
        var yaml = new StringBuilder("schema: 1\nevidence:\n");
        if (rows.Count == 1) yaml.Append("  []\n");
        foreach (var (row, i) in rows.Skip(1).Select((x, i) => (x, i)))
        {
            if (row.Length != Header.Length) throw new ArgumentException($"{source}: row {i + 2} must have five fields");
            for (var j = 0; j < Header.Length; j++)
                yaml.Append(j == 0 ? "  - " : "    ").Append(Header[j]).Append(": ").Append(JsonSerializer.Serialize(row[j])).Append('\n');
        }
        return ProjectEvidenceCatalog.Parse(new StringReader(yaml.ToString()), source);
    }
}
