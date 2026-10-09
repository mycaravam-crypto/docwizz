namespace DocWizz.Tests.Unit;

public class ProductEvidenceTests
{
    [Fact]
    public void Code_facts_are_sorted_traceable_and_do_not_fill_project_sections()
    {
        var template = ProductTemplate.Parse(new StringReader("""
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
                required: true
                sources: [code]
            """), "test.yaml");
        var model = new CodeModel(null,
            [new("cs:Z", "class", "Z", "Z.cs", 12),
             new("cs:A", "interface", "A", "A.cs", 4)],
            [new("cs:Z", "cs:A", "implements")]);
        var result = ProductDraft.Render(template, model);

        Assert.Contains("[Quelle](A.cs#L4)", result);
        Assert.Contains("[Quelle](Z.cs#L12)", result);
        Assert.True(result.IndexOf("interface: `A`", StringComparison.Ordinal) <
                    result.IndexOf("class: `Z`", StringComparison.Ordinal));
        Assert.Contains("`Z` → `A` (implements", result);
        Assert.Equal(1, result.Split("OFFEN").Length - 1);
        Assert.Equal(result, ProductDraft.Render(template, model));
    }

    [Fact]
    public void Unrecognized_sections_and_missing_code_stay_open()
    {
        var template = ProductTemplate.Parse(new StringReader("""
            schema: 1
            product_id: sw-architecture
            xt_variant: V-Modell XT
            xt_version: '2.4'
            tailoring: project-specific
            sections:
              - id: claims
                title: Annahmen
                required: true
                sources: [code]
            """), "test.yaml");
        var model = new CodeModel(null, [new("cs:A", "class", "A", "A.cs", 1)], []);
        Assert.Contains("OFFEN", ProductDraft.Render(template, model));
    }
}
