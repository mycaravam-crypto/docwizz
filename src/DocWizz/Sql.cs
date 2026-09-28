using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

// .sql files: stored procedures, functions, views, triggers and tables as nodes (`sql:<name>`, lowercase, no brackets),
// with the comment block above a CREATE as its doc and `@parameters`. A routine `accesses` the tables it reads or writes
// and `calls` the procedures it EXECs. Migration files (a migrations folder, Flyway V1__x.sql) are `migration` nodes.
// Code reaches a procedure through `sqlref:<name>` edges (CSharpScanner), resolved here by full or bare name.
// ponytail: regex over the text, no T-SQL/PL/pgSQL parser; dynamic SQL and CTE names stay invisible.
static class Sql
{
    static readonly Regex Create = new(
        @"^[ \t]*(?:CREATE(?:\s+OR\s+(?:ALTER|REPLACE))?|ALTER)\s+(PROCEDURE|PROC|FUNCTION|VIEW|TABLE|TRIGGER)\s+((?:[\w\[\]""`]+\.)?[\w\[\]""`]+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex Tables = new(@"\b(?:FROM|JOIN|INTO|UPDATE|MERGE(?:\s+INTO)?|DELETE\s+FROM|TRUNCATE\s+TABLE)\s+((?:[\w\[\]""`]+\.)?[\w\[\]""`]+)",
        RegexOptions.IgnoreCase);
    static readonly Regex Exec = new(@"\bEXEC(?:UTE)?\s+((?:[\w\[\]""`]+\.)?[\w\[\]""`]+)", RegexOptions.IgnoreCase);

    public static string Name(string raw) => Regex.Replace(raw, @"[\[\]""`]", "");
    static string Id(string name) => $"sql:{Name(name).ToLowerInvariant()}";

    public static bool IsMigration(string rel) =>
        rel.Split('/').SkipLast(1).Any(d => d.Equals("migrations", StringComparison.OrdinalIgnoreCase)) || Regex.IsMatch(Path.GetFileName(rel), @"^[VU]\d+(_\d+)*__");

    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files)
    {
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var bodies = new List<(string Id, string Body)>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;
            var migration = IsMigration(rel) ? $"sql:migration:{rel.ToLowerInvariant()}" : null;
            if (migration is not null)
                nodes.Add(new Node(migration, "migration", Path.GetFileNameWithoutExtension(file), rel, 1, Doc: Doc(text, 0), Hash: Hash(text),
                    EndLine: LineOf(text.Length)));

            var creates = Create.Matches(text).ToList();
            for (var i = 0; i < creates.Count; i++)
            {
                var m = creates[i];
                var end = i + 1 < creates.Count ? creates[i + 1].Index : text.Length;
                var body = text[m.Index..end];
                var name = Name(m.Groups[2].Value);
                var kind = m.Groups[1].Value.ToUpperInvariant() switch
                {
                    "PROCEDURE" or "PROC" => "procedure", "FUNCTION" => "sql-function", "VIEW" => "sql-view", "TRIGGER" => "trigger", _ => "table",
                };
                var id = Id(name);
                // Routine parameters: `@name type` in the header, before AS/BEGIN.
                var header = Regex.Split(body, @"\b(?:AS|BEGIN|RETURNS)\b", RegexOptions.IgnoreCase)[0];
                var ps = kind is "procedure" or "sql-function"
                    ? Regex.Matches(header, @"(@\w+)\s+([\w]+(?:\s*\([\d\s,]+\))?)").Select(p => $"{p.Groups[1].Value}: {p.Groups[2].Value}").ToList() : [];
                nodes.Add(new Node(id, kind, name.Split('.').Last(), rel, LineOf(m.Index), "public", Doc(text, m.Index),
                    Complexity: kind == "table" ? null : 1 + Regex.Matches(body, @"\b(?:IF|WHILE|CASE\s+WHEN|WHEN)\b", RegexOptions.IgnoreCase).Count,
                    Params: ps.Count > 0 ? ps.Count : null, Hash: Hash(body), EndLine: LineOf(end - 1), Parameters: ps.Count > 0 ? ps : null));
                if (kind != "table") bodies.Add((id, body[m.Length..]));
            }
        }

        // What each routine touches, among the tables and procedures found.
        var byName = Index(nodes);
        foreach (var (id, body) in bodies)
        {
            var code = Regex.Replace(body, @"--[^\n]*|/\*.*?\*/|'(?:[^']|'')*'", " ", RegexOptions.Singleline);
            foreach (var t in Tables.Matches(code).Select(x => Resolve(byName, x.Groups[1].Value)).OfType<Node>().Where(n => n.Kind is "table" or "sql-view").DistinctBy(n => n.Id))
                if (t.Id != id) edges.Add(new(id, t.Id, "accesses"));
            foreach (var p in Exec.Matches(code).Select(x => Resolve(byName, x.Groups[1].Value)).OfType<Node>().Where(n => n.Kind == "procedure").DistinctBy(n => n.Id))
                edges.Add(new(id, p.Id, "calls"));
        }
        return (CodeModel.MergeHashes(nodes).DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
    }

    // Code → procedure: `sqlref:<name>` edges point at the procedure node, or are dropped when there is none.
    public static List<Edge> Link(List<Node> nodes, List<Edge> edges)
    {
        var byName = Index(nodes.Where(n => n.Kind is "procedure" or "sql-function"));
        return edges.Select(e => e.To.StartsWith("sqlref:") ? Resolve(byName, e.To[7..]) is { } p ? e with { To = p.Id } : null : e).OfType<Edge>().Distinct().ToList();
    }

    static ILookup<string, Node> Index(IEnumerable<Node> nodes) =>
        nodes.Where(n => n.Id.StartsWith("sql:") && n.Kind != "migration")
            .SelectMany(n => new[] { (Key: n.Id[4..], Node: n), (Key: n.Id[4..].Split('.').Last(), Node: n) }).Distinct().ToLookup(x => x.Key, x => x.Node);

    // Full name first (dbo.GetStock), else the bare name when exactly one object has it.
    static Node? Resolve(ILookup<string, Node> byName, string raw)
    {
        var name = Name(raw).ToLowerInvariant();
        return byName[name].FirstOrDefault(n => n.Id == $"sql:{name}") ?? (byName[name.Split('.').Last()].ToList() is [var only] ? only : null);
    }

    // `-- comment` lines or a `/* block */` right above the statement (or at the top of a migration) → <summary>.
    static string? Doc(string text, int index)
    {
        var before = text[..index].TrimEnd();
        var block = Regex.Match(before, @"/\*(.*?)\*/\z", RegexOptions.Singleline);
        var lines = block.Success ? [block.Groups[1].Value]
            : before.Split('\n').Reverse().TakeWhile(l => l.TrimStart().StartsWith("--")).Reverse().Select(l => l.TrimStart()[2..]).ToList();
        if (index == 0) lines = text.Split('\n').TakeWhile(l => l.TrimStart().StartsWith("--")).Select(l => l.TrimStart()[2..]).ToList();
        var summary = Regex.Replace(string.Join(" ", lines).Replace("*", " "), @"\s+", " ").Trim();
        return summary.Length > 0 ? $"<member><summary>{SecurityElement.Escape(summary)}</summary></member>" : null;
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim())))[..12].ToLowerInvariant();
}
