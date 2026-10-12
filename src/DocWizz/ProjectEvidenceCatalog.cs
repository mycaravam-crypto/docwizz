using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

// P2 evidence inventory: explicit, versioned project facts only. No inferred relationships.
internal sealed record ProjectEvidence(string Id, string Kind, string Text, string Source, string Version, string Hash);
internal sealed record ProjectEvidenceCatalog(IReadOnlyList<ProjectEvidence> Items)
{
    static readonly HashSet<string> Kinds = ["requirement", "architecture", "test"];

    public static ProjectEvidenceCatalog Load(string path)
    {
        using var reader = File.OpenText(path);
        return Parse(reader, path);
    }

    public static ProjectEvidenceCatalog Parse(TextReader reader, string source = "project evidence")
    {
        try { return ParseCore(reader); }
        catch (ArgumentException ex) { throw new ArgumentException($"{source}: {ex.Message}", ex); }
    }

    static ProjectEvidenceCatalog ParseCore(TextReader reader)
    {
        var stream = new YamlStream();
        try { stream.Load(reader); }
        catch (YamlDotNet.Core.YamlException ex) { throw new ArgumentException($"invalid evidence YAML: {ex.Message}", ex); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("evidence catalog must contain exactly one mapping");
        Keys(root, ["schema", "evidence"], "catalog");
        if (Value(root, "schema", "catalog") != "1" || Node(root, "schema") is not YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain })
            throw new ArgumentException("catalog.schema must be unquoted 1");
        if (Node(root, "evidence") is not YamlSequenceNode entries)
            throw new ArgumentException("catalog.evidence must be a list");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ProjectEvidence>();
        foreach (var (entry, i) in entries.Children.Select((x, i) => (x, i)))
        {
            var at = $"evidence[{i}]";
            if (entry is not YamlMappingNode item) throw new ArgumentException($"{at} must be a mapping");
            Keys(item, ["id", "kind", "text", "source", "version"], at);
            var id = Value(item, "id", at);
            var kind = Value(item, "kind", at);
            var text = Value(item, "text", at);
            var path = Value(item, "source", at);
            var version = Value(item, "version", at);
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Z][A-Z0-9]*-[0-9]+$"))
                throw new ArgumentException($"{at}.id must look like REQ-017");
            if (!seen.Add(id)) throw new ArgumentException($"{at}.id duplicate '{id}'");
            if (!Kinds.Contains(kind)) throw new ArgumentException($"{at}.kind must be requirement, architecture or test");
            if (new[] { text, path, version }.Any(x => x.Contains('\n') || x.Contains('\r')))
                throw new ArgumentException($"{at} values must be single-line");
            if (!path.StartsWith("docs/", StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.Ordinal)
                || path.Split('/').Any(x => x is "." or ".." or "") || path.Contains('\\'))
                throw new ArgumentException($"{at}.source must be a repository-relative docs/*.md path");
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", id, kind, text, path, version));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            items.Add(new(id, kind, text, path, version, hash));
        }
        return new(items);
    }

    static YamlNode? Node(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    static string Value(YamlMappingNode map, string key, string context)
    {
        if (Node(map, key) is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{context}.{key} must be a non-empty scalar");
        return value.Trim();
    }

    static void Keys(YamlMappingNode map, HashSet<string> allowed, string context)
    {
        foreach (var key in map.Children.Keys)
            if (key is not YamlScalarNode scalar || scalar.Value is null || !allowed.Contains(scalar.Value))
                throw new ArgumentException($"{context}: unknown key '{key}'");
    }
}
