using System.Text;
using System.Text.Json;

// Reader-facing documentation of explicit ADO test-case evidence. No execution or code-coverage claims.
static class AdoTestDocumentation
{
    public static string Render(IReadOnlyList<AdoTestSnapshot.ImportData> snapshots)
    {
        var sb = new StringBuilder("# Imported Azure DevOps test traceability\n\n");
        sb.AppendLine("Source: offline Azure DevOps test snapshots. Requirement-to-test-case links are explicit ADO evidence; they do **not** establish executed tests, code coverage, acceptance, or behavioral correctness.\n");
        foreach (var snapshot in snapshots.OrderBy(s => s.Project).ThenBy(s => s.Area))
        {
            using var json = JsonDocument.Parse(snapshot.Content);
            var root = json.RootElement;
            sb.Append("## ").Append(E(snapshot.Project)).Append(" / ").Append(E(snapshot.Area)).AppendLine("\n");
            sb.Append("Snapshot: `").Append(E(snapshot.Format)).Append("`; captured ")
                .Append(snapshot.GeneratedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'"))
                .AppendLine(".\n");
            sb.AppendLine($"Plans: **{snapshot.PlanIds.Length}**; distinct test cases: **{snapshot.CaseCount}**; requirements: **{snapshot.RequirementCount}**; linked requirements: **{snapshot.LinkedCount}**.\n");
            sb.AppendLine("### Plans\n");
            if (root.TryGetProperty("plans", out var plans))
                foreach (var plan in plans.EnumerateArray())
                    sb.Append("- #").Append(plan.GetProperty("id").GetInt32()).Append(" ")
                        .Append(E(plan.GetProperty("name").GetString())).AppendLine();
            else
                sb.Append("- #").Append(root.GetProperty("planId").GetInt32()).Append(" ")
                    .Append(E(root.GetProperty("planName").GetString())).AppendLine();
            sb.AppendLine("\n### Requirements and explicitly linked test cases\n");
            var cases = root.GetProperty("testCases").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32());
            foreach (var requirement in root.GetProperty("requirements").EnumerateArray().OrderBy(r => r.GetProperty("id").GetInt32()))
            {
                sb.Append("- Requirement #").Append(requirement.GetProperty("id").GetInt32()).Append(": ")
                    .Append(E(requirement.GetProperty("title").GetString())).AppendLine();
                var links = requirement.GetProperty("testCaseIds").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (links.Length == 0) sb.AppendLine("  - No linked case within this snapshot");
                foreach (var id in links)
                    sb.Append("  - Test case #").Append(id).Append(": ")
                        .Append(E(cases[id].GetProperty("title").GetString())).AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string E(string? text) => (text ?? "").Replace("`", "'")
        .Replace("[", "(").Replace("]", ")").Replace("|", "/").Replace("<", "&lt;").Replace(">", "&gt;");
}
