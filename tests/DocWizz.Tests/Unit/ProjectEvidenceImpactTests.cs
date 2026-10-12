namespace DocWizz.Tests.Unit;

public class ProjectEvidenceImpactTests
{
    static ProjectEvidenceCatalog Catalog(string version, bool includeTest, bool links)
    {
        var yaml = $"""
            schema: 1
            evidence:
              - id: REQ-001
                kind: requirement
                text: A requirement
                source: docs/requirements.md
                version: {version}
              - id: ARC-001
                kind: architecture
                text: A component
                source: docs/architecture.md
                version: v1
            """;
        if (includeTest)
            yaml += "\n  - id: TEST-001\n    kind: test\n    text: Acceptance\n    source: docs/test.md\n    version: v1";
        if (links)
            yaml += "\nlinks:\n  - from: REQ-001\n    to: ARC-001";
        return ProjectEvidenceCatalog.Parse(new StringReader(yaml));
    }

    [Fact]
    public void Changed_version_identifies_connected_evidence_without_inference()
    {
        var before = Catalog("v1", false, true);
        var after = Catalog("v2", true, true);
        var report = ProjectEvidenceImpact.Compare(before, after);
        Assert.Equal(["REQ-001", "TEST-001"], report.Evidence.Select(x => x.Id));
        Assert.Equal(["changed", "added"], report.Evidence.Select(x => x.Change));
        Assert.Equal(["ARC-001"], report.Evidence[0].RelatedIds);
        Assert.Empty(report.AddedLinks);
    }

    [Fact]
    public void Link_addition_and_removal_are_explicit()
    {
        var a = Catalog("v1", false, false);
        var b = Catalog("v1", false, true);
        Assert.Equal([new EvidenceLink("REQ-001", "ARC-001")], ProjectEvidenceImpact.Compare(a, b).AddedLinks);
        Assert.Equal([new EvidenceLink("REQ-001", "ARC-001")], ProjectEvidenceImpact.Compare(b, a).RemovedLinks);
        Assert.Empty(ProjectEvidenceImpact.Compare(a, b).Evidence);
    }

    const string Chain = """
        schema: 1
        evidence:
          - id: REQ-001
            kind: requirement
            text: A requirement
            source: docs/requirements.md
            version: v1
          - id: ARC-001
            kind: architecture
            text: A component
            source: docs/architecture.md
            version: v1
          - id: TEST-001
            kind: test
            text: Acceptance
            source: docs/test.md
            version: v1
          - id: TEST-002
            kind: test
            text: Regression
            source: docs/test.md
            version: v1
        links:
          - from: REQ-001
            to: ARC-001
          - from: ARC-001
            to: TEST-001
          - from: ARC-001
            to: TEST-002
        """;

    static ProjectEvidenceCatalog Parse(string yaml) => ProjectEvidenceCatalog.Parse(new StringReader(yaml));

    [Fact]
    public void Changed_requirement_reports_tests_reachable_through_explicit_links()
    {
        var report = ProjectEvidenceImpact.Compare(Parse(Chain), Parse(Chain.Replace("text: A requirement", "text: A sharper requirement")));
        var change = Assert.Single(report.Evidence);
        Assert.Equal("REQ-001", change.Id);
        Assert.Equal(["text"], change.Fields);
        Assert.Equal(["ARC-001"], change.RelatedIds);
        Assert.Equal(["TEST-001", "TEST-002"], change.AffectedTests);
    }

    [Fact]
    public void Changed_fields_are_named_and_a_test_change_affects_no_other_tests()
    {
        var after = Parse(Chain.Replace("text: Acceptance\n    source: docs/test.md\n    version: v1", "text: Acceptance\n    source: docs/acceptance.md\n    version: v2"));
        var change = Assert.Single(ProjectEvidenceImpact.Compare(Parse(Chain), after).Evidence);
        Assert.Equal("TEST-001", change.Id);
        Assert.Equal(["source", "version"], change.Fields);
        Assert.Equal(["ARC-001"], change.RelatedIds);
        Assert.Empty(change.AffectedTests);
    }

    [Fact]
    public void Removed_record_keeps_its_old_links_as_related_and_removed_links()
    {
        var withoutTest = Parse(Chain.Replace("  - id: TEST-002\n    kind: test\n    text: Regression\n    source: docs/test.md\n    version: v1\n", "")
            .Replace("\n  - from: ARC-001\n    to: TEST-002", ""));
        var report = ProjectEvidenceImpact.Compare(Parse(Chain), withoutTest);
        var change = Assert.Single(report.Evidence);
        Assert.Equal(("TEST-002", "removed"), (change.Id, change.Change));
        Assert.Empty(change.Fields);
        Assert.Equal(["ARC-001"], change.RelatedIds);
        Assert.Equal([new EvidenceLink("ARC-001", "TEST-002")], report.RemovedLinks);
    }

    [Fact]
    public void Identical_snapshots_produce_empty_differences()
    {
        var a = Catalog("v1", false, true);
        var result = ProjectEvidenceImpact.Compare(a, Catalog("v1", false, true));
        Assert.Empty(result.Evidence);
        Assert.Empty(result.AddedLinks);
        Assert.Empty(result.RemovedLinks);
    }
}
