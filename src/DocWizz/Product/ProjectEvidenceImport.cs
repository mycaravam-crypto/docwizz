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
        var sourceLines = new List<int>();
        var lines = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var (raw, i) in lines.Select((x, i) => (x, i)))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('|') || !line.EndsWith('|'))
                throw new ArgumentException($"{source}: line {i + 1} must be a pipe-delimited table row");
            var cells = line[1..^1].Split('|').Select(x => x.Trim()).ToArray();
            if (rows.Count == 1 && cells.Length == Header.Length && cells.All(x => x.Length >= 3 && x.All(c => c == '-' || c == ':')))
                continue;
            rows.Add(cells);
            sourceLines.Add(i + 1);
        }
        return FromRows(rows, source, sourceLines);
    }

    static ProjectEvidenceCatalog FromRows(List<string[]> rows, string source, List<int>? sourceLines = null)
    {
        if (rows.Count == 0 || !rows[0].SequenceEqual(Header, StringComparer.Ordinal))
            throw new ArgumentException($"{source}: expected header: {string.Join(",", Header)}" +
                (rows.Count > 0 && rows[0].Length == 1 && rows[0][0].Contains(';') ? " (semicolon-separated? export as comma-separated CSV)" : ""));
        var yaml = new StringBuilder(rows.Count == 1 ? "schema: 1\nevidence: []\n" : "schema: 1\nevidence:\n");
        foreach (var (row, i) in rows.Skip(1).Select((x, i) => (x, i)))
        {
            if (row.Length != Header.Length) throw new ArgumentException($"{source}: line {(sourceLines is null ? i + 2 : sourceLines[i + 1])} must have five fields");
            for (var j = 0; j < Header.Length; j++)
                yaml.Append(j == 0 ? "  - " : "    ").Append(Header[j]).Append(": ").Append(JsonSerializer.Serialize(row[j])).Append('\n');
        }
        try { return ProjectEvidenceCatalog.Parse(new StringReader(yaml.ToString()), source); }
        catch (ArgumentException ex)
        {
            var message = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"evidence\[(\d+)\]", match =>
            {
                var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return index < rows.Count - 1 ? $"line {(sourceLines is null ? index + 2 : sourceLines[index + 1])}" : match.Value;
            });
            throw new ArgumentException(message, ex);
        }
    }
}
