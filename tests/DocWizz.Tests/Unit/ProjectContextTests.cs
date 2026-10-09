namespace DocWizz.Tests.Unit;

public class ProjectContextTests
{
    const string TemplateYaml = """
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
        """;

    const string ContextYaml = """
        schema: 1
        statements:
          - section: scope
            text: The scope is defined in the signed project charter.
            source: docs/charter.md
        """;

    [Fact]
    public void Provenance_backed_project_statement_fills_requested_section()
    {
        var template = ProductTemplate.Parse(new StringReader(TemplateYaml), "template.yaml");
        var context = ProjectContext.Parse(new StringReader(ContextYaml));
        var output = ProductDraft.Render(template, project: context);
        Assert.Contains("signed project charter", output);
        Assert.Contains("Quelle: docs/charter.md", output);
        Assert.DoesNotContain("OFFEN", output);
        Assert.Equal(output, ProductDraft.Render(template, project: context));
    }

    [Fact]
    public void Statements_must_target_existing_project_sections()
    {
        var template = ProductTemplate.Parse(new StringReader(TemplateYaml), "template.yaml");
        var typo = ProjectContext.Parse(new StringReader(ContextYaml.Replace("section: scope", "section: scpoe")));
        Assert.Contains("not in template", Assert.Throws<ArgumentException>(() => ProductDraft.Render(template, project: typo)).Message);
        var codeOnly = ProductTemplate.Parse(new StringReader(TemplateYaml.Replace("sources: [project]", "sources: [code]")), "template.yaml");
        var valid = ProjectContext.Parse(new StringReader(ContextYaml));
        Assert.Contains("does not accept project", Assert.Throws<ArgumentException>(() => ProductDraft.Render(codeOnly, project: valid)).Message);
    }

    [Theory]
    [InlineData("schema: 2\nstatements: []")]
    [InlineData("schema: 1\nstatements:\n  - section: scope\n    text: claim")]
    [InlineData("schema: 1\nstatements:\n  - section: ../../bad\n    text: claim\n    source: charter.md")]
    [InlineData("schema: 1\nstatements:\n  - section: scope\n    text: claim\n    source: charter.md\n    approved: yes")]
    public void Invalid_context_is_rejected(string yaml) =>
        Assert.Throws<ArgumentException>(() => ProjectContext.Parse(new StringReader(yaml)));
}
