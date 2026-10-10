// The command line as data: every command, the options it accepts, and a parser that rejects the rest, so `--html` on
// `analyze` is an error instead of silently doing nothing. Help output (Program.cs) is written from the same table, and
// CLI.md documents it.
static class Cli
{
    // `Options` is the help text; `Accepts` is what the parser allows (besides the global options).
    public record Command(string Syntax, string Purpose, string Use, string Example, string Options, string[] Accepts);

    public record Request(string? Command, Dictionary<string, string> Options, List<string> Positional, string? Error);

    public static readonly string[] Flags = ["ai", "html", "progress", "refresh-html-on-version-change", "timings", "validate", "force", "include-ai", "auto-rescan", "help", "version"];

    // Valued option → what its value is, for the error when it is missing.
    public static readonly Dictionary<string, string> Valued = new()
    {
        ["format"] = "console or json", ["profile"] = $"a profile ({string.Join(", ", Profiles.Names)}) or a .yaml file",
        ["context"] = "a project context YAML file", ["since"] = "a git ref, e.g. origin/main", ["package"] = "a package name", ["to"] = "a version",
        ["for"] = "a symbol, file, folder or endpoint (\"POST /api/orders\")", ["hops"] = $"a number of hops, 1 to {AgentContext.MaxHops}",
        ["budget"] = "a number of tokens",
    };

    // Meaningful for every command.
    public static readonly string[] Global = ["help", "timings", "version"];

    // One line per command, in the order a new user meets them; `Use` says who it's for (CLI.md has the full matrix).
    public static readonly Dictionary<string, Command> Commands = new()
    {
        ["setup"] = new("setup [dir]", "first run: detect the stack, write docwizz.yaml, analyze, generate docs, check", "start here",
            "docwizz setup .", "--force (overwrite docwizz.yaml), --html, --progress, --refresh-html-on-version-change, --ai, --profile <name|file>", ["force", "html", "progress", "refresh-html-on-version-change", "ai", "profile"]),
        ["generate"] = new("generate <dir> [out]", "write Markdown docs (default <dir>/docs)", "everyday",
            "docwizz generate . --html", "--html (HTML next to every page), --progress, --refresh-html-on-version-change, --ai (local Ollama drafts), --profile", ["html", "progress", "refresh-html-on-version-change", "ai", "profile"]),
        ["check"] = new("check <dir> [--since <ref>]", "quality gate: exit 1 when thresholds fail (only new problems with --since)", "CI",
            "docwizz check . --since origin/main", "--since <ref>, --format json, --profile", ["since", "format", "profile"]),
        ["analyze"] = new("analyze <dir>", "documentation report: gaps, coverage, quality, architecture", "everyday",
            "docwizz analyze .", "--format json, --profile", ["format", "profile"]),
        ["architecture"] = new("architecture <dir>", "layers, layer dependencies, violations, cycles; exit 1 above check thresholds", "architects",
            "docwizz architecture .", "--format json, --profile", ["format", "profile"]),
        ["diff"] = new("diff [dir] [base] [head]", "changed symbols, affected pages and linked tests vs the last generate or git refs", "reviews, CI",
            "docwizz diff HEAD~1 HEAD", "--format json, --profile", ["format", "profile"]),
        ["context"] = new("context <dir> --for <target>", "token-budgeted context package for a coding agent: one symbol, file, folder or endpoint", "coding agents",
            "docwizz context . --for \"POST /api/materials\"", "--for <symbol|file|folder|VERB /route>, --hops <1-4>, --budget <tokens>, --format md|json, --include-ai",
            ["for", "hops", "budget", "format", "include-ai"]),
        ["mcp"] = new("mcp <dir>", "read-only MCP server on stdio: coding agents query the code model (find_symbol, context, impact, check, ...)", "coding agents",
            "docwizz mcp .", "--auto-rescan (rescan when the model is stale)", ["auto-rescan"]),
        ["product"] = new("product <dir> <template.yaml> [out.md]", "render a product draft from code and project evidence (--ai: local model wording, cited)", "product documents",
            "docwizz product . templates/vmodell-xt/sw-architecture.yaml --context project.yaml", "--context <file.yaml>, --ai (self-hosted model words each section from its evidence)", ["context", "ai"]),
        ["sbom"] = new("sbom [dir] [out]", "export direct manifest dependencies as CycloneDX 1.6 JSON", "supply-chain inventory",
            "docwizz sbom . sbom.cdx.json", "no options", []),
        ["remediate"] = new("remediate <dir>", "package update suggestions: command or patch, impact, confidence", "maintenance",
            "docwizz remediate . --validate", "--package <name> --to <version>, --validate (build and test in a temporary copy), --since <ref>, --format json",
            ["package", "to", "validate", "since", "format"]),
        ["init"] = new("init [dir]", "write a starter docwizz.yaml with every default (setup does this from evidence)", "manual setup",
            "docwizz init .", "", []),
        ["scan"] = new("scan <dir> [model.json]", "dump the raw code model (nodes and edges)", "advanced, debugging",
            "docwizz scan . model.json", "", []),
    };

    // The first problem wins; parsing still runs to the end so the error can point at the command's help.
    public static Request Parse(IReadOnlyList<string> args)
    {
        var opts = new Dictionary<string, string>();
        var pos = new List<string>();
        string? error = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "-h") opts["help"] = "";
            else if (a == "-v") opts["version"] = "";
            else if (!a.StartsWith("--")) pos.Add(a);
            else if (Flags.Contains(a[2..])) opts[a[2..]] = "";
            else if (Valued.TryGetValue(a[2..], out var what))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--")) opts[a[2..]] = args[++i];
                else error ??= $"{a} needs a value: {what}";
            }
            else error ??= $"unknown option {a}" + Suggest(a[2..].Split('=')[0], [.. Flags, .. Valued.Keys], "--");
        }
        var cmd = pos.ElementAtOrDefault(0);
        if (cmd is not null && cmd is not ("help" or "version") && !Commands.ContainsKey(cmd))
            error ??= $"unknown command {cmd}" + Suggest(cmd, Commands.Keys, "");
        if (opts.GetValueOrDefault("format") is { } format && format is not ("console" or "json") && !(format == "md" && cmd == "context"))
            error ??= $"unknown format {format}: use console or json" + (cmd == "context" ? " (md is the same as console)" : "");
        if (cmd is not null && Commands.TryGetValue(cmd, out var c))
        {
            foreach (var o in opts.Keys.Where(o => !Global.Contains(o) && !c.Accepts.Contains(o)))
                error ??= $"--{o} doesn't apply to {cmd}; it applies to {string.Join(", ", Commands.Where(kv => kv.Value.Accepts.Contains(o)).Select(kv => kv.Key))}";
            if (opts.ContainsKey("package") != opts.ContainsKey("to"))
                error ??= "--package and --to go together: --package <name> --to <version>";
        }
        return new(cmd, opts, pos, error);
    }

    // `; did you mean --format?` for a likely typo (edit distance ≤ 2), else nothing.
    static string Suggest(string typed, IEnumerable<string> known, string prefix) =>
        known.Select(k => (k, d: Distance(typed, k))).Where(x => x.d <= 2 && x.d < x.k.Length).OrderBy(x => x.d).ThenBy(x => x.k, StringComparer.Ordinal)
            .Select(x => $"; did you mean {prefix}{x.k}?").FirstOrDefault() ?? "";

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cur = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = cur;
            }
        }
        return row[b.Length];
    }
}
