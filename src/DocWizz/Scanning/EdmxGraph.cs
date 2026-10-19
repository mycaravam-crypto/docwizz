using System.Globalization;
using System.Text;
using System.Text.Json;

// Deterministic, evidence-only technical projection of the P0 EDMX facts. Not a second domain engine.
static class EdmxGraph
{
    public const string Schema = "docwizz.edmx-graph/v1";

    public record Weights(decimal Degree = 1m, decimal FanIn = 1m, decimal FanOut = 1m, decimal Inheritance = 1m);
    public record AssociationEdge(string Id, string From, string To, string FromRole, string ToRole,
        string FromMultiplicity, string ToMultiplicity, string Cardinality, EdmxParser.Evidence Source,
        EdmxParser.Evidence FromSource, EdmxParser.Evidence ToSource);
    public record NavigationEdge(string Owner, string Name, string Target, string Association,
        EdmxParser.Evidence Source);
    public record InheritanceEdge(string Derived, string Base, EdmxParser.Evidence Source);
    public record EntityMetric(string Id, int Degree, int FanIn, int FanOut, int InheritanceLinks,
        decimal Score, bool Isolated, string Cluster);
    public record Cluster(string Id, List<string> Members);
    public record Report(string Schema, string File, Weights Weights, int EntityCount, int AssociationCount,
        int RelationshipCount, List<AssociationEdge> Associations, List<NavigationEdge> Navigations,
        List<InheritanceEdge> Inheritance, List<EntityMetric> Entities, List<Cluster> Clusters,
        List<string> CentralEntities, List<string> IsolatedEntities, List<EdmxParser.Finding> Findings);

    static string Cardinality(string from, string to) =>
        from == "*" && to == "*" ? "n:m" : from == "*" || to == "*" ? "1:n" : "1:1";

    static bool ValidMultiplicity(string value) => value is "1" or "0..1" or "*";

    public static bool TryWeights(IReadOnlyDictionary<string, string> options, out Weights weights, out string? error)
    {
        weights = new();
        string? failure = null;
        decimal Read(string option, decimal fallback)
        {
            if (!options.TryGetValue(option, out var text)) return fallback;
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value) || value is < 0m or > 100m)
            {
                failure ??= "--" + option + " must be a decimal from 0 to 100 (use '.' as separator)";
                return fallback;
            }
            return value;
        }
        weights = new(Read("degree-weight", 1m), Read("fan-in-weight", 1m),
            Read("fan-out-weight", 1m), Read("inheritance-weight", 1m));
        if (failure is null && weights.Degree + weights.FanIn + weights.FanOut + weights.Inheritance == 0m)
            failure = "at least one EDMX graph weight must be greater than zero";
        error = failure;
        return error is null;
    }

    /// <summary>
    /// Builds a source-backed, deterministic technical graph from conceptual EF EDMX facts only.
    /// As associations are not directed calls, degree counts association incidence while fan-in and fan-out
    /// count explicit, resolved navigation properties. No inferred domain boundaries are emitted.
    /// </summary>
    public static Report Build(EdmxParser.Report source, Weights? weights = null)
    {
        weights ??= new();
        if (new[] { weights.Degree, weights.FanIn, weights.FanOut, weights.Inheritance }
            .Any(x => x is < 0m or > 100m) ||
            weights.Degree + weights.FanIn + weights.FanOut + weights.Inheritance == 0m)
            throw new ArgumentOutOfRangeException(nameof(weights), "Weights must be in [0,100] with at least one positive weight");

        var findings = new List<EdmxParser.Finding>(source.Findings);
        void Warn(string message, EdmxParser.Evidence at) =>
            findings.Add(new("EDMX_GRAPH_REFERENCE", "warning", message, at));
        var entities = source.Entities.GroupBy(e => e.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var group in entities.Where(g => g.Count() != 1))
            Warn("Duplicate conceptual entity ID: " + group.Key, group.First().Source);
        var types = entities.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var associationEdges = new List<AssociationEdge>();
        foreach (var group in source.Associations.GroupBy(a => a.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var definitions = group.ToArray();
            if (definitions.Length != 1)
            {
                Warn("Duplicate association ID: " + group.Key, definitions[0].Source);
                continue;
            }
            var association = definitions[0];
            if (association.Ends.Count != 2 ||
                association.Ends.Any(e => !types.ContainsKey(e.EntityType) || !ValidMultiplicity(e.Multiplicity)) ||
                association.Ends[0].Role == association.Ends[1].Role)
            {
                Warn("Association cannot be resolved as an evidenced binary relationship: " + association.Id,
                    association.Source);
                continue;
            }
            // Canonical direction is lexicographic ONLY for storage; associations have no causal direction.
            var ends = association.Ends.OrderBy(e => e.EntityType, StringComparer.Ordinal)
                .ThenBy(e => e.Role, StringComparer.Ordinal).ToArray();
            associationEdges.Add(new(association.Id, ends[0].EntityType, ends[1].EntityType,
                ends[0].Role, ends[1].Role, ends[0].Multiplicity, ends[1].Multiplicity,
                Cardinality(ends[0].Multiplicity, ends[1].Multiplicity),
                association.Source, ends[0].Source, ends[1].Source));
        }

        var validAssociations = associationEdges.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var navigations = new List<NavigationEdge>();
        foreach (var n in source.Navigations.OrderBy(n => n.Owner, StringComparer.Ordinal)
            .ThenBy(n => n.Name, StringComparer.Ordinal))
        {
            if (!types.ContainsKey(n.Owner) || !validAssociations.TryGetValue(n.Relationship, out var a))
            {
                Warn("Navigation has no resolved conceptual owner/association: " + n.Owner + "." + n.Name, n.Source);
                continue;
            }
            string? target = null;
            if (a.From == n.Owner && a.FromRole == n.FromRole && a.ToRole == n.ToRole)
                target = a.To;
            else if (a.To == n.Owner && a.ToRole == n.FromRole && a.FromRole == n.ToRole)
                target = a.From;
            if (target is null)
            {
                Warn("Navigation roles do not match the association ends: " + n.Owner + "." + n.Name, n.Source);
                continue;
            }
            navigations.Add(new(n.Owner, n.Name, target, n.Relationship, n.Source));
        }

        var inheritance = new List<InheritanceEdge>();
        foreach (var entity in types.Values.Where(e => e.BaseType is { Length: > 0 })
            .OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var baseId = entity.BaseType!;
            if (types.ContainsKey(baseId))
                inheritance.Add(new(entity.Id, baseId, entity.Source));
            else
                Warn("Unresolved base entity: " + entity.Id + " -> " + baseId, entity.Source);
        }

        // Components are WEAKLY connected through undirected association and inheritance links.
        var neighbours = types.Keys.ToDictionary(id => id, _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var a in associationEdges)
        {
            neighbours[a.From].Add(a.To);
            neighbours[a.To].Add(a.From);
        }
        foreach (var edge in inheritance)
        {
            neighbours[edge.Derived].Add(edge.Base);
            neighbours[edge.Base].Add(edge.Derived);
        }
        var components = new List<List<string>>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in types.Keys.Order(StringComparer.Ordinal).Where(id => !visited.Contains(id)))
        {
            visited.Add(id);
            var stack = new Stack<string>();
            stack.Push(id);
            var members = new List<string>();
            while (stack.TryPop(out var current))
            {
                members.Add(current);
                foreach (var next in neighbours[current].Where(next => !visited.Contains(next)))
                {
                    visited.Add(next);
                    stack.Push(next);
                }
            }
            members.Sort(StringComparer.Ordinal);
            components.Add(members);
        }
        components.Sort((a, b) => StringComparer.Ordinal.Compare(a[0], b[0]));
        var clusters = components.Select((members, index) =>
            new Cluster("component-" + (index + 1).ToString("D3", CultureInfo.InvariantCulture), members)).ToList();
        var clusterOf = clusters.SelectMany(c => c.Members.Select(id => (id, c.Id)))
            .ToDictionary(x => x.id, x => x.Id, StringComparer.Ordinal);

        var degree = types.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var fanIn = types.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var fanOut = types.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var inheritanceLinks = types.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var a in associationEdges)
        {
            degree[a.From]++;
            if (a.From != a.To) degree[a.To]++;
        }
        foreach (var n in navigations)
        {
            fanOut[n.Owner]++;
            fanIn[n.Target]++;
        }
        foreach (var edge in inheritance)
        {
            inheritanceLinks[edge.Derived]++;
            if (edge.Derived != edge.Base) inheritanceLinks[edge.Base]++;
        }
        var metrics = types.Keys.Order(StringComparer.Ordinal).Select(id =>
        {
            var score = degree[id] * weights.Degree + fanIn[id] * weights.FanIn +
                fanOut[id] * weights.FanOut + inheritanceLinks[id] * weights.Inheritance;
            return new EntityMetric(id, degree[id], fanIn[id], fanOut[id], inheritanceLinks[id],
                score, degree[id] + fanIn[id] + fanOut[id] + inheritanceLinks[id] == 0, clusterOf[id]);
        }).ToList();
        var max = metrics.Count == 0 ? 0m : metrics.Max(e => e.Score);
        return new(Schema, source.File, weights, source.Entities.Count, source.Associations.Count,
            associationEdges.Count + inheritance.Count, associationEdges, navigations, inheritance,
            metrics, clusters,
            [.. metrics.Where(e => max > 0m && e.Score == max).Select(e => e.Id)],
            [.. metrics.Where(e => e.Isolated).Select(e => e.Id)],
            [.. findings.OrderBy(f => f.Source.File, StringComparer.Ordinal)
                .ThenBy(f => f.Source.Line).ThenBy(f => f.Code, StringComparer.Ordinal)
                .ThenBy(f => f.Message, StringComparer.Ordinal)]);
    }

    public static string Json(Report report) => JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    { WriteIndented = true }) + "\n";

    public static string Text(Report report)
    {
        var text = new StringBuilder();
        text.AppendLine("EDMX technical graph " + report.File + " (" + Schema + "): " +
            report.EntityCount + " entities; " + report.Associations.Count + " resolved associations; " +
            report.Inheritance.Count + " inheritance links; " + report.Clusters.Count + " components");
        text.AppendLine("Weights: degree=" + report.Weights.Degree.ToString(CultureInfo.InvariantCulture) +
            ", fan-in=" + report.Weights.FanIn.ToString(CultureInfo.InvariantCulture) +
            ", fan-out=" + report.Weights.FanOut.ToString(CultureInfo.InvariantCulture) +
            ", inheritance=" + report.Weights.Inheritance.ToString(CultureInfo.InvariantCulture));
        foreach (var edge in report.Associations)
            text.AppendLine(edge.Id + " " + edge.From + " [" + edge.FromMultiplicity + "] -- " +
                edge.To + " [" + edge.ToMultiplicity + "] (" + edge.Cardinality + ") @" +
                edge.Source.File + ":" + edge.Source.Line);
        foreach (var metric in report.Entities)
            text.AppendLine(metric.Id + ": degree=" + metric.Degree + ", in=" + metric.FanIn +
                ", out=" + metric.FanOut + ", inheritance=" + metric.InheritanceLinks +
                ", technicalScore=" + metric.Score.ToString(CultureInfo.InvariantCulture) +
                ", cluster=" + metric.Cluster);
        foreach (var finding in report.Findings)
            text.AppendLine(finding.Severity + " " + finding.Code + " " +
                finding.Source.File + ":" + finding.Source.Line + ": " + finding.Message);
        text.AppendLine("Technical scores and components are NOT confirmed business importance or bounded contexts.");
        return text.ToString();
    }
}
