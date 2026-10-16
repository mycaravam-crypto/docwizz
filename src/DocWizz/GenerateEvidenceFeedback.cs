// Report only local, validated evidence availability; no implied test execution or coverage.
static class GenerateEvidenceFeedback
{
    public static string[] Lines(string root)
    {
        var lines = new List<string>();
        var requirements = Path.Combine(root, RequirementIndex.DefaultFile);
        if (!File.Exists(requirements))
            lines.Add("Requirements  not imported (docwizz requirements import)");
        else
        {
            try
            {
                RequirementIndex.Load(requirements);
                lines.Add("Requirements  imported (historical work-item links; not verified test coverage)");
            }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException)
            {
                lines.Add("Requirements  invalid index: " + e.Message);
            }
        }

        var folder = Path.Combine(root, "docs", ".docwizz");
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "ado-tests-*.json").Order(StringComparer.Ordinal).ToArray()
            : [];
        if (files.Length == 0)
        {
            lines.Add("ADO tests     not imported (docwizz tests import)");
            return lines.ToArray();
        }
        var plans = new HashSet<int>();
        var cases = 0;
        var linked = 0;
        var invalid = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var data = AdoTestSnapshot.Load(file);
                plans.UnionWith(data.PlanIds);
                cases += data.CaseCount;
                linked += data.LinkedCount;
            }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                invalid.Add(Path.GetFileName(file) + ": " + e.Message);
            }
        }
        lines.Add($"ADO tests     {plans.Count} plans, {cases} cases, {linked} linked requirements (explicit links; not execution or code coverage)");
        foreach (var error in invalid) lines.Add("ADO tests     invalid snapshot: " + error);
        return lines.ToArray();
    }
}
