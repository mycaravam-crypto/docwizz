using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

record FrontendModel(List<Node> Nodes, List<Edge> Edges);

static class Frontend
{
    // Runs scanner-vue (Node) over .vue/.ts files. Missing node or scanner → warn and skip, backend still works.
    public static (List<Node>, List<Edge>) Scan(string root, List<string> files)
    {
        if (files.Count == 0) return ([], []);
        var script = FindScanner();
        if (script is null || !ScannerAvailable(script))
        {
            Console.Error.WriteLine($"docwizz: Vue/TS skipped — {NotInstalled}");
            return ([], []);
        }

        try
        {
            var psi = new ProcessStartInfo("node", [script, Path.GetFullPath(root)]) { RedirectStandardInput = true, RedirectStandardOutput = true };
            // Node 22.1+ keeps compiled TypeScript and Vue compiler code here, so later runs skip most of loading them
            // (older Node ignores it). Anyone who set their own cache directory keeps it.
            if (Environment.GetEnvironmentVariable("NODE_COMPILE_CACHE") is null)
                psi.Environment["NODE_COMPILE_CACHE"] = Path.Combine(Path.GetTempPath(), "docwizz-node-compile-cache");
            var json = ScannerProcess.Run(psi, string.Join('\n', files.Select(Path.GetFullPath)));
            var m = JsonSerializer.Deserialize<FrontendModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return (m.Nodes, m.Edges);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"docwizz: Vue/TS skipped — scanner-vue failed: {e.Message}");
            return ([], []);
        }
    }

    internal const string NotInstalled =
        "scanner-vue not found or not installed (reinstall the tool, or run npm ci in scanner-vue/ of a source checkout)";

    // The source checkout and the packaged copy both keep their dependencies next to the entry point.
    internal static bool ScannerAvailable(string script) => File.Exists(script) &&
        Directory.Exists(Path.Combine(Path.GetDirectoryName(script)!, "node_modules"));

    // scanner-vue/index.mjs (DOCWIZZ_SCANNER_VUE overrides); java.mjs sits next to it. An installed tool carries it in
    // scanner-vue/dist next to the binary (DocWizz.csproj stages it on pack); otherwise it is found by walking up.
    public static string? FindScanner()
    {
        if (Environment.GetEnvironmentVariable("DOCWIZZ_SCANNER_VUE") is { } env) return env;
        var bundled = Path.Combine(AppContext.BaseDirectory, "scanner-vue", "dist", "index.mjs");
        if (ScannerAvailable(bundled)) return bundled;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scanner-vue", "index.mjs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Points `http:GET /api/x/{}` edges at the matching backend endpoint node, when there is one.
    public static List<Edge> LinkHttp(List<Node> nodes, List<Edge> edges)
    {
        var index = new Dictionary<string, string>();
        foreach (var n in nodes.Where(n => n.Tags is ["endpoint", _, ..]))
            index.TryAdd(Key(n.Tags![1], n.Route ?? ""), n.Id);

        return edges.Select(e => e.Kind == "http" && e.To.Split(' ', 2) is [var verb, var url]
            && index.TryGetValue(Key(verb[5..], url), out var target) ? e with { To = target } : e).ToList();
    }

    static string Key(string verb, string route) =>
        verb.ToUpperInvariant() + " /" + Regex.Replace(route.TrimStart('~').Trim('/').ToLowerInvariant(), @"\{[^}]*\}", "{}");
}
