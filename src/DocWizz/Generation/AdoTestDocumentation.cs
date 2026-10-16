using System.Text;
using System.Text.Json;

// Reader-facing documentation of explicit ADO test-case evidence. No execution or code-coverage claims.
static class AdoTestDocumentation
{
    public static string Render(IReadOnlyList<AdoTestSnapshot.ImportData> snapshots)
    {
        var sb = new StringBuilder("# Test traceability\n\n");
        sb.AppendLine("Source: imported offline Azure DevOps snapshots. Links below are **explicit ADO relationships**, not evidence of test execution, acceptance or code coverage.\n");
        foreach (var snapshot in snapshots.OrderBy(s => s.Project).ThenBy(s => s.Area))
        {
            using var json = JsonDocument.Parse(snapshot.Content);
            var root = json.RootElement;
            sb.Append("## ").Append(E(snapshot.Project)).Append(" / ").Append(E(snapshot.Area)).AppendLine("\n");
            sb.Append("Captured ").Append(snapshot.GeneratedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'"))
                .Append(" · Schema `").Append(E(snapshot.Format)).AppendLine("`\n");
            sb.AppendLine("| Plans | Test cases | Requirements | Linked requirements | Unlinked requirements |");
            sb.AppendLine("| ---: | ---: | ---: | ---: | ---: |");
            sb.Append("| **").Append(snapshot.PlanIds.Length).Append("** | **").Append(snapshot.CaseCount)
                .Append("** | **").Append(snapshot.RequirementCount).Append("** | **").Append(snapshot.LinkedCount)
                .Append("** | **").Append(snapshot.RequirementCount - snapshot.LinkedCount).AppendLine("** |\n");

            sb.AppendLine("### Test plans\n");
            sb.AppendLine("| Plan ID | Name | Test cases in snapshot |");
            sb.AppendLine("| ---: | --- | ---: |");
            var cases = root.GetProperty("testCases").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32());
            if (root.TryGetProperty("plans", out var plans))
            {
                var assignments = root.GetProperty("assignments").EnumerateArray().ToArray();
                foreach (var plan in plans.EnumerateArray().OrderBy(p => p.GetProperty("id").GetInt32()))
                {
                    var id = plan.GetProperty("id").GetInt32();
                    var count = assignments.Where(a => a.GetProperty("planId").GetInt32() == id)
                        .Select(a => a.GetProperty("testCaseId").GetInt32()).Distinct().Count();
                    sb.Append("| ").Append(id).Append(" | ").Append(E(plan.GetProperty("name").GetString()))
                        .Append(" | ").Append(count).AppendLine(" |");
                }
            }
            else
                sb.Append("| ").Append(root.GetProperty("planId").GetInt32()).Append(" | ")
                    .Append(E(root.GetProperty("planName").GetString())).Append(" | ")
                    .Append(snapshot.CaseCount).AppendLine(" |");
            sb.AppendLine();

            sb.AppendLine("### Requirements and test cases\n");
            sb.AppendLine("| Requirement | Description | Linked cases | Test cases |");
            sb.AppendLine("| --- | --- | ---: | --- |");
            foreach (var requirement in root.GetProperty("requirements").EnumerateArray().OrderBy(r => r.GetProperty("id").GetInt32()))
            {
                var ids = requirement.GetProperty("testCaseIds").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                var titles = ids.Select(id => cases.TryGetValue(id, out var testCase)
                    ? $"#{id} {E(testCase.GetProperty("title").GetString())}" : $"#{id} (not in snapshot)");
                sb.Append("| #").Append(requirement.GetProperty("id").GetInt32()).Append(" | ")
                    .Append(E(requirement.GetProperty("title").GetString())).Append(" | ")
                    .Append(ids.Length).Append(" | ")
                    .Append(ids.Length == 0 ? "No linked case in this snapshot" : string.Join("<br>", titles))
                    .AppendLine(" |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string E(string? text) => (text ?? "").Replace("`", "'")
        .Replace("[", "(").Replace("]", ")").Replace("|", "&#124;")
        .Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;");
}
