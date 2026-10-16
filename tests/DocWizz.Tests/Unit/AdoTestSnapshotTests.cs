namespace DocWizz.Tests.Unit;

public class AdoTestSnapshotTests
{
    [Fact]
    public void ImportsExplicitLinksAndRejectsMissingCases()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """
                {"format":"ado.test-traceability/v1","project":"Platform","area":"Product\\ALPHA",
                 "generatedAt":"2026-10-16T08:00:00+00:00","planId":12,"planName":"Release",
                 "testCases":[{"id":101,"title":"Login","area":"Product\\ALPHA","suiteIds":[2]}],
                 "requirements":[{"id":5,"title":"Sign in","testCaseIds":[101]}]}
                """);
            var data = AdoTestSnapshot.Read(file);
            Assert.Contains("1 linked requirements", AdoTestSnapshot.Summary(data));
            File.WriteAllText(file, File.ReadAllText(file).Replace("testCaseIds\":[101]", "testCaseIds\":[999]"));
            Assert.Throws<ArgumentException>(() => AdoTestSnapshot.Read(file));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void MultiPlanSnapshotKeepsPlanAndSuiteMemberships()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """
                {"format":"ado.test-traceability/v2","project":"Platform","area":"Product\\ALPHA",
                 "generatedAt":"2026-10-16T08:00:00+00:00",
                 "plans":[{"id":12,"name":"Release"},{"id":15,"name":"Regression"}],
                 "testCases":[{"id":101,"title":"Login","area":"Product\\ALPHA","suiteIds":[2,7]}],
                 "assignments":[{"planId":12,"suiteId":2,"testCaseId":101},{"planId":15,"suiteId":7,"testCaseId":101}],
                 "requirements":[{"id":5,"title":"Sign in","testCaseIds":[101]}]}
                """);
            var snapshot = AdoTestSnapshot.Load(file);
            Assert.Equal([12, 15], snapshot.PlanIds);
            Assert.Equal(1, snapshot.CaseCount);
            Assert.Equal(1, snapshot.LinkedCount);
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"suiteId\":7", "\"suiteId\":0"));
            Assert.Throws<ArgumentException>(() => AdoTestSnapshot.Load(file));
        }
        finally { File.Delete(file); }
    }

}
