using System.Security.Cryptography;

// Only explicitly declared docs/*.md paths may be inspected; never follow symlinks.
internal static class EvidenceSourceCheck
{
    internal sealed record Finding(string Id, string Source, string Status, string? ExpectedHash, string? ActualHash);

    public static IReadOnlyList<Finding> Check(ProjectEvidenceCatalog catalog, string root)
    {
        var basePath = Path.GetFullPath(root);
        return catalog.Items.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x =>
        {
            var path = Path.GetFullPath(Path.Combine(basePath, x.Source.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException($"unsafe evidence source '{x.Source}'");
            var relative = x.Source.Split('/');
            var current = basePath;
            foreach (var segment in relative)
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current))
                    return new Finding(x.Id, x.Source, "missing", x.SourceHash, null);
                var attr = File.GetAttributes(current);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException($"symlink evidence source rejected: {x.Source}");
            }
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new ArgumentException($"evidence source is not a file: {x.Source}");
            using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            var status = x.SourceHash is null ? "unverified" : actual == x.SourceHash ? "current" : "stale";
            return new Finding(x.Id, x.Source, status, x.SourceHash, actual);
        }).ToArray();
    }
}
