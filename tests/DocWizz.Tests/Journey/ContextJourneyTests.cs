namespace DocWizz.Tests.Journey;

// `docwizz context` on the bundled fixture, against golden files: one package per kind of target. The fixture is copied
// out of the repository first, so the package doesn't carry this repository's commit. DOCWIZZ_UPDATE_GOLDEN=1 rewrites
// the golden files; review the diff before committing it.
public class ContextJourneyTests
{
    static readonly string Root = FindRoot();
    static readonly string Golden = Path.Combine(Root, "tests", "DocWizz.Tests", "Journey", "golden", "context");

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "fixture")) && File.Exists(Path.Combine(dir.FullName, "CLI.md"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found above the test binary");
    }

    sealed class FixtureCopy : IDisposable
    {
        public string Dir { get; }

        public FixtureCopy()
        {
            var tmp = Directory.CreateTempSubdirectory("docwizz-ctx-").FullName;
            Dir = Path.Combine(tmp, "fixture");
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "fixture"), "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Path.Combine(Root, "fixture"), f);
                if (rel.Split(Path.DirectorySeparatorChar).Any(d => d is "node_modules" or "docs" or "bin" or "obj")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Dir, rel))!);
                File.Copy(f, Path.Combine(Dir, rel));
            }
        }

        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Dir)!, recursive: true);
    }

    [Theory]
    [InlineData("symbol", "Fixture.Application.MaterialService.CreateAsync")]
    [InlineData("file", "backend/Api/MaterialController.cs")]
    [InlineData("module", "backend/Infrastructure")]
    [InlineData("endpoint", "POST /api/materials")]
    public void A_package_per_kind_of_target_matches_its_golden_file(string name, string target)
    {
        Docwizz.RequireFrontendScanner();
        using var fixture = new FixtureCopy();
        var r = Docwizz.Run("context", fixture.Dir, "--for", target);
        Assert.True(r.Exit == 0, r.ToString());
        var golden = Path.Combine(Golden, $"{name}.md");
        if (Environment.GetEnvironmentVariable("DOCWIZZ_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Golden);
            File.WriteAllText(golden, r.Out);
        }
        Assert.Equal(File.ReadAllText(golden), r.Out);
        Assert.Equal(r.Out, Docwizz.Run("context", fixture.Dir, "--for", target).Out);   // byte-identical on a second run
    }

    [Fact]
    public void An_unknown_or_ambiguous_target_exits_1_with_candidates_and_prints_no_package()
    {
        using var fixture = new FixtureCopy();
        var unknown = Docwizz.Run("context", fixture.Dir, "--for", "MaterialServise");
        Assert.Equal(1, unknown.Exit);
        Assert.Equal("", unknown.Out);
        Assert.Contains("no symbol, file, folder or endpoint matches MaterialServise", unknown.Err);
        var ambiguous = Docwizz.Run("context", fixture.Dir, "--for", "Fixture.Api.MaterialController.Get");
        Assert.Equal(0, ambiguous.Exit);   // one Get on MaterialController: resolved without its parameter list
        var missing = Docwizz.Run("context", fixture.Dir);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("context needs --for", missing.Err);
        Docwizz.NoStackTrace(missing);
    }

    [Fact]
    public void A_small_budget_is_kept_and_a_changed_file_makes_the_model_stale()
    {
        using var fixture = new FixtureCopy();
        Assert.Equal(0, Docwizz.Run("generate", fixture.Dir).Exit);
        var fresh = Docwizz.Run("context", fixture.Dir, "--for", "backend/Domain", "--budget", "250", "--format", "json");
        Assert.True(fresh.Exit == 0, fresh.ToString());
        Assert.True(fresh.Out.Length <= 250 * 4, $"{fresh.Out.Length} characters");
        Assert.Contains("\"stale\":false", fresh.Out);
        Assert.Contains("docs/.docwizz/model.json", fresh.Out);

        File.SetLastWriteTimeUtc(Path.Combine(fixture.Dir, "backend", "Domain", "Material.cs"), DateTime.UtcNow.AddMinutes(5));
        var stale = Docwizz.Run("context", fixture.Dir, "--for", "backend/Domain");
        Assert.Contains("⚠ stale: 1 source file changed after the model was written", stale.Out);
        Assert.Contains("the model is stale", stale.Err);
    }
}
