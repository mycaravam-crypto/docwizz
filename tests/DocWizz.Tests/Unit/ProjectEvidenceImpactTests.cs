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
