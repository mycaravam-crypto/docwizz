using System.Text;

namespace DocWizz.Tests.Journey;

public class RequirementCheckpointTests
{
    [Fact]
    public void Interrupted_mapping_resumes_completed_commits_without_rescanning()
    {
        using var repo = new FakeHistory();
        for (var n = 1; n <= 7; n++)
        {
            repo.Write("Shop.cs", $"namespace Shop; public class Service {{ public int Value() => {n}; }}");
            repo.Commit($"change {n}", FakeHistory.Verified(100));
        }

        var history = Encoding.UTF8.GetBytes(repo.History());
        var items = RequirementIndex.ReadWorkItems(null);
        RequirementIndex.Index? saved = null;
        var scanned = 0;

        CodeModel Model(string sha, List<string> paths)
        {
            scanned++;
            Assert.Equal(["Shop.cs"], paths);
            return new CodeModel(sha,
                [new Node("Shop.Service.Value", "method", "Value", "Shop.cs", 1, EndLine: 1, Hash: sha[..12])],
                []);
        }

        Assert.Throws<OperationCanceledException>(() => RequirementIndex.Build(
            history, items, null, "", Model, _ => true, checkpoint: partial =>
            {
                saved = partial;
                throw new OperationCanceledException("simulate process interruption after checkpoint");
            }));

        Assert.NotNull(saved);
        Assert.Equal(5, saved!.Commits.Count);
        Assert.Equal(5, scanned);
        var file = Path.Combine(repo.Root, "checkpoint.json");
        File.WriteAllText(file, RequirementIndex.Serialize(saved));
        var loaded = RequirementIndex.Load(file);
        var updates = new List<(int Done, int Total, string? Detail)>();
        var final = RequirementIndex.Build(history, items, null, "", Model, _ => true, loaded,
            (done, total, detail) => updates.Add((done, total, detail)));
        Assert.Equal((5, 7), (updates[0].Done, updates[0].Total));
        Assert.Contains("5 reused", updates[0].Detail);
        Assert.Equal((7, 7), (updates[^1].Done, updates[^1].Total));
        Assert.Contains("2 newly mapped", updates[^1].Detail);

        Assert.Equal(7, scanned);
        Assert.Equal(7, final.Commits.Count);
        Assert.All(final.Commits, c => Assert.Equal("mapped", c.Mapping));
        Assert.Equal(RequirementIndex.Fingerprint(history, items, null), final.Fingerprint);
    }
}
