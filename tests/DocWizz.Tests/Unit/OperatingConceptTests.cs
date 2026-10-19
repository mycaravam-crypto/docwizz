namespace DocWizz.Tests.Unit;

public class OperatingConceptTests
{
    const string General = """
        schema: 1
        product_id: it-betriebskonzept
        document_type: it-operations
        sections:
          - id: runtime
            title: Laufzeit
            required: true
            sources: [code]
          - id: configuration
            title: Konfiguration
            required: true
            sources: [code]
          - id: backup
            title: Wiederherstellung
            required: true
            sources: [project]
        dependencies: []
        """;

    static ProductTemplate Parse(string yaml)
    {
        using var reader = new StringReader(yaml);
        return ProductTemplate.Parse(reader, "operations.yaml");
    }

    [Fact]
    public void Generic_contract_is_separate_from_v_model()
    {
        var template = Parse(General);
        Assert.False(template.IsVModel);
        Assert.Equal("it-operations", template.DocumentType);
        var result = ProductDraft.Render(template);
        Assert.DoesNotContain("V-Modell", result);
        Assert.Contains("Dokumenttyp: it-operations", result);
        Assert.Contains("OFFEN", result);
        Assert.Contains("document_type", ProductDraft.Report(template));

        Assert.Contains("must not declare", Assert.Throws<ArgumentException>(() =>
            Parse(General.Replace("document_type: it-operations",
                "document_type: it-operations\nxt_variant: V-Modell XT"))).Message);
        Assert.Contains("lowercase slug", Assert.Throws<ArgumentException>(() =>
            Parse(General.Replace("document_type: it-operations", "document_type: INVALID TYPE"))).Message);
        Assert.Contains("xt_variant is missing", Assert.Throws<ArgumentException>(() =>
            Parse(General.Replace("document_type: it-operations\n", ""))).Message);
    }

    [Fact]
    public void Runtime_and_configuration_are_evidenced_and_do_not_expose_values()
    {
        var model = new CodeModel("abc123",
            [
                new("proj:App/App.csproj", "project", "App", "App/App.csproj", 1,
                    Tags: ["dotnet", "Microsoft.NET.Sdk.Web", "net8.0", "executable"]),
                new("config:db:password", "config", "Db:Password", "appsettings.json", 3,
                    Tags: ["env:default@appsettings.json", "url:default=private.example"])
            ], []);
        var template = Parse(General);
        var draft = ProductDraft.Render(template, model);
        var report = ProductDraft.Report(template, model);
        Assert.Contains("Projekt: App (Rolle: executable; Target Framework: net8.0)", draft);
        Assert.Contains("Quelle: App/App.csproj:1", draft);
        Assert.Contains("Konfigurationsschluessel: Db:Password", draft);
        Assert.Contains("OFFEN", draft);
        Assert.DoesNotContain("private.example", draft);
        Assert.DoesNotContain("private.example", report);
        Assert.Equal(draft, ProductDraft.Render(template, model));
    }

    [Fact]
    public void Shipped_operating_template_loads()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "templates/it-operations/betriebskonzept.yaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var template = ProductTemplate.Load(Path.Join(root!.FullName, "templates/it-operations/betriebskonzept.yaml"));
        Assert.Equal("it-betriebskonzept", template.ProductId);
        Assert.False(template.IsVModel);
        Assert.Contains(template.Sections, section => section.Id == "backup" && section.Required && section.Sources.SequenceEqual(["project"]));
    }
}
