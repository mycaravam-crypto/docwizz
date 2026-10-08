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
    public void Emits_package_urls_and_keeps_non_literal_specs_unversioned()
    {
        using var src = new Sources(
            ("web/package.json", """{"name":"web","dependencies":{"@scope/ui":"2.0.0","loose":"1.x","git-dep":"git+https://example.com/x.git"}}"""),
            ("svc/pom.xml", """
                <project><artifactId>svc</artifactId><dependencies>
                  <dependency><groupId>org.example</groupId><artifactId>core</artifactId><version>3.1.0</version></dependency>
                </dependencies></project>
                """));
        using var doc = JsonDocument.Parse(Sbom.Export(src.Root, src.Files));
        var byName = doc.RootElement.GetProperty("components").EnumerateArray()
            .ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal("pkg:npm/%40scope/ui@2.0.0", byName["@scope/ui"].GetProperty("purl").GetString());
        Assert.Equal("pkg:maven/org.example/core@3.1.0", byName["org.example:core"].GetProperty("purl").GetString());
        Assert.Equal("pkg:npm/loose", byName["loose"].GetProperty("purl").GetString());
        Assert.False(byName["loose"].TryGetProperty("version", out _));
        Assert.False(byName["git-dep"].TryGetProperty("version", out _));
    }

    [Fact]
    public void Project_references_and_the_scanned_root_are_recorded()
    {
        using var src = new Sources(
            ("lib/lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>"""),
            ("app/app.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                  <ProjectReference Include="../lib/lib.csproj" />
                </ItemGroup></Project>
                """));
        using var doc = JsonDocument.Parse(Sbom.Export(src.Root, src.Files));
        var root = doc.RootElement;
        Assert.Equal(Path.GetFileName(src.Root), root.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString());
        var app = root.GetProperty("dependencies").EnumerateArray()
            .Single(d => d.GetProperty("ref").GetString() == "proj:app/app.csproj");
        Assert.Contains(app.GetProperty("dependsOn").EnumerateArray(), x => x.GetString() == "proj:lib/lib.csproj");
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
