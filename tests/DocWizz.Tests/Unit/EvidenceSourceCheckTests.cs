using System.Security.Cryptography;
using System.Text;

namespace DocWizz.Tests.Unit;

public class EvidenceSourceCheckTests
{
    static ProjectEvidenceCatalog Catalog(string? hash) =>
        ProjectEvidenceCatalog.Parse(new StringReader("schema: 1\nevidence:\n  - id: REQ-001\n    kind: requirement\n    text: Requirement\n    source: docs/req.md\n    version: v1\n" +
            (hash is null ? "" : "    source_hash: " + hash + "\n")));

    [Fact]
    public void Source_digest_detects_current_stale_missing_and_unverified()
    {
        var root = Path.Combine(Path.GetTempPath(), "docwizz-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        try
        {
            var file = Path.Combine(root, "docs", "req.md");
            File.WriteAllText(file, "# requirement\n");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            Assert.Equal("current", Assert.Single(EvidenceSourceCheck.Check(Catalog(hash), root)).Status);
            Assert.Equal("unverified", Assert.Single(EvidenceSourceCheck.Check(Catalog(null), root)).Status);
            File.WriteAllText(file, "# changed\n");
            Assert.Equal("stale", Assert.Single(EvidenceSourceCheck.Check(Catalog(hash), root)).Status);
            File.Delete(file);
            Assert.Equal("missing", Assert.Single(EvidenceSourceCheck.Check(Catalog(hash), root)).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Changing_pinned_digest_is_visible_to_snapshot_diff()
    {
        var first = Catalog(new string('a', 64));
        var second = Catalog(new string('b', 64));
        var impact = ProjectEvidenceImpact.Compare(first, second);
        Assert.Equal("changed", Assert.Single(impact.Evidence).Change);
        Assert.Equal(["source_hash"], impact.Evidence[0].Fields);
        Assert.NotEqual(first.Items[0].Hash, second.Items[0].Hash);
    }

    [Fact]
    public void Bad_explicit_hash_is_rejected()
    {
        Assert.Contains("source_hash", Assert.Throws<ArgumentException>(() => Catalog("bad")).Message);
    }
}
