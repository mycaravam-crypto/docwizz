namespace DocWizz.Tests.Unit;

public class AdoTestDocumentationTests
{
    [Fact]
    public void RendersImportedEvidenceWithoutCoverageClaims()
    {
        var json = """
            {"format":"ado.test-traceability/v2","project":"Demo","area":"Area",
             "generatedAt":"2026-10-16T06:00:00+00:00",
             "plans":[{"id":7,"name":"Plan"}],
             "testCases":[{"id":11,"title":"Case","area":"Area","suiteIds":[3]}],
             "assignments":[{"planId":7,"suiteId":3,"testCaseId":11}],
             "requirements":[{"id":21,"title":"Requirement","testCaseIds":[11]}]}
            """;
        var imported = new AdoTestSnapshot.ImportData("ado.test-traceability/v2", "Demo", "Area",
            DateTimeOffset.Parse("2026-10-16T06:00:00+00:00"), [7], 1, 1, 1, json);
        var page = AdoTestDocumentation.Render([imported]);
        Assert.Contains("| #21 | Requirement | 1 | #11 Case |", page);
        Assert.Contains("| 7 | Plan | 1 |", page);
        Assert.Contains("not", page);
        Assert.Contains("| **1** | **1** | **1** | **1** | **0** |", page);
    }
}
