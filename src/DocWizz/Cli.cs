// The command line as data: every command, the options it accepts, and a parser that rejects the rest, so `--html` on
// `analyze` is an error instead of silently doing nothing. Help output (Program.cs) is written from the same table, and
// CLI.md documents it.
static class Cli
{
    // `Options` is the help text; `Accepts` is what the parser allows (besides the global options).
    public record Command(string Syntax, string Purpose, string Use, string Example, string Options, string[] Accepts);

    public record Request(string? Command, Dictionary<string, string> Options, List<string> Positional, string? Error);

    public static readonly string[] Flags = ["ai", "html", "screenshots", "progress", "refresh-html-on-version-change", "timings", "validate", "force", "include-ai", "auto-rescan", "select-projects", "all-projects", "yes", "requirements", "help", "version"];

    // Valued option → what its value is, for the error when it is missing.
    public static readonly Dictionary<string, string> Valued = new()
    {
        ["format"] = "console or json", ["profile"] = $"a profile ({string.Join(", ", Profiles.Names)}) or a .yaml file",
        ["context"] = "a project context YAML file", ["since"] = "a git ref, e.g. origin/main", ["package"] = "a package name", ["to"] = "a version",
        ["for"] = "a symbol, file, folder or endpoint (\"POST /api/orders\")", ["hops"] = $"a number of hops, 1 to {AgentContext.MaxHops}",
        ["budget"] = "a number of tokens", ["work-item"] = "a work item id, e.g. 1234",
        ["seed"] = "a symbol id, qualified method name or endpoint (POST /api/orders)", ["depth"] = "the traversal depth (0 to 8)",
        ["limit"] = "the maximum number of technical symbols (1 to 500)", ["base-url"] = "a running application URL (http or https)",
        ["storage-state"] = "a Playwright storageState JSON file", ["screenshot-delay"] = "a number of milliseconds, 0 to 60000",
    };

    // Meaningful for every command.
    public static readonly string[] Global = ["help", "timings", "version"];

    // One line per command, in the order a new user meets them; `Use` says who it's for (CLI.md has the full matrix).
    public static readonly Dictionary<string, Command> Commands = new()
    {
        ["discover"] = new("discover [dir]", "read-only inventory of solutions, projects and references", "everyday",
            "docwizz discover . --format json", "--format console|json", ["format"]),
        ["setup"] = new("setup [dir]", "first run: detect the stack, write docwizz.yaml, analyze, generate docs, check", "start here",
            "docwizz setup . --select-projects", "--select-projects (TTY wizard), --all-projects (clear scope), --yes (no prompts), --force (overwrite docwizz.yaml), --html, --progress, --refresh-html-on-version-change, --ai, --profile <name|file>", ["select-projects", "all-projects", "yes", "force", "html", "progress", "refresh-html-on-version-change", "ai", "profile"]),
        ["generate"] = new("generate <dir> [out]", "write Markdown docs (default <dir>/docs)", "everyday",
            "docwizz generate . --html", "--html (HTML next to every page), --screenshots --base-url <url> [--storage-state <file>] [--screenshot-delay <ms>] (Playwright captures detected routes), --progress, --refresh-html-on-version-change, --ai (local Ollama drafts), --requirements (drafts also get each symbol's linked work items), --profile", ["html", "screenshots", "base-url", "storage-state", "screenshot-delay", "progress", "refresh-html-on-version-change", "ai", "requirements", "profile"]),
        ["check"] = new("check <dir> [--since <ref>]", "quality gate: exit 1 when thresholds fail (only new problems with --since)", "CI",
            "docwizz check . --since origin/main", "--since <ref>, --format json, --profile", ["since", "format", "profile"]),
        ["analyze"] = new("analyze <dir>", "documentation report: gaps, coverage, quality, architecture", "everyday",
            "docwizz analyze .", "--format json, --profile", ["format", "profile"]),
        ["architecture"] = new("architecture <dir>", "layers, layer dependencies, violations, cycles; exit 1 above check thresholds", "architects",
            "docwizz architecture .", "--format json, --profile", ["format", "profile"]),
        ["diff"] = new("diff [dir] [base] [head]", "changed symbols, affected pages and linked tests vs the last generate or git refs", "reviews, CI",
            "docwizz diff HEAD~1 HEAD", "--format json, --profile", ["format", "profile"]),
        ["context"] = new("context <dir> --for <target>", "token-budgeted context package for a coding agent: one symbol, file, folder or endpoint", "coding agents",
            "docwizz context . --for \"POST /api/materials\"", "--for <symbol|file|folder|VERB /route>, --hops <1-4>, --budget <tokens>, --format md|json, --include-ai, --requirements (linked work items from docwizz requirements import)",
            ["for", "hops", "budget", "format", "include-ai", "requirements"]),
        ["domain"] = new("domain discover <dir> (--seed <symbol|endpoint> | --work-item <id>)",
            "bounded, read-only technical graph walk with separately labelled historical requirement evidence", "architects",
            "docwizz domain discover . --seed \"POST /api/orders\" --depth 3 --limit 100 --format json",
            "--seed <symbol|endpoint> or --work-item <id>, --depth <0-8>, --limit <1-500>, --format console|md|json",
            ["seed", "work-item", "depth", "limit", "format"]),
        ["mcp"] = new("mcp <dir>", "read-only MCP server on stdio: coding agents query the code model (find_symbol, context, impact, check, ...)", "coding agents",
            "docwizz mcp .", "--auto-rescan (rescan when the model is stale)", ["auto-rescan"]),
        ["trace"] = new("trace <dir> <evidence.yaml>", "explicit evidence-chain gaps as Markdown or JSON", "product documents",
            "docwizz trace . project-evidence.yaml --format json", "--format json", ["format"]),
        ["evidence-diff"] = new("evidence-diff <dir> <before.yaml> <after.yaml>", "changed evidence, explicit relations and affected tests", "product documents",
            "docwizz evidence-diff . before.yaml after.yaml --format json", "--format json", ["format"]),
        ["product"] = new("product [check] <dir> <template.yaml> [out.md]", "render a product draft from code and project evidence (--ai: local model wording, cited); check: references and freshness", "product documents",
            "docwizz product . templates/vmodell-xt/sw-architecture.yaml --context project.yaml", "--context <file.yaml>, --ai (self-hosted model words each section from its evidence), --format json (product check)", ["context", "ai", "format"]),
        ["requirements"] = new("requirements import|show|query <dir> ...", "index exported commit/PR/work-item history (import), then the requirements of one symbol (show) or of a PR's changed symbols for gitwizz (query)", "coding agents, reviews",
            "docwizz requirements show . --for MaterialService.CreateAsync --format json", "import <dir> <history.json> [<work-items dir|file>]; show <dir> --for <symbol> | --work-item <id>, --format json; query <dir> <query.json> (gitwizz.requirement-query/v1 → gitwizz.requirement-input/v1)",
            ["for", "work-item", "format"]),
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
        if (opts.GetValueOrDefault("format") is { } format && format is not ("console" or "json") && !(format == "md" && cmd is "context" or "domain"))
            error ??= $"unknown format {format}: use console" + (cmd == "domain" ? ", md" : "") + " or json" + (cmd == "context" ? " (md is the same as console)" : "");
        if (cmd is not null && Commands.TryGetValue(cmd, out var c))
        {
            foreach (var o in opts.Keys.Where(o => !Global.Contains(o) && !c.Accepts.Contains(o)))
                error ??= $"--{o} doesn't apply to {cmd}; it applies to {string.Join(", ", Commands.Where(kv => kv.Value.Accepts.Contains(o)).Select(kv => kv.Key))}";
            if (opts.ContainsKey("select-projects") && (opts.ContainsKey("all-projects") || opts.ContainsKey("yes")))
                error ??= "--select-projects cannot be combined with --all-projects or --yes";
            if (opts.ContainsKey("package") != opts.ContainsKey("to"))
                error ??= "--package and --to go together: --package <name> --to <version>";
            if (cmd == "generate")
            {
                if (opts.ContainsKey("screenshots") && !opts.ContainsKey("base-url"))
                    error ??= "--screenshots requires --base-url <url>";
                if (!opts.ContainsKey("screenshots") && opts.ContainsKey("base-url"))
                    error ??= "--base-url requires --screenshots";
                if (!opts.ContainsKey("screenshots") && opts.ContainsKey("storage-state"))
                    error ??= "--storage-state requires --screenshots";
                if (!opts.ContainsKey("screenshots") && opts.ContainsKey("screenshot-delay"))
                    error ??= "--screenshot-delay requires --screenshots";
                if (opts.GetValueOrDefault("screenshot-delay") is { } delay && (!int.TryParse(delay, out var ms) || ms is < 0 or > 60000))
                    error ??= "--screenshot-delay must be a number of milliseconds, 0 to 60000";
                if (opts.GetValueOrDefault("base-url") is { } url &&
                    (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                     uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 ||
                     uri.Query.Length > 0 || uri.Fragment.Length > 0))
                    error ??= "--base-url must be an absolute http(s) URL without credentials, query or fragment";
            }
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
