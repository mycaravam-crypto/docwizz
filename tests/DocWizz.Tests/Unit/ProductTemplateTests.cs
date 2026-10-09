namespace DocWizz.Tests.Unit;
public class ProductTemplateTests
{
    const string Valid = """
        schema: 1
        product_id: sw-architecture
        xt_variant: V-Modell XT
        xt_version: '2.4'
        tailoring: project-specific
        sections:
          - id: scope
            title: Scope
            required: true
            sources: [project]
        dependencies: []
        """;

    static ProductTemplate Parse(string yaml) => ProductTemplate.Parse(new StringReader(yaml), "test.yaml");

    [Fact]
    public void Valid_contract_parses()
    {
        var p = Parse(Valid);
        Assert.Equal("sw-architecture", p.ProductId);
        Assert.Single(p.Sections);
        Assert.True(p.Sections[0].Required);
    }

    [Theory]
    [InlineData("schema: 2", "unsupported")]
    [InlineData("schema: \"1\"", "unsupported")]
    [InlineData("product_id: ''", "product_id")]
    [InlineData("required: yes", "true or false")]
    [InlineData("required: \"true\"", "true or false")]
    [InlineData("sources: [other]", "unsupported source")]
    [InlineData("sources: [code, code]", "duplicate value")]
    public void Invalid_field_is_rejected(string replacement, string expected)
    {
        var key = replacement.Split(':')[0];
        var original = Valid.Split('\n').Select(s => s.Trim()).First(s => s.StartsWith(key + ":"));
        var changed = Valid.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Valid, changed);
        var error = Assert.Throws<ArgumentException>(() => Parse(changed));
        Assert.Contains("test.yaml:", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("- a", "mapping")]
    [InlineData("product_id: x", "schema is missing")]
    [InlineData("schema: 1\n---\nschema: 1", "one YAML document")]
    [InlineData("sections: [bad", "invalid product template YAML")]
    [InlineData("schema: 1\nproduct_id: x\nxt_variant: x\nxt_version: x\ntailoring: x\nsections: []", "non-empty list")]
    [InlineData("schema: 1\nproduct_id: x\nxt_variant: x\nxt_version: x\ntailoring: x\nsections:\n  - id: x\n    title: X\n    required: true", "sources must not be empty")]
    public void Invalid_document_is_rejected(string yaml, string expected)
    {
        Assert.Contains(expected, Assert.Throws<ArgumentException>(() => Parse(yaml)).Message);
    }

    [Fact]
    public void Shipped_template_loads()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "templates/vmodell-xt/sw-architecture.yaml")))
            root = root.Parent;
        Assert.NotNull(root);
        Assert.Equal("sw-architecture", ProductTemplate.Load(Path.Combine(root!.FullName, "templates/vmodell-xt/sw-architecture.yaml")).ProductId);
    }

    [Fact]
    public void Duplicate_section_is_rejected()
    {
        var repeated = Valid.Replace("dependencies: []", "  - id: scope\n    title: Other\n    required: true\n    sources: [code]\ndependencies: []");
        Assert.Contains("duplicate section id", Assert.Throws<ArgumentException>(() => Parse(repeated)).Message);
    }
}
