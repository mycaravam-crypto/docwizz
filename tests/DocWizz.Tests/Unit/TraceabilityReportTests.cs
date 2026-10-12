namespace DocWizz.Tests.Unit;

public class TraceabilityReportTests
{
    const string Yaml = """
        schema: 1
        evidence:
          - id: REQ-020
            kind: requirement
            text: A second requirement
            source: docs/req.md
            version: v2
          - id: ARC-001
            kind: architecture
            text: Component
            source: docs/arch.md
            version: v1
          - id: TEST-003
            kind: test
            text: A test
            source: docs/test.md
            version: v1
          - id: REQ-010
            kind: requirement
            text: A requirement
            source: docs/req.md
            version: v1
          - id: ARC-002
            kind: architecture
            text: Other component
            source: docs/arch.md
            version: v1
        links:
          - from: REQ-020
            to: ARC-001
          - from: ARC-001
            to: TEST-003
          - from: REQ-010
            to: ARC-002
        """;

    [Fact]
    public void Reports_linked_no_test_and_unlinked_items_in_stable_order()
    {
        var report = TraceabilityReport.Build(ProjectEvidenceCatalog.Parse(new StringReader(Yaml)));
        Assert.Equal(["REQ-010", "REQ-020"], report.Requirements.Select(x => x.Requirement));
        Assert.Equal(["no-test", "linked"], report.Requirements.Select(x => x.Status));
        Assert.Equal(["TEST-003"], report.Requirements[1].Tests);
        Assert.Empty(report.UnlinkedArchitectures);
        Assert.Equal(["ARC-002"], report.UntestedArchitectures);
        Assert.Equal(TraceabilityReport.Json(report), TraceabilityReport.Json(report));
        Assert.Contains("OFFEN", TraceabilityReport.Markdown(report));
    }

    [Fact]
    public void Missing_architecture_and_orphan_architecture_are_reported()
    {
        var yaml = Yaml[..Yaml.IndexOf("links:", StringComparison.Ordinal)];
        var report = TraceabilityReport.Build(ProjectEvidenceCatalog.Parse(new StringReader(yaml)));
        Assert.All(report.Requirements, x => Assert.Equal("no-architecture", x.Status));
        Assert.Equal(["ARC-001", "ARC-002"], report.UnlinkedArchitectures);
        Assert.Equal(["ARC-001", "ARC-002"], report.UntestedArchitectures);
    }

    [Fact]
    public void Empty_catalog_has_stable_empty_outputs()
    {
        var report = TraceabilityReport.Build(ProjectEvidenceCatalog.Parse(new StringReader("schema: 1\nevidence: []")));
        Assert.Empty(report.Requirements);
        Assert.Contains("\"Requirements\": []", TraceabilityReport.Json(report));
    }
}
