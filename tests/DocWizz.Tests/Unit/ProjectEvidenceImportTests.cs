namespace DocWizz.Tests.Unit;

public class ProjectEvidenceImportTests
{
    const string Csv = """
        id,kind,text,source,version
        REQ-017,requirement,"Authenticate, securely",docs/requirements.md,v1
        ARC-004,architecture,AuthService,docs/architecture.md,v2
        """;

    [Fact]
    public void Csv_import_preserves_typed_fields_and_hashes()
    {
        var catalog = ProjectEvidenceImport.ParseCsv(new StringReader(Csv));
        Assert.Equal(["REQ-017", "ARC-004"], catalog.Items.Select(x => x.Id));
        Assert.Equal("Authenticate, securely", catalog.Items[0].Text);
        Assert.Equal(64, catalog.Items[0].Hash.Length);
        Assert.Empty(catalog.Links);
        Assert.Equal(catalog.Items.Select(x => x.Hash), ProjectEvidenceImport.ParseCsv(new StringReader(Csv)).Items.Select(x => x.Hash));
    }

    [Fact]
    public void Markdown_table_has_the_same_evidence_semantics()
    {
        const string markdown = """
            | id | kind | text | source | version |
            |---|---|---|---|---|
            | REQ-017 | requirement | Authenticate, securely | docs/requirements.md | v1 |
            | ARC-004 | architecture | AuthService | docs/architecture.md | v2 |
            """;
        var data = ProjectEvidenceImport.ParseMarkdown(new StringReader(markdown));
        Assert.Equal(["REQ-017", "ARC-004"], data.Items.Select(x => x.Id));
        Assert.Equal("AuthService", data.Items[1].Text);
    }

    [Theory]
    [InlineData("id,kind,text,source,version\nREQ-017,wrong,x,docs/requirements.md,v1", "kind must be")]
    [InlineData("id,kind,text,source,version\nREQ-017,requirement,x,../secret.md,v1", "source")]
    [InlineData("id,kind,text,source,version\nREQ-017,requirement", "five fields")]
    [InlineData("id,kind,text,source,version\nREQ-017,requirement,x,docs/req.md,v1\nREQ-017,requirement,x,docs/req.md,v1", "duplicate")]
    public void Invalid_csv_is_rejected_by_common_validator(string csv, string expected)
    {
        Assert.Contains(expected, Assert.Throws<ArgumentException>(() =>
            ProjectEvidenceImport.ParseCsv(new StringReader(csv), "input.csv")).Message);
    }

    [Fact]
    public void Wrong_markdown_shape_is_rejected()
    {
        Assert.Contains("line 1", Assert.Throws<ArgumentException>(() =>
            ProjectEvidenceImport.ParseMarkdown(new StringReader("# free text"))).Message);
    }

    [Fact]
    public void Empty_csv_is_a_valid_empty_catalog()
    {
        Assert.Empty(ProjectEvidenceImport.ParseCsv(new StringReader("id,kind,text,source,version")).Items);
    }
}
