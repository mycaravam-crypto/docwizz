namespace DocWizz.Tests.Unit;

public class ProjectEvidenceCatalogTests
{
    const string Valid = """
        schema: 1
        evidence:
          - id: REQ-017
            kind: requirement
            text: Users must authenticate.
            source: docs/requirements.md
            version: v1
          - id: ARC-004
            kind: architecture
            text: Authentication is handled by AuthService.
            source: docs/architecture.md
            version: v2
          - id: TEST-032
            kind: test
            text: Successful sign-in is verified.
            source: docs/tests.md
            version: v1
        """;

    [Fact]
    public void Valid_catalog_has_stable_order_and_hashes()
    {
        var first = ProjectEvidenceCatalog.Parse(new StringReader(Valid), "evidence.yaml");
        var second = ProjectEvidenceCatalog.Parse(new StringReader(Valid), "evidence.yaml");
        Assert.Equal(["REQ-017", "ARC-004", "TEST-032"], first.Items.Select(x => x.Id));
        Assert.Equal(first.Items.Select(x => x.Hash), second.Items.Select(x => x.Hash));
        Assert.All(first.Items, x => Assert.Equal(64, x.Hash.Length));
        Assert.NotEqual(first.Items[0].Hash, ProjectEvidenceCatalog.Parse(new StringReader(Valid.Replace("version: v1", "version: v3")), "evidence.yaml").Items[0].Hash);
    }

    [Theory]
    [InlineData("schema: 1\nevidence:", "schema: 1\nmissing:", "catalog.evidence")]
    [InlineData("schema: 1", "schema: '1'", "unquoted 1")]
    [InlineData("id: REQ-017", "id: ../../escape", "evidence[0].id")]
    [InlineData("kind: requirement", "kind: decision", "evidence[0].kind")]
    [InlineData("source: docs/requirements.md", "source: ../secrets.md", "evidence[0].source")]
    [InlineData("version: v1", "version: ''", "evidence[0].version")]
    [InlineData("version: v2", "version: v2\n    unknown: yes", "unknown key")]
    public void Invalid_fields_report_context(string find, string replacement, string expected)
    {
        Assert.Contains(find, Valid);
        var yaml = Valid.Replace(find, replacement, StringComparison.Ordinal);
        var ex = Assert.Throws<ArgumentException>(() => ProjectEvidenceCatalog.Parse(new StringReader(yaml), "project.yaml"));
        Assert.Contains("project.yaml:", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Duplicate_ids_fail()
    {
        var yaml = Valid.Replace("ARC-004", "REQ-017");
        Assert.Contains("duplicate", Assert.Throws<ArgumentException>(() => ProjectEvidenceCatalog.Parse(new StringReader(yaml))).Message);
    }
}
