using System.Text.Json;

namespace DocWizz.Tests.Unit;

public class RequirementsCatalogTests
{
    static string Root()
    {
        var root = Path.Join(Path.GetTempPath(), "docwizz-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static string Catalog(bool complete = true, string relations = """[{"kind":"parent","id":100}]""") =>
        $$"""
          {
            "schema": "ado.workitem-catalog/v1",
            "project": "Platform", "area": "Platform\\Auth", "source": "https://tfs",
            "complete": {{(complete ? "true" : "false")}},
            "warnings": [],
            "items": [
              {"id": 9, "type": "User Story", "title": "<Login> | reset",
               "state": "Closed", "area": "Platform\\Auth",
               "iteration": "Sprint 1", "description": "Reset password",
               "criteria": ["Token expires", "Email sent"],
               "relations": {{relations}}}
            ]
          }
          """;

    [Fact]
    public void ImportsCompleteAreaInventoryWithExplicitParentAndNoHistory()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Join(root, "input.json"), Catalog());
            Assert.Equal(0, RequirementsCatalog.Import(root, "input.json"));
            var index = File.ReadAllText(Path.Join(root, "docs", "requirements", "area-catalog.md"));
            Assert.Contains("[9](items/9.md)", index);
            Assert.Contains("| 100 |", index);
            Assert.Contains("&lt;Login&gt; &#124; reset", index);
            Assert.Contains("Not assessed", index);
            var page = File.ReadAllText(Path.Join(root, "docs", "requirements", "items", "9.md"));
            Assert.Contains("Token expires", page);
            Assert.Contains("Email sent", page);
            Assert.True(File.Exists(Path.Join(root, RequirementsCatalog.FileName)));
            Assert.False(File.Exists(Path.Join(root, "docs", ".docwizz", "requirements.json")));
            var outDir = Path.Join(root, "site");
            RequirementsCatalog.RenderIfPresent(root, outDir);
            Assert.True(File.Exists(Path.Join(outDir, "requirements", "area-catalog.md")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsIncompleteCatalogWithoutWritingFiles()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Join(root, "input.json"), Catalog(complete: false));
            Assert.Throws<ArgumentException>(() => RequirementsCatalog.Import(root, "input.json"));
            Assert.False(File.Exists(Path.Join(root, RequirementsCatalog.FileName)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsParentWithoutValidIdInsteadOfCrashingDuringRender()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Join(root, "input.json"), Catalog(relations: """[{"kind":"parent"}]"""));
            var ex = Assert.Throws<ArgumentException>(() => RequirementsCatalog.Import(root, "input.json"));
            Assert.Contains("invalid relation", ex.Message);
            Assert.False(File.Exists(Path.Join(root, RequirementsCatalog.FileName)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        var root = Root();
        try
        {
            var text = Catalog();
            var item = JsonDocument.Parse(text).RootElement.GetProperty("items")[0].GetRawText();
            text = text.Replace(item, item + "," + item);
            File.WriteAllText(Path.Join(root, "input.json"), text);
            Assert.Throws<ArgumentException>(() => RequirementsCatalog.Import(root, "input.json"));
        }
        finally { Directory.Delete(root, true); }
    }
}
