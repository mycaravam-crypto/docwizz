namespace DocWizz.Tests.Unit;

public class GenerateEvidenceFeedbackTests
{
    [Fact]
    public void MissingEvidenceIsReportedWithoutFailing()
    {
        var root = Path.Combine(Path.GetTempPath(), "docwizz-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var lines = GenerateEvidenceFeedback.Lines(root);
            Assert.Contains(lines, x => x.Contains("Requirements  not imported"));
            Assert.Contains(lines, x => x.Contains("ADO tests     not imported"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ValidAndInvalidSnapshotsGiveExplicitFeedback()
    {
        var root = Path.Combine(Path.GetTempPath(), "docwizz-evidence-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "docs", ".docwizz");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "ado-tests-12.json"), """
                {"format":"ado.test-traceability/v2","project":"Platform","area":"Product\\ALPHA",
                 "generatedAt":"2026-10-16T08:00:00+00:00",
                 "plans":[{"id":12,"name":"Release"}],
                 "testCases":[{"id":101,"title":"Login","area":"Product\\ALPHA","suiteIds":[2]}],
                 "assignments":[{"planId":12,"suiteId":2,"testCaseId":101}],
                 "requirements":[{"id":5,"title":"Sign in","testCaseIds":[101]}]}
                """);
            File.WriteAllText(Path.Combine(folder, "ado-tests-bad.json"), "{}");
            var lines = GenerateEvidenceFeedback.Lines(root);
            Assert.Contains(lines, x => x.Contains("1 plans, 1 cases, 1 linked requirements"));
            Assert.Contains(lines, x => x.Contains("invalid snapshot: ado-tests-bad.json"));
        }
        finally { Directory.Delete(root, true); }
    }
}
