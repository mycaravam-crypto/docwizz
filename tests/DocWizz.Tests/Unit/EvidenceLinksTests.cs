namespace DocWizz.Tests.Unit;

public class EvidenceLinksTests
{
    const string Valid = """
        schema: 1
        evidence:
          - id: REQ-017
            kind: requirement
            text: Authenticate users
            source: docs/requirements.md
            version: v1
          - id: ARC-004
            kind: architecture
            text: AuthService
            source: docs/architecture.md
            version: v1
          - id: TEST-032
            kind: test
            text: Verify login
            source: docs/tests.md
            version: v1
        links:
          - from: REQ-017
            to: ARC-004
          - from: ARC-004
            to: TEST-032
        """;

    [Fact]
    public void Explicit_chain_is_preserved_in_order()
    {
        var catalog = ProjectEvidenceCatalog.Parse(new StringReader(Valid), "links.yaml");
        Assert.Equal([new EvidenceLink("REQ-017", "ARC-004"), new EvidenceLink("ARC-004", "TEST-032")], catalog.Links);
        Assert.Equal(catalog.Links, ProjectEvidenceCatalog.Parse(new StringReader(Valid)).Links);
    }

    [Theory]
    [InlineData("from: REQ-017", "from: REQ-999", "links[0].from unknown")]
    [InlineData("to: TEST-032", "to: REQ-017", "links[1]: relationship")]
    [InlineData("to: ARC-004", "to: TEST-032", "links[0]: relationship")]
    [InlineData("from: ARC-004", "from: TEST-032", "links[1]: relationship")]
    [InlineData("to: ARC-004", "to: NOT-FOUND", "links[0].to unknown")]
    [InlineData("to: ARC-004", "to: ARC-004\n    extra: yes", "links[0]: unknown key")]
    public void Bad_links_have_indexed_errors(string find, string replace, string expected)
    {
        var yaml = Valid.Replace(find, replace, StringComparison.Ordinal);
        var ex = Assert.Throws<ArgumentException>(() => ProjectEvidenceCatalog.Parse(new StringReader(yaml), "links.yaml"));
        Assert.Contains("links.yaml:", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Duplicate_link_is_rejected()
    {
        var yaml = Valid + "  - from: REQ-017\n    to: ARC-004\n";
        Assert.Contains("duplicate link", Assert.Throws<ArgumentException>(() => ProjectEvidenceCatalog.Parse(new StringReader(yaml))).Message);
    }

    [Fact]
    public void Existing_catalog_without_links_is_compatible()
    {
        var yaml = Valid[..Valid.IndexOf("links:", StringComparison.Ordinal)];
        Assert.Empty(ProjectEvidenceCatalog.Parse(new StringReader(yaml)).Links);
    }

    [Fact]
    public void Non_list_links_is_rejected()
    {
        var yaml = Valid[..Valid.IndexOf("links:", StringComparison.Ordinal)] + "links: none\n";
        Assert.Contains("catalog.links must be a list", Assert.Throws<ArgumentException>(() => ProjectEvidenceCatalog.Parse(new StringReader(yaml))).Message);
    }
}
