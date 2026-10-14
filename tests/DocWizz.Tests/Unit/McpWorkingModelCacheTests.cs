namespace DocWizz.Tests.Unit;

public class McpWorkingModelCacheTests
{
    [Fact]
    public void Reuses_unchanged_scan_and_invalidates_on_file_change_head_or_rescan()
    {
        var head = "one";
        var files = "a";
        var count = 0;
        var cache = new McpWorkingModelCache(() => head, () => files,
            () => { count++; return new CodeModel(null, [], []); });
        var first = cache.Get();
        Assert.Same(first, cache.Get());
        Assert.Equal(1, count);
        files = "b";
        var next = cache.Get();
        Assert.NotSame(first, next);
        Assert.Equal(2, count);
        head = "two";
        Assert.NotSame(next, cache.Get());
        Assert.Equal(3, count);
        cache.Get(force: true);
        Assert.Equal(4, count);
    }

    [Fact]
    public void Editing_during_scan_invalidates_the_next_request()
    {
        var files = "before";
        var builds = 0;
        var cache = new McpWorkingModelCache(() => "HEAD", () => files,
            () => { builds++; files = "edited during scan"; return new CodeModel(null, [], []); });
        cache.Get();
        cache.Get();
        Assert.Equal(2, builds);
    }

    // Regression (#205 review): the fingerprint must change for every input change a scan would see, not only for
    // newer mtimes on source extensions.
    [Fact]
    public void Fingerprint_changes_on_delete_rename_with_same_mtime_and_project_file_edit()
    {
        using var src = new Sources(("A.cs", "class A {}"), ("B.cs", "class B {}"), ("App.csproj", "<Project />"));
        string Print() => McpWorkingModelCache.Fingerprint(src.Root,
            Directory.EnumerateFiles(src.Root).Order(StringComparer.Ordinal));
        var original = Print();
        Assert.Equal(original, Print());

        var b = Path.Combine(src.Root, "B.cs");
        var mtime = File.GetLastWriteTimeUtc(b);
        var renamed = Path.Combine(src.Root, "C.cs");
        File.Move(b, renamed);
        File.SetLastWriteTimeUtc(renamed, mtime);
        var afterRename = Print();
        Assert.NotEqual(original, afterRename);

        File.Delete(renamed);
        var afterDelete = Print();
        Assert.NotEqual(afterRename, afterDelete);

        var project = Path.Combine(src.Root, "App.csproj");
        var projectTime = File.GetLastWriteTimeUtc(project);
        File.WriteAllText(project, "<Project Sdk=\"x\" />");
        File.SetLastWriteTimeUtc(project, projectTime);   // size alone must still tell
        Assert.NotEqual(afterDelete, Print());
    }
}
