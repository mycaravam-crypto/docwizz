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
        var withControl = control.Render(template, plain);
        Assert.Contains("# SW-Architektur Auftragsverwaltung", withControl);
        Assert.Contains("## Deckblatt", withControl);
        Assert.Contains("## Dokumentenlenkung", withControl);
        Assert.Contains("## Inhaltsverzeichnis", withControl);
        Assert.Contains("[Bausteine](#structure)", withControl);
        Assert.Contains("<a id=\"structure\"></a>", withControl);
        Assert.Contains("Genehmigung nicht verifiziert", withControl);
        Assert.Contains("OFFEN – keine Prüf- oder Freigabenachweise", withControl);
        Assert.Equal(withControl, control.Render(template, plain));
        Assert.Equal(plain, ProductDraft.Render(template));
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
}
