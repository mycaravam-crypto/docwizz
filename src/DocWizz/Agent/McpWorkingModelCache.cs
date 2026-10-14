// MCP working-tree scans are reusable only while both HEAD and source mtimes stay unchanged.
// The timestamp is captured before scanning to catch edits made during the scan.
sealed class McpWorkingModelCache(Func<string?> head, Func<DateTime, int> changedSince, Func<CodeModel> scan)
{
    CodeModel? model;
    string? scannedHead;
    DateTime started;

    public CodeModel Get(bool force = false)
    {
        var current = head();
        if (!force && model is not null && current == scannedHead && changedSince(started) == 0)
            return model;
        var start = DateTime.UtcNow;
        var fresh = scan();
        model = fresh;
        started = start;
        scannedHead = current;
        return fresh;
    }
}
