using DocWizz.Tests.Unit;

namespace DocWizz.Tests.Journey;

public class AiRatingsJourneyTests
{
    [Theory]
    [InlineData("generate")]
    [InlineData("setup")]
    public void Drafts_and_ratings_are_independent_opt_ins_and_cached_ratings_survive(string command)
    {
        using var server = new AiProviderTests.FakeServer(200,
            """{"message":{"content":"{\"score\":2,\"missing\":[\"purpose\"],\"note\":\"Explain why.\"}"}}""");
        using var repo = new Sources(("Orders.cs", """
            using Microsoft.AspNetCore.Mvc;
            namespace Shop;
            /// <summary>Places orders.</summary>
            [ApiController]
            public class OrdersController
            {
                /// <summary>Finds an order.</summary>
                public int Find(int id) => id;
                public int Create(int a, int b, int c, int d, int e, int f) => a + b + c + d + e + f;
            }
            """));
        var env = new Dictionary<string, string?> { ["OLLAMA_HOST"] = server.Uri.ToString(), ["DOCWIZZ_MODEL"] = "local:7b" };
        var drafted = Docwizz.Run(null, env, command, repo.Root, "--ai");
        Assert.True(drafted.Exit == 0, drafted.ToString());
        Assert.NotEmpty(server.Requests);
        Assert.DoesNotContain(server.Requests, r => r.Contains("You review", StringComparison.Ordinal));
        var cache = Path.Combine(repo.Root, "docs", ".docwizz", "ai-assessments.json");
        Assert.False(File.Exists(cache));

        var before = server.Requests.Count;
        var rated = Docwizz.Run(null, env, command, repo.Root, "--ai-ratings");
        Assert.True(rated.Exit == 0, rated.ToString());
        Assert.True(server.Requests.Count > before, rated.ToString());
        Assert.All(server.Requests.Skip(before), r => Assert.Contains("You review", r));
        Assert.Contains("\"score\": 2", File.ReadAllText(cache));
        Assert.Contains("| 2 | purpose | Explain why. |", File.ReadAllText(Path.Combine(repo.Root, "docs", "quality.md")));

        before = server.Requests.Count;
        var cached = Docwizz.Run(null, env, command, repo.Root);
        Assert.True(cached.Exit == 0, cached.ToString());
        Assert.Equal(before, server.Requests.Count);
        Assert.Contains("| 2 | purpose | Explain why. |", File.ReadAllText(Path.Combine(repo.Root, "docs", "quality.md")));
    }
}
