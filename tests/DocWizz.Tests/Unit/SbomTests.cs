using System.Text.Json;

namespace DocWizz.Tests.Unit;

public class SbomTests
{
    [Fact]
    public void Exports_direct_dependencies_and_preserves_unresolved_ranges()
    {
        using var src = new Sources(
            ("app/app.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                  <PackageReference Include="Example.Lib" Version="1.2.3" />
                </ItemGroup></Project>
                """),
            ("web/package.json", """{"name":"web","dependencies":{"left-pad":"^1.0.0"}}"""));
        var first = Sbom.Export(src.Root, src.Files);
        Assert.Equal(first, Sbom.Export(src.Root, src.Files));
        using var doc = JsonDocument.Parse(first);
        var root = doc.RootElement;
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
        Assert.Equal("1.6", root.GetProperty("specVersion").GetString());
        var components = root.GetProperty("components").EnumerateArray().ToList();
        var literal = components.Single(x => x.GetProperty("name").GetString() == "Example.Lib");
        Assert.Equal("1.2.3", literal.GetProperty("version").GetString());
        var range = components.Single(x => x.GetProperty("name").GetString() == "left-pad");
        Assert.False(range.TryGetProperty("version", out _));
        Assert.Contains(range.GetProperty("properties").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "docwizz:declaredVersion" &&
                 p.GetProperty("value").GetString() == "^1.0.0");
        Assert.Equal(2, root.GetProperty("dependencies").GetArrayLength());
    }

    [Fact]
    public void Different_versions_remain_separate_components()
    {
        using var src = new Sources(
            ("one/package.json", """{"name":"one","dependencies":{"common":"1.0.0"}}"""),
            ("two/package.json", """{"name":"two","dependencies":{"common":"2.0.0"}}"""));
        using var doc = JsonDocument.Parse(Sbom.Export(src.Root, src.Files));
        var packages = doc.RootElement.GetProperty("components").EnumerateArray()
            .Where(x => x.GetProperty("name").GetString() == "common").ToList();
        Assert.Equal(2, packages.Count);
        Assert.Equal(2, packages.Select(x => x.GetProperty("bom-ref").GetString()).Distinct().Count());
    }

    [Fact]
    public void Empty_repository_produces_an_empty_bom()
    {
        using var src = new Sources(("README.md", "# empty"));
        using var doc = JsonDocument.Parse(Sbom.Export(src.Root, src.Files));
        Assert.Empty(doc.RootElement.GetProperty("components").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("dependencies").EnumerateArray());
    }
}
