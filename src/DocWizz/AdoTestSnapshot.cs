using System.Text.Json;

// Offline-only import of ADO test case traceability. Never infers code links or test success.
static class AdoTestSnapshot
{
    public sealed record Case(int Id, string Title, string Area, int[] SuiteIds);
    public sealed record Requirement(int Id, string Title, int[] TestCaseIds);
    public sealed record Data(string Format, string Project, string Area, DateTimeOffset GeneratedAt,
        int PlanId, string PlanName, Case[] TestCases, Requirement[] Requirements);

    public static Data Read(string file)
    {
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("empty ADO test snapshot");
        if (data.Format != "ado.test-traceability/v1" || string.IsNullOrWhiteSpace(data.Project)
            || string.IsNullOrWhiteSpace(data.Area) || data.PlanId <= 0
            || data.TestCases is null || data.Requirements is null)
            throw new ArgumentException("invalid or unsupported ADO test snapshot (expected ado.test-traceability/v1)");
        if (data.TestCases.Any(c => c.Id <= 0 || c.SuiteIds is null) ||
            data.TestCases.Select(c => c.Id).Distinct().Count() != data.TestCases.Length ||
            data.Requirements.Any(r => r.Id <= 0 || r.TestCaseIds is null) ||
            data.Requirements.Select(r => r.Id).Distinct().Count() != data.Requirements.Length)
            throw new ArgumentException("duplicate or invalid case/requirement IDs");
        var ids = data.TestCases.Select(c => c.Id).ToHashSet();
        if (data.Requirements.SelectMany(r => r.TestCaseIds).Any(id => !ids.Contains(id)))
            throw new ArgumentException("test link references a case absent from the snapshot");
        return data;
    }

    public static string Summary(Data data) =>
        $"ADO test snapshot: {data.Project} / {data.Area}; plan #{data.PlanId} {data.PlanName}; " +
        $"{data.Requirements.Length} requirements, {data.TestCases.Length} cases, " +
        $"{data.Requirements.Count(r => r.TestCaseIds.Length > 0)} linked requirements. " +
        "Links are explicit ADO evidence, not proof of execution or code coverage.";
}
