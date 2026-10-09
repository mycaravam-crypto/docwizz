using YamlDotNet.RepresentationModel;

// Product template contract, deliberately independent of documentation-coverage profiles and the renderer.
// Schema v1 defines structure and source policy only. It does not claim V-Modell XT conformity.
internal sealed record ProductSection(string Id, string Title, bool Required, IReadOnlyList<string> Sources);
internal sealed record ProductTemplate(
    string ProductId, string XtVariant, string XtVersion, string Tailoring,
    IReadOnlyList<ProductSection> Sections, IReadOnlyList<string> Dependencies)
{
    static readonly HashSet<string> RootKeys =
        ["schema", "product_id", "xt_variant", "xt_version", "tailoring", "sections", "dependencies"];
    static readonly HashSet<string> SectionKeys = ["id", "title", "required", "sources"];
    static readonly HashSet<string> SourceKinds = ["code", "project"];

    // Load and validate a product template.
    public static ProductTemplate Load(string file)
    {
        using var input = File.OpenText(file);
        return Parse(input, file);
    }

    // Parse templates without file I/O for isolated tests.
    public static ProductTemplate Parse(TextReader input, string source)
    {
        try { return ParseCore(input); }
        catch (ArgumentException error) { throw new ArgumentException($"{source}: {error.Message}", error); }
    }

    static ProductTemplate ParseCore(TextReader input)
    {
        var yaml = new YamlStream();
        try { yaml.Load(input); }
        catch (YamlDotNet.Core.YamlException e) { throw new ArgumentException($"invalid product template YAML: {e.Message}", e); }
        if (yaml.Documents.Count != 1)
            throw new ArgumentException("product template must contain exactly one YAML document");
        var root = Map(yaml.Documents[0].RootNode, "template");
        Keys(root, RootKeys, "template");
        var schema = Required(root, "schema", "template");
        if (((YamlScalarNode)root.Children[new YamlScalarNode("schema")]).Style != YamlDotNet.Core.ScalarStyle.Plain || schema != "1") throw new ArgumentException($"unsupported product template schema '{schema}' (expected 1)");
        var id = Required(root, "product_id", "template");
        if (!Slug.IsValid(id))
            throw new ArgumentException("template.product_id must be a lowercase slug");
        var variant = Required(root, "xt_variant", "template");
        var version = Required(root, "xt_version", "template");
        var tailoring = Required(root, "tailoring", "template");

        if (!root.Children.TryGetValue(new YamlScalarNode("sections"), out var sectionNode)
            || sectionNode is not YamlSequenceNode sections || sections.Children.Count == 0)
            throw new ArgumentException("template.sections must be a non-empty list");

        var result = new List<ProductSection>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, index) in sections.Children.Select((node, index) => (node, index)))
        {
            var context = $"sections[{index}]";
            var section = Map(node, context);
            Keys(section, SectionKeys, context);
            var sectionId = Required(section, "id", context);
            if (!Slug.IsValid(sectionId)) throw new ArgumentException($"{context}.id must be a lowercase slug");
            var title = Required(section, "title", context);
            if (!ids.Add(sectionId))
                throw new ArgumentException($"duplicate section id '{sectionId}'");
            var requiredText = Required(section, "required", context);
            if (((YamlScalarNode)section.Children[new YamlScalarNode("required")]).Style != YamlDotNet.Core.ScalarStyle.Plain || requiredText is not ("true" or "false"))
                throw new ArgumentException($"{context}.required must be true or false");
            var sources = Strings(section, "sources", context);
            if (sources.Count == 0) throw new ArgumentException($"{context}.sources must not be empty");
            foreach (var source in sources)
                if (!SourceKinds.Contains(source))
                    throw new ArgumentException($"{context}.sources: unsupported source '{source}' (code, project)");
            Unique(sources, $"{context}.sources");
            result.Add(new ProductSection(sectionId, title, requiredText == "true", sources));
        }

        var dependencies = Strings(root, "dependencies", "template");
        foreach (var dependency in dependencies)
            if (!Slug.IsValid(dependency))
                throw new ArgumentException($"invalid dependency id: {dependency}");
        if (dependencies.Contains(id)) throw new ArgumentException("product cannot depend on itself");
        Unique(dependencies, "template.dependencies");
        return new ProductTemplate(id, variant, version, tailoring, result, dependencies);
    }

    static void Unique(IEnumerable<string> values, string context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(value)) throw new ArgumentException($"{context}: duplicate value '{value}'");
    }

    static YamlMappingNode Map(YamlNode node, string context) =>
        node as YamlMappingNode ?? throw new ArgumentException($"{context} must be a mapping");

    static void Keys(YamlMappingNode map, HashSet<string> allowed, string context)
    {
        foreach (var key in map.Children.Keys)
        {
            if (key is not YamlScalarNode scalar || scalar.Value is null || !allowed.Contains(scalar.Value))
                throw new ArgumentException($"{context}: unknown key '{(key as YamlScalarNode)?.Value ?? key.ToString()}'");
        }
    }

    static string Required(YamlMappingNode map, string name, string context)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(name), out var value))
            throw new ArgumentException($"{context}.{name} is missing");
        if (value is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            throw new ArgumentException($"{context}.{name} must be a non-empty scalar");
        return scalar.Value.Trim();
    }

    static List<string> Strings(YamlMappingNode map, string name, string context)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(name), out var node)) return [];
        if (node is not YamlSequenceNode sequence)
            throw new ArgumentException($"{context}.{name} must be a list");
        var result = new List<string>();
        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
                throw new ArgumentException($"{context}.{name} entries must be non-empty strings");
            result.Add(scalar.Value.Trim());
        }
        return result;
    }
}
