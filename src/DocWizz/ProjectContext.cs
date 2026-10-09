using YamlDotNet.RepresentationModel;

// Human-maintained project statements, not assertions of review or approval.
internal sealed record ProjectStatement(string SectionId, string Text, string Source);
internal sealed record ProjectContext(IReadOnlyList<ProjectStatement> Statements)
{
    public static ProjectContext Load(string file)
    {
        using var reader = File.OpenText(file);
        return Parse(reader, file);
    }

    public static ProjectContext Parse(TextReader reader, string source = "project context")
    {
        try { return ParseCore(reader); }
        catch (ArgumentException e) { throw new ArgumentException($"{source}: {e.Message}", e); }
    }

    public void Validate(ProductTemplate template)
    {
        foreach (var (statement, i) in Statements.Select((x, i) => (x, i)))
        {
            var section = template.Sections.FirstOrDefault(x => x.Id == statement.SectionId);
            if (section is null)
                throw new ArgumentException($"statements[{i}]: section '{statement.SectionId}' is not in template {template.ProductId}");
            if (!section.Sources.Contains("project"))
                throw new ArgumentException($"statements[{i}]: section '{statement.SectionId}' does not accept project sources");
        }
    }

    static ProjectContext ParseCore(TextReader reader)
    {
        var yaml = new YamlStream();
        try { yaml.Load(reader); }
        catch (YamlDotNet.Core.YamlException e) { throw new ArgumentException($"invalid project context YAML: {e.Message}", e); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("project context must contain one mapping document");
        CheckKeys(root, ["schema", "statements"], "context");
        if (Read(root, "schema", "context") != "1" || Get(root, "schema") is not YamlScalarNode schema || schema.Style != YamlDotNet.Core.ScalarStyle.Plain)
            throw new ArgumentException("schema must be unquoted 1");
        if (Get(root, "statements") is not YamlSequenceNode entries)
            throw new ArgumentException("statements must be a list");

        var result = new List<ProjectStatement>();
        var seen = new HashSet<(string, string)>();
        foreach (var (item, i) in entries.Children.Select((x, i) => (x, i)))
        {
            var context = $"statements[{i}]";
            if (item is not YamlMappingNode map) throw new ArgumentException($"{context} must be a mapping");
            CheckKeys(map, ["section", "text", "source"], context);
            var section = Read(map, "section", context);
            var value = Read(map, "text", context);
            var provenance = Read(map, "source", context);
            if (!Slug.IsValid(section)) throw new ArgumentException($"{context}.section must be a lowercase slug");
            if (value.Contains('\n') || value.Contains('\r') || provenance.Contains('\n') || provenance.Contains('\r'))
                throw new ArgumentException($"{context}: text and source must be single-line");
            if (value.Contains("OFFEN", StringComparison.OrdinalIgnoreCase) || value.Contains("freigegeben", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{context}.text must not impersonate status or approval");
            if (!provenance.StartsWith("docs/", StringComparison.Ordinal) || provenance.Contains("..", StringComparison.Ordinal)
                || provenance.Contains(' ') || !provenance.EndsWith(".md", StringComparison.Ordinal))
                throw new ArgumentException($"{context}.source must be a repository-relative docs/*.md path");
            if (!seen.Add((section, value))) throw new ArgumentException($"{context}: duplicate statement in '{section}'");
            result.Add(new(section, value, provenance));
        }
        return new(result);
    }

    static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    static string Read(YamlMappingNode map, string key, string context)
    {
        if (Get(map, key) is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{context}.{key} must be a non-empty scalar");
        return value.Trim();
    }

    static void CheckKeys(YamlMappingNode map, HashSet<string> allowed, string context)
    {
        foreach (var key in map.Children.Keys)
            if (key is not YamlScalarNode scalar || scalar.Value is null || !allowed.Contains(scalar.Value))
                throw new ArgumentException($"{context}: unknown key '{key}'");
    }
}
