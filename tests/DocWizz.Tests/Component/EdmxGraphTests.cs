using System.Globalization;
using System.Text.Json;

namespace DocWizz.Tests.Component;

public class EdmxGraphTests
{
    static readonly string Fixture = "fixture-legacy/Shop/Models/GraphGolden.edmx";

    static string Root()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Join(path.FullName, Fixture))) return path.FullName;
        throw new InvalidOperationException("GraphGolden.edmx fixture was not found");
    }

    static EdmxGraph.Report ParseGraph() => EdmxGraph.Build(EdmxParser.Parse(Root(), Fixture));

    static string GoldenLines(EdmxGraph.Report graph)
    {
        var lines = new List<string>
        {
            graph.Schema,
            "counts|" + graph.EntityCount + "|" + graph.AssociationCount + "|" +
                graph.RelationshipCount + "|" + graph.Navigations.Count + "|" +
                graph.Inheritance.Count + "|" + graph.Clusters.Count
        };
        lines.AddRange(graph.Associations.Select(e => "A|" + e.Id + "|" + e.From + "|" + e.To + "|" +
            e.FromMultiplicity + "|" + e.ToMultiplicity + "|" + e.Cardinality));
        lines.AddRange(graph.Navigations.Select(e => "N|" + e.Owner + "|" + e.Name + "|" + e.Target + "|" + e.Association));
        lines.AddRange(graph.Inheritance.Select(e => "I|" + e.Derived + "|" + e.Base));
        lines.AddRange(graph.Entities.Select(e => "M|" + e.Id + "|" + e.Degree + "|" + e.FanIn +
            "|" + e.FanOut + "|" + e.InheritanceLinks + "|" +
            e.Score.ToString(CultureInfo.InvariantCulture) + "|" + e.Cluster));
        lines.AddRange(graph.Clusters.Select(e => "C|" + e.Id + "|" + string.Join(",", e.Members)));
        lines.Add("central|" + string.Join(",", graph.CentralEntities));
        lines.Add("isolated|" + string.Join(",", graph.IsolatedEntities));
        lines.Add("findings|" + graph.Findings.Count);
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public void Golden_EF6_model_has_exact_relationships_metrics_and_clusters()
    {
        var root = Root();
        var facts = EdmxParser.Parse(root, Fixture);
        Assert.Empty(facts.Findings);
        Assert.Equal(6, facts.Entities.Count);
        Assert.Equal(6, facts.StorageEntities.Count);
        Assert.Equal(6, facts.Mappings.Count);
        Assert.Single(facts.AssociationMappings);

        var graph = EdmxGraph.Build(facts);
        var expected = File.ReadAllText(Path.Join(root, "tests", "DocWizz.Tests", "Golden", "edmx-graph-v1.txt"))
            .Replace("\r\n", "\n");
        Assert.Equal(expected, GoldenLines(graph));
        Assert.Equal("docwizz.edmx-graph/v1", graph.Schema);
        Assert.All(graph.Associations, a =>
        {
            Assert.Equal(Fixture, a.Source.File);
            Assert.True(a.Source.Line > 0);
            Assert.True(a.FromSource.Line > 0);
            Assert.True(a.ToSource.Line > 0);
        });
        Assert.All(graph.Navigations, n => Assert.True(n.Source.Line > 0));
        Assert.All(graph.Inheritance, e => Assert.True(e.Source.Line > 0));
        Assert.DoesNotContain(graph.Entities, e => e.Id.Contains("Store.", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(EdmxGraph.Json(graph));
        Assert.Equal(graph.Schema, json.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void Graph_is_deterministic_and_weights_change_only_scores()
    {
        var facts = EdmxParser.Parse(Root(), Fixture);
        var standard = EdmxGraph.Build(facts);
        Assert.Equal(EdmxGraph.Json(standard), EdmxGraph.Json(EdmxGraph.Build(facts)));
        var weighted = EdmxGraph.Build(facts, new(2m, 0m, 0m, 0m));
        Assert.Equal(4m, weighted.Entities.Single(e => e.Id == "Legacy.Customer").Score);
        Assert.Equal(4m, weighted.Entities.Single(e => e.Id == "Legacy.Order").Score);
        Assert.Equal(["Legacy.Customer", "Legacy.Order"], weighted.CentralEntities);
        Assert.Equal(standard.Associations, weighted.Associations);
        Assert.Equal(standard.Navigations, weighted.Navigations);
        Assert.Equal(standard.Clusters, weighted.Clusters);
        Assert.Throws<ArgumentOutOfRangeException>(() => EdmxGraph.Build(facts, new(0m, 0m, 0m, 0m)));
    }

    [Fact]
    public void Invalid_references_do_not_produce_unproven_edges()
    {
        using var source = new Sources(("invalid.edmx", """
            <Edmx><Runtime><ConceptualModels><Schema Namespace="Example">
            <EntityType Name="A"><NavigationProperty Name="Missing" Relationship="Example.Bad" FromRole="A" ToRole="B"/></EntityType>
            <Association Name="Bad"><End Role="A" Type="Example.A" Multiplicity="1"/><End Role="B" Type="Example.Unknown" Multiplicity="*"/></Association>
            </Schema></ConceptualModels></Runtime></Edmx>
            """));
        var facts = EdmxParser.Parse(source.Root, "invalid.edmx");
        var graph = EdmxGraph.Build(facts);
        Assert.Single(graph.Entities);
        Assert.Empty(graph.Associations);
        Assert.Empty(graph.Navigations);
        Assert.Equal(["Example.A"], graph.IsolatedEntities);
        Assert.Contains(graph.Findings, f => f.Code == "EDMX_GRAPH_REFERENCE");
        Assert.Contains(graph.Findings, f => f.Code == "EDMX_REFERENCE");
        Assert.All(graph.Findings, f => Assert.True(f.Source.Line > 0));
    }

    [Fact]
    public void Rejects_out_of_range_weights_and_localized_numbers()
    {
        var options = new Dictionary<string, string> { ["degree-weight"] = "1,5" };
        Assert.False(EdmxGraph.TryWeights(options, out _, out var error));
        Assert.Contains("--degree-weight", error);
        options["degree-weight"] = "1.5";
        options["fan-in-weight"] = "101";
        Assert.False(EdmxGraph.TryWeights(options, out _, out error));
        Assert.Contains("--fan-in-weight", error);
        options["fan-in-weight"] = "0";
        options["fan-out-weight"] = "0";
        options["inheritance-weight"] = "0";
        Assert.True(EdmxGraph.TryWeights(options, out var result, out error));
        Assert.Null(error);
        Assert.Equal(1.5m, result.Degree);
        options["degree-weight"] = "0";
        Assert.False(EdmxGraph.TryWeights(options, out _, out error));
        Assert.Contains("at least one", error);
    }
}
