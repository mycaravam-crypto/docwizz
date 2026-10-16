using System.Text.Json;

// Offline-only import of ADO test case traceability. Never infers code links or test success.
static class AdoTestSnapshot
{
    public sealed record Case(int Id, string Title, string Area, int[] SuiteIds);
    public sealed record Requirement(int Id, string Title, int[] TestCaseIds);
    public sealed record Plan(int Id, string Name);
    public sealed record Assignment(int PlanId, int SuiteId, int TestCaseId);
    public sealed record MultiData(string Format, string Project, string Area, DateTimeOffset GeneratedAt,
        Plan[] Plans, Case[] TestCases, Assignment[] Assignments, Requirement[] Requirements);
    public sealed record ImportData(string Format, string Project, string Area, DateTimeOffset GeneratedAt,
        int[] PlanIds, int CaseCount, int RequirementCount, int LinkedCount, string Content);

    public static ImportData Load(string file)
    {
        var json = File.ReadAllText(file);
        using var doc = JsonDocument.Parse(json);
        var format = doc.RootElement.GetProperty("format").GetString();
        if (format == "ado.test-traceability/v1")
        {
            var value = Read(file);
            return new(value.Format, value.Project, value.Area, value.GeneratedAt, [value.PlanId],
                value.TestCases.Length, value.Requirements.Length, value.Requirements.Count(r => r.TestCaseIds.Length > 0), json);
        }
        if (format != "ado.test-traceability/v2")
            throw new ArgumentException("unsupported ADO test snapshot format");
        var data = JsonSerializer.Deserialize<MultiData>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("empty ADO test snapshot");
        if (string.IsNullOrWhiteSpace(data.Project) || string.IsNullOrWhiteSpace(data.Area) ||
            data.Plans is null || data.TestCases is null || data.Requirements is null || data.Assignments is null)
            throw new ArgumentException("invalid ADO test snapshot");
        var plans = data.Plans.Select(p => p.Id).ToHashSet();
        var cases = data.TestCases.Select(c => c.Id).ToHashSet();
        if (plans.Count != data.Plans.Length || cases.Count != data.TestCases.Length ||
            data.Requirements.Select(r => r.Id).Distinct().Count() != data.Requirements.Length ||
            data.Assignments.Any(a => !plans.Contains(a.PlanId) || !cases.Contains(a.TestCaseId) || a.SuiteId <= 0) ||
            data.Assignments.Distinct().Count() != data.Assignments.Length ||
            data.Requirements.Any(r => r.TestCaseIds is null || r.TestCaseIds.Any(id => !cases.Contains(id))))
            throw new ArgumentException("invalid ADO snapshot references");
        return new(data.Format, data.Project, data.Area, data.GeneratedAt, plans.Order().ToArray(),
            cases.Count, data.Requirements.Length, data.Requirements.Count(r => r.TestCaseIds.Length > 0), json);
    }

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

    public static string Summary(ImportData data) =>
        $"ADO test snapshot: {data.Project} / {data.Area}; {data.PlanIds.Length} plans, " +
        $"{data.RequirementCount} requirements, {data.CaseCount} cases, {data.LinkedCount} linked requirements. " +
        "Explicit links only; not proof of execution or code coverage.";

    public static string Summary(Data data) =>
        $"ADO test snapshot: {data.Project} / {data.Area}; plan #{data.PlanId} {data.PlanName}; " +
        $"{data.Requirements.Length} requirements, {data.TestCases.Length} cases, " +
        $"{data.Requirements.Count(r => r.TestCaseIds.Length > 0)} linked requirements. " +
        "Links are explicit ADO evidence, not proof of execution or code coverage.";
}
