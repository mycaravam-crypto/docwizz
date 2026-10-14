namespace DocWizz.Tests.Unit;

public class McpRefModelCacheTests
{
    [Fact]
    public void Reuses_resolved_commit_but_rebuilds_when_ref_moves_or_scope_changes()
    {
        var cache = new McpRefModelCache();
        var sha = new string('a', 40);
        var builds = 0;
        CodeModel Build(string _) { builds++; return new CodeModel(null, [], []); }
        string? Resolve(string _) => sha;

        Assert.NotNull(cache.Get("HEAD", "scope-a", Resolve, Build));
        Assert.NotNull(cache.Get("HEAD", "scope-a", Resolve, Build));
        Assert.Equal(1, builds);
        sha = new string('b', 40);
        Assert.NotNull(cache.Get("HEAD", "scope-a", Resolve, Build));
        Assert.Equal(2, builds);
        Assert.NotNull(cache.Get("HEAD", "scope-b", Resolve, Build));
        Assert.Equal(3, builds);
    }

    [Fact]
    public void Invalid_refs_and_failed_scans_are_not_cached()
    {
        var cache = new McpRefModelCache();
        var builds = 0;
        CodeModel? Build(string _) { builds++; return null; }
        Assert.Null(cache.Get("missing", "scope", _ => null, Build));
        Assert.Null(cache.Get("abbreviated", "scope", _ => "abc123", Build));
        Assert.Equal(0, builds);
        Assert.Null(cache.Get("HEAD", "scope", _ => new string('c', 40), Build));
        Assert.Null(cache.Get("HEAD", "scope", _ => new string('c', 40), Build));
        Assert.Equal(2, builds);
    }
    [Fact]
    public void Evicts_oldest_commit_when_capacity_is_exceeded()
    {
        var cache = new McpRefModelCache();
        var scans = 0;
        CodeModel Build(string _) { scans++; return new CodeModel(null, [], []); }
        string Sha(int i) => i.ToString("x40");
        for (var i = 0; i <= McpRefModelCache.Capacity; i++)
            Assert.NotNull(cache.Get("ref", null, _ => Sha(i), Build));
        Assert.Equal(McpRefModelCache.Capacity + 1, scans);
        // The newest remains cached; the first commit has been evicted.
        Assert.NotNull(cache.Get("ref", null, _ => Sha(McpRefModelCache.Capacity), Build));
        Assert.Equal(McpRefModelCache.Capacity + 1, scans);
        Assert.NotNull(cache.Get("ref", null, _ => Sha(0), Build));
        Assert.Equal(McpRefModelCache.Capacity + 2, scans);
    }

}
