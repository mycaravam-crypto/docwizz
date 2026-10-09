namespace DocWizz.Tests.Unit;

public class ProductDraftTests
{
    [Fact]
    public void Empty_evidence_produces_only_open_sections()
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
              - id: modules
                title: Bausteine
                required: true
                sources: [code]
            """), "test.yaml");
        var draft = ProductDraft.Render(template);
        Assert.StartsWith("# sw-architecture", draft);
        Assert.Equal(2, draft.Split("OFFEN").Length - 1);
        Assert.Contains("## Geltungsbereich", draft);
        Assert.Contains("## Bausteine", draft);
        Assert.DoesNotContain("freigegeben", draft, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(draft, ProductDraft.Render(template));
    }
}
