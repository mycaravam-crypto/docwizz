namespace DocWizz.Tests.Unit;

public class ProductTemplateTests
{
    static ProductTemplate Parse(string yaml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"docwizz-template-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(file, yaml);
        try { return ProductTemplate.Load(file); }
        finally { File.Delete(file); }
    }

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
          - id: structure
            title: Structure
            required: false
            sources: [code, project]
        dependencies: [sw-specification]
        """;

    [Fact]
    public void Loads_minimal_explicit_contract()
    {
        var template = Parse(Valid);
        Assert.Equal("sw-architecture", template.ProductId);
        Assert.Equal("V-Modell XT", template.XtVariant);
        Assert.Equal("2.4", template.XtVersion);
        Assert.Equal("project-specific", template.Tailoring);
        Assert.Equal(["scope", "structure"], template.Sections.Select(s => s.Id));
        Assert.True(template.Sections[0].Required);
        Assert.False(template.Sections[1].Required);
        Assert.Equal(["code", "project"], template.Sections[1].Sources);
        Assert.Equal(["sw-specification"], template.Dependencies);
    }

    [Theory]
    [InlineData("schema: 2", "unsupported product template schema")]
    [InlineData("product_id: ''", "product_id")]
    [InlineData("sections: []", "non-empty list")]
    [InlineData("required: yes", "must be true or false")]
    [InlineData("sources: [internet]", "unsupported source")]
    [InlineData("sources: [code, code]", "contains duplicates")]
    [InlineData("dependencies: [sw-architecture]", "cannot depend on itself")]
    [InlineData("unexpected: true", "unknown key")]
    public void Rejects_invalid_or_ambiguous_contracts(string replacement, string message)
    {
        var yaml = replacement.StartsWith("sections:") ? Valid.Replace(
            "sections:\n  - id: scope\n    title: Scope\n    required: true\n    sources: [project]\n  - id: structure\n    title: Structure\n    required: false\n    sources: [code, project]", replacement)
            : replacement.StartsWith("required:") ? Valid.Replace("required: true", replacement)
            : replacement.StartsWith("sources:") ? Valid.Replace("sources: [project]", replacement)
            : replacement.StartsWith("unexpected:") ? Valid + "\n" + replacement + "\n"
            : Valid.Replace(Valid.Split('\n').First(line => line.StartsWith(replacement.Split(':')[0] + ":")), replacement);
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => Parse(yaml)).Message);
    }

    [Fact]
    public void Rejects_duplicate_section_ids()
    {
        var yaml = Valid.Replace("id: structure", "id: scope");
        Assert.Contains("duplicate section id", Assert.Throws<ArgumentException>(() => Parse(yaml)).Message);
    }

    [Fact]
    public void Rejects_malformed_yaml()
    {
        Assert.Contains("invalid product template YAML",
            Assert.Throws<ArgumentException>(() => Parse("sections: [unclosed")).Message);
    }
}
