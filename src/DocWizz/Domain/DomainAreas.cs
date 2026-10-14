using System.Text;
using System.Text.Json;

// A first domain-architecture layer over the unchanged code model (#187): methods grouped into candidate business areas.
// Every placement is inferred and carries the evidence it rests on, for a reviewer to accept or move; nothing is confirmed.
static class DomainAreas
{
    public const string Schema = "docwizz.domain-areas/v1";

    public record Member(string Id, string Location, string Reason);
    public record Area(string Name, string Status, List<Member> Members);
    public record Report(string Schema, string? Commit, List<Area> Areas);

    /// <summary>
    /// Groups production methods by folder, then moves two kinds of method to where their behaviour lives:
    /// a method in an outer folder (one no other folder calls into, e.g. controllers and jobs) that calls into exactly one
    /// other type joins that type's area, and an interface method joins its implementations' area when they agree.
    /// </summary>
    public static Report Group(CodeModel model)
    {
        var methods = model.Nodes.Where(n => n.Kind == "method" && n.Tags?.Contains("test") != true)
            .DistinctBy(n => n.Id).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var owner = model.Edges.Where(e => e.Kind == "contains" && methods.ContainsKey(e.To))
            .GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From, StringComparer.Ordinal);
        string Type(string m) => owner.GetValueOrDefault(m) ?? methods[m].File;
        string Folder(string m) => Path.GetDirectoryName(methods[m].File)?.Replace('\\', '/') is { Length: > 0 } d ? d : ".";

        var calls = model.Edges.Where(e => e.Kind == "calls" && e.From != e.To && methods.ContainsKey(e.From) && methods.ContainsKey(e.To))
            .GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var calledFromOutside = calls.SelectMany(c => c.Value.Where(to => Folder(to) != Folder(c.Key)).Select(Folder)).ToHashSet();

        var area = methods.Keys.ToDictionary(m => m, Folder, StringComparer.Ordinal);
        var reason = methods.Keys.ToDictionary(m => m, _ => "folder", StringComparer.Ordinal);
        // ponytail: one delegation hop; a wrapper calling a wrapper stays with the first. Follow chains if real code needs it.
        foreach (var m in methods.Keys.Order(StringComparer.Ordinal))
        {
            if (calledFromOutside.Contains(Folder(m)) || !calls.TryGetValue(m, out var to)) continue;
            var types = to.Select(Type).Distinct().ToList();
            if (types.Count == 1 && types[0] != Type(m))
                (area[m], reason[m]) = (Folder(to[0]), $"delegates to {types[0]}");
        }
        foreach (var g in model.Edges.Where(e => e.Kind == "implements" && methods.ContainsKey(e.From) && methods.ContainsKey(e.To)).GroupBy(e => e.To))
            if (g.Select(e => area[e.From]).Distinct().ToList() is [var only])
                (area[g.Key], reason[g.Key]) = (only, $"implemented by {string.Join(", ", g.Select(e => e.From).Order(StringComparer.Ordinal))}");

        var areas = area.GroupBy(a => a.Value).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Area(g.Key, "inferred", [.. g.Select(a => a.Key).Order(StringComparer.Ordinal)
                .Select(m => new Member(m, CodeModel.Location(methods[m]), reason[m]))]))
            .ToList();
        return new(Schema, model.Commit, areas);
    }

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Json(Report report) => JsonSerializer.Serialize(report, JsonOptions) + "\n";

    public static string Text(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Candidate business areas: {report.Areas.Count} (inferred from code structure, not confirmed)");
        foreach (var a in report.Areas)
        {
            sb.AppendLine($"{a.Name} ({a.Members.Count})");
            foreach (var m in a.Members)
                sb.AppendLine($"  {m.Id} ({m.Location})" + (m.Reason == "folder" ? "" : $": {m.Reason}"));
        }
        return sb.ToString();
    }
}
