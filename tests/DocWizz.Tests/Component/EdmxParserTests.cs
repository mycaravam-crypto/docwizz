using System.Text.Json;
namespace DocWizz.Tests.Component;

public class EdmxParserTests
{
    [Fact]
    public void Ef6_fixture_parses_entities_associations_mappings_and_functions()
    {
        var root = FindRepositoryRoot();
        var report = EdmxParser.Parse(root, "fixture-legacy/Shop/Models/Legacy.edmx");
        Assert.Equal("docwizz.edmx/v1", report.Schema);
        Assert.Equal(2, report.Entities.Count);
        Assert.Equal(2, report.StorageEntities.Count);
        Assert.Equal(2, report.ConceptualSets.Count);
        Assert.Single(report.Associations);
        Assert.Equal(["1", "*"], report.Associations[0].Ends.Select(x => x.Multiplicity).ToArray());
        Assert.Single(report.Navigations);
        Assert.Equal(2, report.Mappings.Count);
        Assert.Equal("tbl_customer", report.StorageSets.Single(x => x.Id == "Customers").Table);
        Assert.Equal("Store.GetTopCustomers", Assert.Single(report.FunctionImports).StoreFunction);
        Assert.Empty(report.Findings);
        Assert.True(report.Entities[0].Source.Line > 0);
        Assert.Equal(EdmxParser.Json(report), EdmxParser.Json(EdmxParser.Parse(root, "fixture-legacy/Shop/Models/Legacy.edmx")));
        using var json = JsonDocument.Parse(EdmxParser.Json(report));
        Assert.Equal("docwizz.edmx/v1", json.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void Malformed_and_unsafe_xml_are_findings_not_execution()
    {
        using var source = new Sources(("broken.edmx", "<Edmx"), ("xxe.edmx",
            "<!DOCTYPE Edmx [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><Edmx>&xxe;</Edmx>"));
        Assert.Contains(EdmxParser.Parse(source.Root, "broken.edmx").Findings, f => f.Code == "EDMX_XML_INVALID");
        Assert.Contains(EdmxParser.Parse(source.Root, "xxe.edmx").Findings, f => f.Code == "EDMX_XML_INVALID");
    }

    [Fact]
    public void Missing_references_are_reported_with_source_location()
    {
        using var source = new Sources(("orphan.edmx",
            "<Edmx><Runtime><ConceptualModels><Schema Namespace='M'><EntityContainer Name='C'><EntitySet Name='E' EntityType='M.Missing'/></EntityContainer></Schema></ConceptualModels></Runtime></Edmx>"));
        var finding = Assert.Single(EdmxParser.Parse(source.Root, "orphan.edmx").Findings);
        Assert.Equal("EDMX_REFERENCE", finding.Code);
        Assert.Equal("orphan.edmx", finding.Source.File);
        Assert.True(finding.Source.Line > 0);
    }

    [Fact]
    public void Paths_outside_the_repository_are_rejected()
    {
        using var source = new Sources(("valid.edmx", "<Edmx/>"));
        Assert.Throws<ArgumentException>(() => EdmxParser.Parse(source.Root, "../outside.edmx"));
        Assert.Throws<ArgumentException>(() => EdmxParser.Parse(source.Root, "."));
    }

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "fixture-legacy", "Shop", "Models", "Legacy.edmx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository fixture missing");
    }
}
