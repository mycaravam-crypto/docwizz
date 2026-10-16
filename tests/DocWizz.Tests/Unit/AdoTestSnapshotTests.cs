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
}
