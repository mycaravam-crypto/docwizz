using YamlDotNet.RepresentationModel;

// Human-approved project evidence, separate from CodeModel facts and local AI drafts.
// Every statement carries a source; this contract is not an approval mechanism.
internal sealed record ProjectStatement(string SectionId, string Text, string Source);
internal sealed record ProjectContext(IReadOnlyList<ProjectStatement> Statements)
{
    public static ProjectContext Load(string file)
    {
        using var reader = File.OpenText(file);
        try { return Parse(reader); }
        catch (ArgumentException e) { throw new ArgumentException($"{file}: {e.Message}", e); }
    }

    public static ProjectContext Parse(TextReader reader)
    {
        var yaml = new YamlStream();
        try { yaml.Load(reader); }
        catch (YamlDotNet.Core.YamlException e) { throw new ArgumentException($"invalid project context YAML: {e.Message}", e); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("project context must be one mapping document");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "schema", "statements" };
        CheckKeys(root, allowed);
        if (Read(root, "schema") != "1" || Get(root, "schema") is not YamlScalarNode schema || schema.Style != YamlDotNet.Core.ScalarStyle.Plain)
            throw new ArgumentException("project context schema must be unquoted 1");
        if (Get(root, "statements") is not YamlSequenceNode entries)
            throw new ArgumentException("statements must be a list");
        var result = new List<ProjectStatement>();
        var seen = new HashSet<(string, string)>();
        foreach (var item in entries.Children)
        {
            if (item is not YamlMappingNode map) throw new ArgumentException("statement must be a mapping");
            CheckKeys(map, new HashSet<string>(StringComparer.Ordinal) { "section", "text", "source" });
            var section = Read(map, "section");
            var text = Read(map, "text");
            var source = Read(map, "source");
            if (!System.Text.RegularExpressions.Regex.IsMatch(section, "^[a-z0-9][a-z0-9-]*$"))
                throw new ArgumentException("statement section must be a lowercase slug");
            if (!seen.Add((section, text))) throw new ArgumentException($"duplicate statement in '{section}'");
            result.Add(new(section, text, source));
        }
        return new(result);
    }

    static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    static string Read(YamlMappingNode map, string key)
    {
        if (Get(map, key) is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{key} must be a non-empty scalar");
        return value.Trim();
    }

    static void CheckKeys(YamlMappingNode map, HashSet<string> allowed)
    {
        foreach (var key in map.Children.Keys)
            if (key is not YamlScalarNode scalar || scalar.Value is null || !allowed.Contains(scalar.Value))
                throw new ArgumentException($"unknown project context key '{key}'");
    }
}
