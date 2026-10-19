namespace DocWizz.Tests.Unit;

public class ControlledDocumentTests
{
    const string Metadata = """
        schema: 1
        document_id: SWA-001
        title: SW-Architektur Auftragsverwaltung
        version: "0.1"
        author: Architekturteam
        owner: Systemarchitektur
        created: 2026-10-19
        classification: Intern
        distribution: Projektteam mit Berechtigung
        retention: Gemäß dokumentierter Aufbewahrungsregel QM-03
        changes:
          - version: "0.1"
            date: 2026-10-19
            author: Architekturteam
            description: Erstentwurf
        approvals: []
        """;

    [Fact]
    public void Cover_contents_control_and_sources_are_deterministic()
    {
        var control = ControlledDocument.Parse(Metadata);
        var template = ProductDraftTests.Template();
        var plain = ProductDraft.Render(template);
        var withControl = control.Render(template, ProductDraft.Render(template, controlled: true));
        Assert.Contains("# SW-Architektur Auftragsverwaltung", withControl);
        Assert.Contains("## Deckblatt", withControl);
        Assert.Contains("## Dokumentenlenkung", withControl);
        Assert.Contains("## Inhaltsverzeichnis", withControl);
        Assert.Contains("[Bausteine](#structure)", withControl);
        Assert.Contains("<a id=\"structure\"></a>", withControl);
        Assert.Contains("Genehmigung nicht verifiziert", withControl);
        Assert.Contains("OFFEN – keine Prüf- oder Freigabenachweise", withControl);
        Assert.Equal(withControl, control.Render(template, ProductDraft.Render(template, controlled: true)));
        Assert.Equal(plain, ProductDraft.Render(template));
    }

    [Fact]
    public void Fingerprint_is_independent_of_line_endings()
    {
        var lf = ControlledDocument.Parse(Metadata);
        var crlf = ControlledDocument.Parse(Metadata.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(lf.Hash, crlf.Hash);
    }

    [Fact]
    public void Control_hash_is_recorded_and_changes_are_detectable()
    {
        var first = ControlledDocument.Parse(Metadata);
        var json = first.AddToReport(ProductDraft.Report(ProductDraftTests.Template()));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(first.Hash, doc.RootElement.GetProperty("document_control").GetProperty("sha256").GetString());
        Assert.NotEqual(first.Hash, ControlledDocument.Parse(Metadata.Replace("Erstentwurf", "Änderung")).Hash);
    }

    [Theory]
    [InlineData("created: 2026-10-19", "created: yesterday")]
    [InlineData("document_id: SWA-001", "document_id: ../wrong")]
    [InlineData("changes:", "unknown_changes:")]
    public void Invalid_metadata_is_rejected(string oldValue, string newValue)
    {
        Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(Metadata.Replace(oldValue, newValue)));
    }

    [Fact]
    public void Unknown_keys_and_unsafe_evidence_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(Metadata + "\niso_certified: true\n"));
        Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(Metadata.Replace("approvals: []", """
            approvals:
              - kind: approval
                role: QM
                person: Example
                date: 2026-10-19
                evidence: ../fake.md
            """)));
    }

    // External YAML is a trust boundary: reject invalid data before any document is written.
    [Theory]
    [InlineData("document_id: SWA-001", "document_id: X")]
    [InlineData("document_id: SWA-001", "document_id: invalid/path")]
    [InlineData("document_id: SWA-001", "document_id: ")]
    [InlineData("version: \"0.1\"\nauthor:", "version: \"0.2\"\nauthor:")]
    [InlineData("created: 2026-10-19", "created: 19-10-2026")]
    [InlineData("created: 2026-10-19", "created: 2026/10/19")]
    [InlineData("schema: 1", "schema: 2")]
    public void Invalid_values_fail_fast(string before, string after)
    {
        Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(Metadata.Replace(before, after)));
    }

    [Theory]
    [InlineData("document_id: SWA-001")]
    [InlineData("title: SW-Architektur Auftragsverwaltung")]
    [InlineData("version: \"0.1\"")]
    [InlineData("author: Architekturteam")]
    [InlineData("owner: Systemarchitektur")]
    [InlineData("created: 2026-10-19")]
    [InlineData("classification: Intern")]
    [InlineData("distribution: Projektteam mit Berechtigung")]
    [InlineData("retention: Gemäß dokumentierter Aufbewahrungsregel QM-03")]
    public void Missing_required_fields_fail_fast(string line)
    {
        Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(Metadata.Replace("\n" + line + "\n", "\n")));
    }

    [Fact]
    public void Identifier_length_and_evidence_extension_are_enforced()
    {
        var invalidId = Metadata.Replace("document_id: SWA-001", "document_id: " + new string('A', 81));
        Assert.Contains("document_id", Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(invalidId)).Message);

        const string review = """
            approvals:
              - kind: approval
                role: QS
                person: Reviewer
                date: 2026-10-19
                evidence: docs/reviews/report.txt
            """;
        var invalidEvidence = Metadata.Replace("approvals: []", review);
        Assert.Contains("safe docs/*.md path",
            Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(invalidEvidence)).Message);
    }

    [Fact]
    public void Quoted_schema_is_valid_and_malformed_yaml_is_rejected()
    {
        Assert.Equal(ControlledDocument.Parse(Metadata).Id, ControlledDocument.Parse(Metadata.Replace("schema: 1", "schema: \"1\"")).Id);
        var invalid = Assert.Throws<ArgumentException>(() => ControlledDocument.Parse("schema: [unfinished"));
        Assert.Contains("invalid YAML", invalid.Message);
    }

    [Fact]
    public void Missing_required_changes_are_reported_explicitly()
    {
        const string changeBlock = "changes:\n  - version: \"0.1\"\n    date: 2026-10-19\n    author: Architekturteam\n    description: Erstentwurf";
        var withoutChanges = Metadata.Replace(changeBlock, "");
        var error = Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(withoutChanges));
        Assert.Contains("changes must be a list", error.Message);
    }

    [Fact]
    public void Changes_must_be_a_list_with_current_version_and_known_fields()
    {
        var changed = Metadata.Replace("changes:\n  - version: \"0.1\"\n    date: 2026-10-19\n    author: Architekturteam\n    description: Erstentwurf", "changes: unknown");
        Assert.Contains("must be a list", Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(changed)).Message);

        Assert.Contains("changes must contain current version",
            Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(Metadata.Replace("  - version: \"0.1\"", "  - version: \"0.0\""))).Message);
        Assert.Contains("required", Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(
            Metadata.Replace("  - version: \"0.1\"\n    date: 2026-10-19\n    author: Architekturteam\n    description: Erstentwurf", "  - date: 2026-10-19\n    author: Architekturteam\n    description: Erstentwurf"))).Message);
        Assert.Contains("unknown key", Assert.Throws<ArgumentException>(() => ControlledDocument.Parse(
            Metadata.Replace("description: Erstentwurf", "description: Erstentwurf\n    bogus: yes"))).Message);
    }

    [Fact]
    public void Approval_records_validate_structure_kind_dates_and_safe_evidence_paths()
    {
        const string valid = """
            approvals:
              - kind: approval
                role: Qualitätsmanagement
                person: Reviewer
                date: 2026-10-19
                evidence: docs/reviews/approval.md
            """;
        var full = Metadata.Replace("approvals: []", valid);
        Assert.Single(ControlledDocument.Parse(full).Approvals);
        Assert.Contains("must be a list", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(Metadata.Replace("approvals: []", "approvals: not-a-list"))).Message);
        Assert.Contains("must be a mapping", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(Metadata.Replace("approvals: []", "approvals: [text]"))).Message);
        Assert.Contains(".kind is required", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(full.Replace("  - kind: approval\n    role: Qualitätsmanagement", "  - role: Qualitätsmanagement"))).Message);
        Assert.Contains("review or approval", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(full.Replace("kind: approval", "kind: signoff"))).Message);
        Assert.Contains("date must be yyyy-MM-dd", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(full.Replace("date: 2026-10-19", "date: 19/10/2026"))).Message);
        Assert.Contains("safe docs/*.md path", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(full.Replace("docs/reviews/approval.md", "docs/../outside.md"))).Message);
        Assert.Contains("safe docs/*.md path", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(full.Replace("docs/reviews/approval.md", "/tmp/approval.md"))).Message);
        Assert.Contains("unknown key", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(full.Replace("evidence: docs/reviews/approval.md", "evidence: docs/reviews/approval.md\n    signed: true"))).Message);
    }

    [Fact]
    public void Metadata_table_cells_are_bounded_and_single_line()
    {
        Assert.Contains("max. 500", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(Metadata.Replace("description: Erstentwurf", "description: " + new string('x', 501)))).Message);
        Assert.Contains("one line", Assert.Throws<ArgumentException>(() =>
            ControlledDocument.Parse(Metadata.Replace("description: Erstentwurf", "description: |\n      Erstentwurf\n      weiterer Absatz"))).Message);
    }

    [Fact]
    public void Rendered_review_and_approval_are_declarations_and_escape_untrusted_markdown()
    {
        const string rows = """
            approvals:
              - kind: review
                role: QS
                person: Prüfer
                date: 2026-10-18
                evidence: docs/reviews/review.md
              - kind: approval
                role: QM
                person: Genehmiger
                date: 2026-10-19
                evidence: docs/reviews/approval.md
            """;
        var control = ControlledDocument.Parse(Metadata.Replace("approvals: []", rows)
            .Replace("description: Erstentwurf", "description: 'Review `code` | #draft'"));
        var rendered = control.Render(ProductDraftTests.Template(),
            ProductDraft.Render(ProductDraftTests.Template(), controlled: true));
        Assert.Contains("| review | QS | Prüfer | 2026-10-18 | docs/reviews/review.md |", rendered);
        Assert.Contains("| approval | QM | Genehmiger | 2026-10-19 | docs/reviews/approval.md |", rendered);
        Assert.Contains(@"Review \`code\` \| \#draft", rendered);
        Assert.Contains("keine Identitäts-, Signatur- oder Genehmigungsprüfung", rendered);
    }

    [Fact]
    public void Document_check_reports_missing_evidence_and_unverified_approval_separately()
    {
        var dir = Directory.CreateTempSubdirectory("docwizz-control-");
        try
        {
            const string row = """
                approvals:
                  - kind: review
                    role: QS
                    person: Prüfer
                    date: 2026-10-19
                    evidence: docs/reviews/review.md
                """;
            var reviewOnly = ControlledDocument.Parse(Metadata.Replace("approvals: []", row));
            var report = Path.Join(dir.FullName, "product.evidence.json");
            File.WriteAllText(report, reviewOnly.AddToReport(ProductDraft.Report(ProductDraftTests.Template())));
            var missing = reviewOnly.Check(dir.FullName, report);
            Assert.Contains(missing, finding => finding.Rule == "DOC-002" && finding.Severity == "error");
            Assert.Contains(missing, finding => finding.Rule == "DOC-003" && finding.Severity == "warning");

            var evidence = Path.Join(dir.FullName, "docs", "reviews", "review.md");
            Directory.CreateDirectory(Path.Join(dir.FullName, "docs", "reviews"));
            File.WriteAllText(evidence, "# Human review record");
            var withReview = reviewOnly.Check(dir.FullName, report);
            Assert.DoesNotContain(withReview, finding => finding.Rule == "DOC-002");
            Assert.Contains(withReview, finding => finding.Rule == "DOC-003");

            var approved = ControlledDocument.Parse(Metadata.Replace("approvals: []", row.Replace("kind: review", "kind: approval")));
            File.WriteAllText(report, approved.AddToReport(ProductDraft.Report(ProductDraftTests.Template())));
            Assert.DoesNotContain(approved.Check(dir.FullName, report), finding => finding.Rule is "DOC-001" or "DOC-002" or "DOC-003");
        }
        finally { dir.Delete(recursive: true); }
    }
}
