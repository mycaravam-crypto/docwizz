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
        if (script is null || !Directory.Exists(Path.Combine(Path.GetDirectoryName(script)!, "node_modules")))
        {
            Console.Error.WriteLine("docwizz: Vue/TS skipped — scanner-vue not found or not installed (npm ci in scanner-vue/)");
            return ([], []);
        }

        try
        {
            var p = Process.Start(new ProcessStartInfo("node", [script, Path.GetFullPath(root)])
                { RedirectStandardInput = true, RedirectStandardOutput = true })!;
            p.StandardInput.Write(string.Join('\n', files.Select(Path.GetFullPath)));
            p.StandardInput.Close();
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"exit code {p.ExitCode}");
            var m = JsonSerializer.Deserialize<FrontendModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return (m.Nodes, m.Edges);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"docwizz: Vue/TS skipped — scanner-vue failed: {e.Message}");
            return ([], []);
        }
    }

    // ponytail: found by walking up from the binary; ship it inside the tool package when publishing.
    static string? FindScanner()
    {
        if (Environment.GetEnvironmentVariable("DOCWIZZ_SCANNER_VUE") is { } env) return env;
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
