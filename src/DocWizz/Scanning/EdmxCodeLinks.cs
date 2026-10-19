using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// P2: evidence-only bridge from EDMX/P1 to the existing C# CodeModel and #187 DomainAreas.
// CodeModel edges are scanner observations, not invented call-sites or DDD assertions.
static class EdmxCodeLinks
{
    public const string Schema = "docwizz.edmx-code-links/v1";
    public const int MaxDepth = 8;
    public const int MaxVisitedPerAction = 5000;
    public const int MaxTraces = 200;

    [JsonConverter(typeof(JsonStringEnumConverter<Status>))]
    public enum Status
    {
        [JsonStringEnumMemberName("observed")] Observed,
        [JsonStringEnumMemberName("unknown")] Unknown,
        [JsonStringEnumMemberName("inferred")] Inferred
    }

    [JsonConverter(typeof(JsonStringEnumConverter<LinkKind>))]
    public enum LinkKind
    {
        [JsonStringEnumMemberName("dbcontext-persists")] DbContextPersists,
        [JsonStringEnumMemberName("entity-class")] EntityClass
    }

    [JsonConverter(typeof(JsonStringEnumConverter<UnknownKind>))]
    public enum UnknownKind
    {
        [JsonStringEnumMemberName("edmx-entity")] EdmxEntity,
        [JsonStringEnumMemberName("csharp-type")] CSharpType,
        [JsonStringEnumMemberName("dbcontext")] DbContext,
        [JsonStringEnumMemberName("controller-path")] ControllerPath
    }

    public record Link(string Entity, LinkKind Kind, string CodeSymbol, Status Status,
        EdmxParser.Evidence EdmxSource, EdmxParser.Evidence CodeSource);
    public record Hop(string From, string To, string Kind, EdmxParser.Evidence FromDeclaration,
        EdmxParser.Evidence ToDeclaration);
    public record Trace(string Entity, string Controller, string Action, string? Route, Status RouteStatus,
        List<Hop> Hops, EdmxParser.Evidence EdmxSource, Status Status);
    public record Unknown(string Entity, UnknownKind Kind, Status Status, string Reason, EdmxParser.Evidence Source);
    public record DomainHint(string Area, Status Status, string Entity, string CodeSymbol,
        string Reason, EdmxParser.Evidence EdmxSource, EdmxParser.Evidence CodeSource);
    public record Report(string Schema, string File, string GraphSchema, string CodeModelKind,
        List<Link> Links, List<Trace> Traces, List<Unknown> Unknowns,
        List<DomainHint> DomainEvidence, List<EdmxParser.Finding> Findings);

    private sealed record Matches(
        Dictionary<string, EdmxParser.Entity> Entities,
        Dictionary<string, Node> UniqueCode,
        Dictionary<string, Node> ExactTypes,
        Dictionary<string, List<Node>> Contexts,
        List<Link> Links,
        List<Unknown> Unknowns);

    static EdmxParser.Evidence At(Node node) => new(node.File.Replace('\\', '/'), node.Line);

    // Only scanner-observed control and access dependencies. No route-name similarity.
    static readonly HashSet<string> Traversable = ["calls", "accesses", "injects", "creates"];

    /// <summary>
    /// Connects exact EDMX entity declarations to observed C# classes, persistence and MVC paths.
    /// Any domain-area terms are obtained from #187 DomainAreas.Group and remain inferred.
    /// </summary>
    public static Report Build(EdmxParser.Report facts, CodeModel code)
    {
        var matches = MatchExactEntityAndContextLinks(facts, code);
        var findings = new List<EdmxParser.Finding>(facts.Findings);
        var traces = TraverseControllerPaths(code, matches, findings);
        var hints = ProjectDomainHints(code, traces, matches.UniqueCode);
        foreach (var entity in matches.Contexts.Keys.Order(StringComparer.Ordinal)
            .Where(id => !traces.Any(t => t.Entity == id)))
            matches.Unknowns.Add(new(entity, UnknownKind.ControllerPath, Status.Unknown,
                "No observed controller/action dependency path to DbContext", matches.Entities[entity].Source));

        return new(Schema, facts.File, EdmxGraph.Schema, "csharp-code-model",
            [.. matches.Links.Distinct().OrderBy(x => x.Entity, StringComparer.Ordinal)
                .ThenBy(x => x.Kind).ThenBy(x => x.CodeSymbol, StringComparer.Ordinal)],
            [.. traces.DistinctBy(t => (t.Entity, t.Controller, t.Action, t.Hops.Count))
                .OrderBy(t => t.Entity, StringComparer.Ordinal).ThenBy(t => t.Controller, StringComparer.Ordinal)
                .ThenBy(t => t.Action, StringComparer.Ordinal)],
            [.. matches.Unknowns.OrderBy(x => x.Entity, StringComparer.Ordinal).ThenBy(x => x.Kind)],
            [.. hints.Distinct().OrderBy(x => x.Area, StringComparer.Ordinal)
                .ThenBy(x => x.Entity, StringComparer.Ordinal).ThenBy(x => x.CodeSymbol, StringComparer.Ordinal)],
            [.. findings.OrderBy(x => x.Source.File, StringComparer.Ordinal)
                .ThenBy(x => x.Source.Line).ThenBy(x => x.Code, StringComparer.Ordinal)]);
    }

    static Matches MatchExactEntityAndContextLinks(EdmxParser.Report facts, CodeModel code)
    {
        var links = new List<Link>();
        var unknowns = new List<Unknown>();
        var entityGroups = facts.Entities.GroupBy(e => e.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var entityById = entityGroups.Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        foreach (var group in entityGroups.Where(g => g.Count() != 1))
            unknowns.Add(new(group.Key, UnknownKind.EdmxEntity, Status.Unknown, "Duplicate conceptual entity identifier",
                group.First().Source));

        var codeGroups = code.Nodes.Where(n => n.Language is null or "csharp" && n.Id.StartsWith("cs:", StringComparison.Ordinal))
            .GroupBy(n => n.Id, StringComparer.Ordinal).ToList();
        // Language may be null on scanner symbols; IDs still enforce the C# namespace.
        var uniqueCode = codeGroups.Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var duplicateCode = codeGroups.Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var exact = new Dictionary<string, Node>(StringComparer.Ordinal);
        var contextsByEntity = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (var entity in entityById.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var classId = "cs:" + entity.Id;
            if (duplicateCode.Contains(classId) || !uniqueCode.TryGetValue(classId, out var type) ||
                type.Kind is not ("class" or "record" or "struct"))
            {
                unknowns.Add(new(entity.Id, UnknownKind.CSharpType, Status.Unknown,
                    duplicateCode.Contains(classId) ? "Ambiguous C# type: " + classId :
                    "No exact fully qualified C# type: " + classId, entity.Source));
                continue;
            }
            exact[entity.Id] = type;
            links.Add(new(entity.Id, LinkKind.EntityClass, type.Id, Status.Observed, entity.Source, At(type)));

            var contexts = code.Edges.Where(e => e.Kind == "persists" && e.To == type.Id)
                .Select(e => uniqueCode.GetValueOrDefault(e.From))
                .OfType<Node>()
                .Where(n => n.Tags?.Contains("dbcontext") == true)
                .DistinctBy(n => n.Id)
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
            if (contexts.Count == 0)
            {
                unknowns.Add(new(entity.Id, UnknownKind.DbContext, Status.Unknown,
                    "No scanner-observed DbContext.DbSet<T> persistence edge", entity.Source));
                continue;
            }
            contextsByEntity[entity.Id] = contexts;
            foreach (var context in contexts)
                links.Add(new(entity.Id, LinkKind.DbContextPersists, context.Id, Status.Observed, entity.Source, At(context)));
        }

        return new(entityById, uniqueCode, exact, contextsByEntity, links, unknowns);
    }

    static List<Trace> TraverseControllerPaths(CodeModel code, Matches matches, List<EdmxParser.Finding> findings)
    {
        var uniqueCode = matches.UniqueCode;
        var contextsByEntity = matches.Contexts;
        var entityById = matches.Entities;
        var exact = matches.ExactTypes;
        var traces = new List<Trace>();
        var controllerIds = uniqueCode.Values.Where(n => n.Tags?.Contains("controller") == true)
            .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var actions = code.Edges.Where(e => e.Kind == "contains" && controllerIds.Contains(e.From)
            && uniqueCode.TryGetValue(e.To, out var target) && target.Kind == "method")
            .Select(e => (Controller: uniqueCode[e.From], Action: uniqueCode[e.To]))
            .DistinctBy(x => (x.Controller.Id, x.Action.Id))
            .OrderBy(x => x.Controller.Id, StringComparer.Ordinal)
            .ThenBy(x => x.Action.Id, StringComparer.Ordinal)
            .ToList();
        var forward = code.Edges.Where(e => Traversable.Contains(e.Kind)
                && uniqueCode.ContainsKey(e.From) && uniqueCode.ContainsKey(e.To))
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Kind, StringComparer.Ordinal).Distinct().ToList(), StringComparer.Ordinal);

        // Forward traversal records a shortest observed code-symbol chain, with stable ties.
        foreach (var (controller, action) in actions)
        {
            var queue = new Queue<(string Id, List<Edge> Path)>();
            queue.Enqueue((action.Id, []));
            var visited = new HashSet<string>(StringComparer.Ordinal) { action.Id };
            while (queue.TryDequeue(out var item))
            {
                if (contextsByEntity.Count > 0 && item.Path.Count <= MaxDepth)
                {
                    foreach (var (entityId, contexts) in contextsByEntity.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        if (!contexts.Any(n => n.Id == item.Id)) continue;
                        if (traces.Count >= MaxTraces) break;
                        var entity = entityById[entityId];
                        var last = uniqueCode[item.Id];
                        var hops = item.Path.Select(e => new Hop(e.From, e.To, e.Kind,
                            At(uniqueCode[e.From]), At(uniqueCode[e.To]))).ToList();
                        var persistedType = exact[entityId];
                        hops.Add(new(last.Id, persistedType.Id, "persists", At(last), At(persistedType)));
                        var route = string.IsNullOrWhiteSpace(action.Route) ? null : action.Route;
                        traces.Add(new(entityId, controller.Id, action.Id, route,
                            route is null ? Status.Unknown : Status.Observed, hops, entity.Source, Status.Observed));
                    }
                }
                if (item.Path.Count >= MaxDepth || visited.Count >= MaxVisitedPerAction ||
                    traces.Count >= MaxTraces) continue;
                if (!forward.TryGetValue(item.Id, out var next)) continue;
                foreach (var edge in next.Where(e => !visited.Contains(e.To)))
                {
                    visited.Add(edge.To);
                    queue.Enqueue((edge.To, [.. item.Path, edge]));
                }
            }
            if (visited.Count >= MaxVisitedPerAction || traces.Count >= MaxTraces)
            {
                findings.Add(new("EDMX_LINK_LIMIT", "warning",
                    "Code traversal limit reached; unmatched paths remain unknown", At(action)));
                break;
            }
        }

        return traces;
    }

    static List<DomainHint> ProjectDomainHints(CodeModel code, List<Trace> traces,
        Dictionary<string, Node> uniqueCode)
    {
        var hints = new List<DomainHint>();
        var areas = DomainAreas.Group(code);
        var areaByMethod = areas.Areas.SelectMany(a => a.Members.Select(m => (a.Name, m)))
            .ToDictionary(x => x.m.Id, x => (x.Name, x.m.Reason), StringComparer.Ordinal);
        foreach (var trace in traces)
            foreach (var symbol in trace.Hops.Select(h => h.From).Append(trace.Action).Distinct(StringComparer.Ordinal)
                .Where(symbol => areaByMethod.ContainsKey(symbol) && uniqueCode.ContainsKey(symbol)))
            {
                var area = areaByMethod[symbol];
                hints.Add(new(area.Name, Status.Inferred, trace.Entity, symbol,
                    area.Reason, trace.EdmxSource, At(uniqueCode[symbol])));
            }
        return hints;
    }

    public static string Json(Report report) => JsonSerializer.Serialize(report,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n";

    public static string Text(Report report)
    {
        var b = new StringBuilder();
        b.AppendLine("EDMX code evidence " + report.File + ": " + report.Links.Count +
            " exact links, " + report.Traces.Count + " controller paths, " +
            report.Unknowns.Count + " unknowns, " + report.DomainEvidence.Count + " inferred area hints");
        foreach (var x in report.Links)
            b.AppendLine((x.Kind == LinkKind.EntityClass ? "entity-class" : "dbcontext-persists") + ": " + x.Entity + " -> " + x.CodeSymbol +
                " (" + x.EdmxSource.File + ":" + x.EdmxSource.Line + " / " +
                x.CodeSource.File + ":" + x.CodeSource.Line + ")");
        foreach (var t in report.Traces)
            b.AppendLine(t.Entity + ": " + t.Controller + "/" + t.Action + " route=" +
                (t.Route ?? "unknown") + " (" + string.Join(" -> ", t.Hops.Select(h => h.Kind)) + ")");
        foreach (var x in report.Unknowns)
            b.AppendLine("unknown " + x.Kind + " " + x.Entity + ": " + x.Reason);
        b.AppendLine("These are code-structure observations, not confirmed processes or bounded contexts.");
        return b.ToString();
    }
}
