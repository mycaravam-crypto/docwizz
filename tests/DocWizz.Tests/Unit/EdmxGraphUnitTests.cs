namespace DocWizz.Tests.Unit;

// Isolated graph unit contract: no file scanner, EF runtime, CLI process, network or AI.
public class EdmxGraphUnitTests
{
    static EdmxParser.Report Facts(bool missingTarget = false)
    {
        var loc = new EdmxParser.Evidence("model.edmx", 12);
        var a = new EdmxParser.Entity("Example.A", null, [], loc);
        var b = new EdmxParser.Entity("Example.B", null, [], loc);
        var isolated = new EdmxParser.Entity("Example.Isolated", null, [], loc);
        var association = new EdmxParser.Association("Example.Rel",
            [new("Left", a.Id, "1", loc),
             new("Right", missingTarget ? "Example.Missing" : b.Id, "*", loc)], loc);
        return new(EdmxParser.Schema, "model.edmx",
            [a, b, isolated], [], [], [], [association], [],
            [new(a.Id, "Bs", association.Id, "Left", "Right", loc),
             new(b.Id, "A", association.Id, "Right", "Left", loc)],
            [], [], [], []);
    }

    [Fact]
    public void Directionless_associations_and_explicit_navigations_are_counted_independently()
    {
        var graph = EdmxGraph.Build(Facts());
        Assert.Equal(3, graph.EntityCount);
        Assert.Single(graph.Associations);
        Assert.Equal("1:n", graph.Associations[0].Cardinality);
        Assert.Equal(2, graph.Navigations.Count);
        Assert.Equal(2, graph.Clusters.Count);
        Assert.Equal(["Example.Isolated"], graph.IsolatedEntities);
        Assert.Equal(["Example.A", "Example.B"], graph.CentralEntities);
        Assert.All(graph.Entities.Where(x => x.Id != "Example.Isolated"), m =>
        {
            Assert.Equal(1, m.Degree);
            Assert.Equal(1, m.FanIn);
            Assert.Equal(1, m.FanOut);
            Assert.Equal(3m, m.Score);
        });
        Assert.Equal("model.edmx", graph.Associations[0].Source.File);
        Assert.Equal(12, graph.Associations[0].Source.Line);
    }

    [Fact]
    public void Weights_change_only_technical_score_not_evidence_or_components()
    {
        var input = Facts();
        var original = EdmxGraph.Build(input);
        var weighted = EdmxGraph.Build(input, new(2m, 0m, 0m, 0m));
        Assert.Equal(2m, weighted.Entities.Single(x => x.Id == "Example.A").Score);
        Assert.Equal(EdmxGraph.Json(weighted), EdmxGraph.Json(EdmxGraph.Build(input, new(2m, 0m, 0m, 0m))));
        Assert.Equal(original.Associations, weighted.Associations);
        Assert.Equal(original.Navigations, weighted.Navigations);
        Assert.Equal(original.Clusters.Select(c => string.Join(",", c.Members)),
            weighted.Clusters.Select(c => string.Join(",", c.Members)));
        Assert.Throws<ArgumentOutOfRangeException>(() => EdmxGraph.Build(input, new(-1m, 1m, 1m, 1m)));
    }

    [Fact]
    public void Unresolved_associations_do_not_create_speculative_edges()
    {
        var graph = EdmxGraph.Build(Facts(missingTarget: true));
        Assert.Empty(graph.Associations);
        Assert.Empty(graph.Navigations);
        Assert.Empty(graph.CentralEntities);
        Assert.Equal(3, graph.IsolatedEntities.Count);
        Assert.Contains(graph.Findings, f => f.Code == "EDMX_GRAPH_REFERENCE");
    }

    [Fact]
    public void Weight_options_are_strict_invariant_and_zero_only_is_rejected()
    {
        var options = new Dictionary<string, string> { ["degree-weight"] = "2.5" };
        Assert.True(EdmxGraph.TryWeights(options, out var weights, out var error));
        Assert.Null(error);
        Assert.Equal(2.5m, weights.Degree);
        Assert.Equal(1m, weights.FanIn);
        options["degree-weight"] = "1,5";
        Assert.False(EdmxGraph.TryWeights(options, out _, out error));
        Assert.Contains("--degree-weight", error);
        options["degree-weight"] = "0";
        options["fan-in-weight"] = "0";
        options["fan-out-weight"] = "0";
        options["inheritance-weight"] = "0";
        Assert.False(EdmxGraph.TryWeights(options, out _, out error));
        Assert.Contains("at least one", error);
    }
}
