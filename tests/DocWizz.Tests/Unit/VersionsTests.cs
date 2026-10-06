using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

// Package versions: how an update is classified, and package updates as part of a change.
public class VersionsTests
{
    [Theory]
    [InlineData("12.0.3", "13.0.3", "major")]
    [InlineData("13.0.1", "13.1.0", "minor")]
    [InlineData("13.0.1", "13.0.3", "patch")]
    [InlineData("1.2", "1.2.0.1", "patch")]
    [InlineData("2.0.0-rc.1", "2.0.0", "patch")]
    [InlineData("2.0.0", "2.0.0-rc.1", "downgrade")]
    [InlineData("13.0.3", "12.0.3", "downgrade")]
    [InlineData("1.0", "1.0.0", "same")]
    [InlineData("1.*", "2.0.0", "unknown")]
    [InlineData("[1.0,2.0)", "2.0.0", "unknown")]
    [InlineData(null, "2.0.0", "unknown")]
    public void Classifies_updates(string? from, string to, string kind) => Assert.Equal(kind, Versions.Kind(from, to));

    [Fact]
    public void Prereleases_sort_numerically_and_before_the_release()
    {
        var order = new[] { "1.0.0", "1.0.0-rc.10", "1.0.0-rc.2", "1.0.0-beta", "0.9.9" }
            .OrderBy(v => Versions.Parse(v)!, Comparer<Versions.Version>.Create(Versions.Compare)).ToList();
        Assert.Equal(["0.9.9", "1.0.0-beta", "1.0.0-rc.2", "1.0.0-rc.10", "1.0.0"], order);
    }

    [Fact]
    public void Diff_reports_package_version_changes_with_their_impact()
    {
        Node Project() => N("proj:src/Api.csproj", "src/Api.csproj", kind: "project", tags: ["dotnet", "library"]);
        Node Package() => new("pkg:nuget:Newtonsoft.Json", "package", "Newtonsoft.Json", "src/Api.csproj", 1, Tags: ["nuget"]);
        var codec = N("cs:Api.Codec", "src/Codec.cs", hash: "1");
        var uses = E("cs:Api.Codec", "ns:Newtonsoft.Json", "uses-namespace");
        var before = Model([Project(), Package(), codec], [E("proj:src/Api.csproj", "pkg:nuget:Newtonsoft.Json", "depends-on", "12.0.3"), uses]);
        var after = Model([Project(), Package(), codec], [E("proj:src/Api.csproj", "pkg:nuget:Newtonsoft.Json", "depends-on", "13.0.3"), uses]);

        var update = Assert.Single(Diff.Compare(before, after, Config()).Packages);
        Assert.Equal(("Newtonsoft.Json", "12.0.3", "13.0.3", "major", "src/Api.csproj"), (update.Package, update.Before, update.After, update.Kind, update.Project));
        Assert.Equal(["cs:Api.Codec"], update.Impact.Uses.Select(u => u.Id));
        Assert.StartsWith("broader than the change", update.Scope);   // the bump changed no code that uses the package
    }
}
