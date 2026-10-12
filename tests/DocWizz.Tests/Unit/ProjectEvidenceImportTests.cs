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
    public void All_formats_produce_the_same_canonical_evidence_hash()
    {
        const string value = "Prüfung: \"quoted\" \\ path";
        const string csv = "id,kind,text,source,version\nREQ-017,requirement,\"Prüfung: \"\"quoted\"\" \\ path\",docs/req.md,v1";
        const string markdown = "| id | kind | text | source | version |\n|---|---|---|---|---|\n  | REQ-017 | requirement | Prüfung: \"quoted\" \\ path | docs/req.md | v1 |  ";
        const string yaml = "schema: 1\nevidence:\n  - id: REQ-017\n    kind: requirement\n    text: 'Prüfung: \"quoted\" \\ path'\n    source: docs/req.md\n    version: v1";
        var fromCsv = ProjectEvidenceImport.ParseCsv(new StringReader(csv));
        var fromMd = ProjectEvidenceImport.ParseMarkdown(new StringReader(markdown));
        var fromYaml = ProjectEvidenceCatalog.Parse(new StringReader(yaml));
        Assert.Equal(value, fromCsv.Items[0].Text);
        Assert.Equal(fromCsv.Items[0].Hash, fromMd.Items[0].Hash);
        Assert.Equal(fromCsv.Items[0].Hash, fromYaml.Items[0].Hash);
    }

    [Fact]
    public void Imported_errors_point_to_the_original_line()
    {
        var csv = "id,kind,text,source,version\nREQ-017,unknown,x,docs/req.md,v1";
        Assert.Contains("line 2.kind", Assert.Throws<ArgumentException>(() =>
            ProjectEvidenceImport.ParseCsv(new StringReader(csv))).Message);
        var markdown = "| id | kind | text | source | version |\n|---|---|---|---|---|\n\n| REQ-017 | unknown | x | docs/req.md | v1 |";
        Assert.Contains("line 4.kind", Assert.Throws<ArgumentException>(() =>
            ProjectEvidenceImport.ParseMarkdown(new StringReader(markdown))).Message);
    }

    [Fact]
    public void Semicolon_csv_gets_actionable_header_diagnostic()
    {
        Assert.Contains("semicolon-separated", Assert.Throws<ArgumentException>(() =>
            ProjectEvidenceImport.ParseCsv(new StringReader("id;kind;text;source;version"))).Message);
    }

    [Fact]
    public void Empty_csv_is_a_valid_empty_catalog()
    {
        Assert.Empty(ProjectEvidenceImport.ParseCsv(new StringReader("id,kind,text,source,version")).Items);
    }
}
