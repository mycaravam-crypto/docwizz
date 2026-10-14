using System.Security.Cryptography;
using System.Text;

// MCP working-tree scans are reusable only while HEAD and the scanned file set are unchanged: same paths, sizes and
// mtimes, so deleted, renamed (mtime-preserving) and non-source inputs (.csproj, package.json, config) invalidate too.
// The fingerprint is taken before scanning, so an edit made during the scan forces the next request to rescan.
sealed class McpWorkingModelCache(Func<string?> head, Func<string> fingerprint, Func<CodeModel> scan)
{
    CodeModel? model;
    string? scannedHead;
    string? scannedFingerprint;

    public CodeModel Get(bool force = false)
    {
        var current = head();
        var files = fingerprint();
        if (!force && model is not null && current == scannedHead && files == scannedFingerprint)
            return model;
        model = scan();
        scannedHead = current;
        scannedFingerprint = files;
        return model;
    }

    // What a scan of `files` (as listed, under `root`) would read: relative path, size and mtime of each.
    public static string Fingerprint(string root, IEnumerable<string> files)
    {
        var text = new StringBuilder();
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            text.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append('\t');
            if (info.Exists) text.Append(info.Length).Append('\t').Append(info.LastWriteTimeUtc.Ticks);
            text.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
