// The command line as data: every command, the options it accepts, and a parser that rejects the rest, so `--html` on
// `analyze` is an error instead of silently doing nothing. Help output (Program.cs) is written from the same table, and
// CLI.md documents it.
static class Cli
{
    // `Options` is the help text; `Accepts` is what the parser allows (besides the global options).
    public record Command(string Syntax, string Purpose, string Use, string Example, string Options, string[] Accepts);

    public record Request(string? Command, Dictionary<string, string> Options, List<string> Positional, string? Error);

    public static readonly string[] Flags = ["ai", "html", "timings", "validate", "force", "help"];

    // Valued option → what its value is, for the error when it is missing.
    public static readonly Dictionary<string, string> Valued = new()
    {
        ["format"] = "console or json", ["profile"] = $"a profile ({string.Join(", ", Profiles.Names)}) or a .yaml file",
        ["since"] = "a git ref, e.g. origin/main", ["package"] = "a package name", ["to"] = "a version",
    };

    // Meaningful for every command.
    public static readonly string[] Global = ["help", "timings"];

    // One line per command, in the order a new user meets them; `Use` says who it's for (CLI.md has the full matrix).
    public static readonly Dictionary<string, Command> Commands = new()
    {
        ["setup"] = new("setup [dir]", "first run: detect the stack, write docwizz.yaml, analyze, generate docs, check", "start here",
            "docwizz setup .", "--force (overwrite docwizz.yaml), --html, --ai, --profile <name|file>", ["force", "html", "ai", "profile"]),
        ["generate"] = new("generate <dir> [out]", "write Markdown docs (default <dir>/docs)", "everyday",
            "docwizz generate . --html", "--html (HTML next to every page), --ai (local Ollama drafts), --profile", ["html", "ai", "profile"]),
        ["check"] = new("check <dir> [--since <ref>]", "quality gate: exit 1 when thresholds fail (only new problems with --since)", "CI",
            "docwizz check . --since origin/main", "--since <ref>, --format json, --profile", ["since", "format", "profile"]),
        ["analyze"] = new("analyze <dir>", "documentation report: gaps, coverage, quality, architecture", "everyday",
            "docwizz analyze .", "--format json, --profile", ["format", "profile"]),
        ["architecture"] = new("architecture <dir>", "layers, layer dependencies, violations, cycles; exit 1 above check thresholds", "architects",
            "docwizz architecture .", "--format json, --profile", ["format", "profile"]),
        ["diff"] = new("diff [dir] [base] [head]", "changed symbols, affected pages and linked tests vs the last generate or git refs", "reviews, CI",
            "docwizz diff HEAD~1 HEAD", "--format json, --profile", ["format", "profile"]),
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
        if (cmd is not null && cmd != "help" && !Commands.ContainsKey(cmd))
            error ??= $"unknown command {cmd}" + Suggest(cmd, Commands.Keys, "");
        if (opts.GetValueOrDefault("format") is { } format && format is not ("console" or "json"))
            error ??= $"unknown format {format}: use console or json";
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
