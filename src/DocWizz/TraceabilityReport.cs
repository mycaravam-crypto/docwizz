using System.Text;
using System.Text.Json;

// Deterministic views of explicit links; never infer technical or test coverage.
internal static class TraceabilityReport
{
    internal sealed record Row(string Requirement, string Source, string Version, string[] Architectures, string[] Tests, string Status);
    internal sealed record Report(IReadOnlyList<Row> Requirements, string[] UnlinkedArchitectures, string[] UntestedArchitectures);

    public static Report Build(ProjectEvidenceCatalog catalog)
    {
        var items = catalog.Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var byFrom = catalog.Links.GroupBy(x => x.From).ToDictionary(x => x.Key, x => x.Select(y => y.To).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        string[] Targets(string from) => byFrom.GetValueOrDefault(from) ?? [];
        var rows = catalog.Items.Where(x => x.Kind == "requirement").OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x =>
            {
                var architectures = Targets(x.Id);
                var tests = architectures.SelectMany(Targets).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new Row(x.Id, x.Source, x.Version, architectures, tests,
                    architectures.Length == 0 ? "no-architecture" : tests.Length == 0 ? "no-test" : "linked");
            }).ToArray();
        var linkedArchitectures = catalog.Links.Where(x => items[x.From].Kind == "requirement").Select(x => x.To).ToHashSet(StringComparer.Ordinal);
        var architecturesAll = catalog.Items.Where(x => x.Kind == "architecture").OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return new(rows,
            architecturesAll.Where(x => !linkedArchitectures.Contains(x.Id)).Select(x => x.Id).ToArray(),
            architecturesAll.Where(x => Targets(x.Id).Length == 0).Select(x => x.Id).ToArray());
    }

    public static string Json(Report report) => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n";

    public static string Markdown(Report report)
    {
        static string Safe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);
        var b = new StringBuilder("# Traceability (explicit links only)\n\n");
        b.AppendLine("This report does not verify requirements, tests, source freshness or V-Modell conformity.").AppendLine();
        b.AppendLine("| Requirement | Source | Architecture | Test | Status |").AppendLine("|---|---|---|---|---|");
        foreach (var x in report.Requirements)
            b.Append("| `").Append(x.Requirement).Append("` | `").Append(Safe(x.Source)).Append("@").Append(Safe(x.Version))
                .Append("` | ").Append(x.Architectures.Length == 0 ? "OFFEN" : string.Join(", ", x.Architectures.Select(y => "`" + y + "`")))
                .Append(" | ").Append(x.Tests.Length == 0 ? "OFFEN" : string.Join(", ", x.Tests.Select(y => "`" + y + "`")))
                .Append(" | ").Append(x.Status).AppendLine(" |");
        b.AppendLine().AppendLine("Unlinked architectures: " + (report.UnlinkedArchitectures.Length == 0 ? "none" : string.Join(", ", report.UnlinkedArchitectures)));
        b.AppendLine("Architectures without tests: " + (report.UntestedArchitectures.Length == 0 ? "none" : string.Join(", ", report.UntestedArchitectures)));
        return b.ToString();
    }
}
