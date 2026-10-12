// Records the generator version that last successfully produced HTML at an output path.
// The stamp is independent of git's source-code commit and never changes hand-authored docs.
static class HtmlVersion
{
    public static string PathFor(string outDir) => Path.Combine(outDir, ".docwizz", "html-version.txt");

    // Missing or malformed stamps (including projects upgraded from older docwizz versions)
    // are treated as unknown: opting into a version refresh regenerates every HTML page once.
    public static bool NeedsRefresh(string outDir, string version)
    {
        var file = PathFor(outDir);
        return !File.Exists(file) || !string.Equals(File.ReadAllText(file).Trim(), version, StringComparison.Ordinal);
    }

    public static void Record(string outDir, string version)
    {
        var file = PathFor(outDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (NeedsRefresh(outDir, version)) File.WriteAllText(file, version + "\n");
    }
}
