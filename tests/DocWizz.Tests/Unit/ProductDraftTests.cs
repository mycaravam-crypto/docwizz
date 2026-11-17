namespace DocWizz.Tests.Unit;

public class ProductDraftTests
{
    const string TemplateYaml = """
        schema: 1
        product_id: sw-architecture
        xt_variant: V-Modell XT
        xt_version: '2.4'
        tailoring: project-specific
        sections:
          - id: scope
            title: Geltungsbereich
            required: true
            sources: [project]
          - id: structure
            title: Bausteine
            required: true
            sources: [code]
          - id: interfaces
            title: Schnittstellen
            required: false
            sources: [code]
        dependencies: []
        """;

    internal static ProductTemplate Template() => ProductTemplate.Parse(new StringReader(TemplateYaml), "test.yaml");

    [Fact]
    public void No_evidence_matches_exact_golden_output()
    {
        var text = ProductDraft.Render(Template());
        var expected = """
            # sw-architecture

            V-Modell: V-Modell XT 2.4; Tailoring: project-specific
            Product dependencies: none declared

            > Draft – no statement of V-Modell XT conformity or approval.

            ## Geltungsbereich

            OPEN – needs a source and expert review.

            ## Bausteine

            OPEN – needs a source and expert review.

            ## Schnittstellen

            Optional – no evidenced content.

            """;
        Assert.Equal(expected.TrimEnd() + Environment.NewLine + Environment.NewLine, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        Assert.Equal(text, ProductDraft.Render(Template()));
    }

    [Fact]
    public void Facts_are_traceable_ordered_and_do_not_claim_more_than_code_shows()
    {
        var model = new CodeModel("abc123",
            [new("cs:Z", "class", "Z", "Z.cs", 12), new("cs:A", "interface", "A", "A.cs", 4),
             new("cs:R", "record", "R", "R.cs", 3), new("cs:M", "method", "M", "Z.cs", 18)],
            [new("cs:Z", "cs:A", "implements"), new("cs:Z", "cs:M", "contains"),
             new("cs:M", "cs:A", "calls"), new("cs:Z", "missing", "calls"),
             new("cs:Z", "cs:A", "references")]);
        var text = ProductDraft.Render(Template(), model);
        Assert.Contains("Code baseline: abc123", text);
        Assert.Contains("record: `R`", text);
        Assert.Contains("source: `A.cs:4`", text);
        Assert.DoesNotContain("method: `M`", text);
        Assert.DoesNotContain("(references;", text);
        Assert.DoesNotContain("`missing`", text);
        Assert.True(text.IndexOf("## Geltungsbereich", StringComparison.Ordinal) <
                    text.IndexOf("## Bausteine", StringComparison.Ordinal));
        Assert.Equal(text, ProductDraft.Render(Template(), model));
    }
}
