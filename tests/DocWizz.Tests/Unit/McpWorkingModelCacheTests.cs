namespace DocWizz.Tests.Unit;

public class McpWorkingModelCacheTests
{
    [Fact]
    public void Reuses_unchanged_scan_and_invalidates_on_touch_head_or_rescan()
    {
        var head = "one";
        var changed = false;
        var count = 0;
        var cache = new McpWorkingModelCache(() => head, _ => changed ? 1 : 0,
            () => { count++; return new CodeModel(null, [], []); });
        var first = cache.Get();
        Assert.Same(first, cache.Get());
        Assert.Equal(1, count);
        changed = true;
        var next = cache.Get();
        Assert.NotSame(first, next);
        Assert.Equal(2, count);
        changed = false;
        head = "two";
        Assert.NotSame(next, cache.Get());
        Assert.Equal(3, count);
        cache.Get(force: true);
        Assert.Equal(4, count);
    }

    [Fact]
    public void Editing_during_scan_invalidates_the_next_request()
    {
        var changed = false;
        var builds = 0;
        var cache = new McpWorkingModelCache(() => "HEAD", _ => changed ? 1 : 0,
            () => { builds++; changed = true; return new CodeModel(null, [], []); });
        cache.Get();
        cache.Get();
        Assert.Equal(2, builds);
    }
}
