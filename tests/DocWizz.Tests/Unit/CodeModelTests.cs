using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class CodeModelTests
{
    [Fact]
    public void Location_includes_the_end_line_only_when_it_spans_lines()
    {
        Assert.Equal("a.cs:3", CodeModel.Location(new Node("cs:A", "class", "A", "a.cs", 3)));
        Assert.Equal("a.cs:3-9", CodeModel.Location(new Node("cs:A", "class", "A", "a.cs", 3, EndLine: 9)));
    }

    [Fact]
    public void Merged_hash_does_not_depend_on_declaration_order()
    {
        Node[] parts = [N("cs:Report", "Report.A.cs", hash: "aaa"), N("cs:Report", "Report.B.cs", hash: "bbb")];
        var forward = CodeModel.MergeHashes(parts).Select(n => n.Hash).Distinct().Single();
        var backward = CodeModel.MergeHashes(parts.Reverse()).Select(n => n.Hash).Distinct().Single();
        Assert.Equal(forward, backward);
        Assert.NotEqual("aaa", forward);
    }

    [Fact]
    public void Merging_keeps_test_and_production_sides_apart()
    {
        Node[] parts = [N("route:/login", "src/router.ts", hash: "p"), N("route:/login", "tests/router.ts", hash: "t")];
        var merged = CodeModel.MergeHashes(parts, n => n.File.StartsWith("tests/")).ToList();
        Assert.Equal(["p", "t"], merged.Select(n => n.Hash));
    }

    [Fact]
    public void Tests_edges_are_not_dependencies() => Assert.DoesNotContain("tests", CodeModel.DependencyKinds);
}
