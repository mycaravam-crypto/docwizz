using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Offline TestWizz screenshot evidence; never executes tests or calls an external provider.</summary>
static class JourneyEvidence
{
    public sealed record Step(int Id, string Action, string Expected, string Image);
    public sealed record Guide(string Id, string Title, string Route, int WorkItem, int WorkItemRevision,
        int TestCase, int TestCaseRevision, int Plan, string Project,
        IReadOnlyList<string> Criteria, IReadOnlyList<Step> Steps);

    private static readonly Regex SafeId = new(@"^wi-[0-9]+-case-[0-9]+-[a-f0-9]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex GeneratedImage = new(@"^wi-[0-9]+-case-[0-9]+-[a-f0-9]{12}-step-[0-9]{4}\.png$", RegexOptions.CultureInvariant);
    private static readonly Regex ImageName = new(@"^screenshots/step-[0-9]{4}\.png$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Validates all evidence before copying any screenshot to generated documentation.</summary>
    public static IReadOnlyList<Guide> Import(string? directory, string output)
    {
        if (directory is null) return [];
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(directory))
            throw new InvalidDataException("Journey evidence directory not found: " + directory);
        bool directBundle = File.Exists(Path.Combine(directory, "manifest.json"));
        string[] sources = directBundle
            ? [directory] : Directory.GetDirectories(directory).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).ToArray();
        if (sources.Length == 0) throw new InvalidDataException("No TestWizz journey manifests found.");
        var guides = new List<Guide>();
        var images = new List<(string Source, string Relative, string Sha256)>();
        foreach (var source in sources.Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symlinked evidence directories are not supported.");
            string id = Path.GetFileName(source);
            if (!SafeId.IsMatch(id) && !directBundle)
                throw new InvalidDataException("Unsafe journey directory name.");
            var manifest = Path.Combine(source, "manifest.json");
            if (new FileInfo(manifest).Length is < 1 or > 65536 ||
                (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid journey manifest.");
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = json.RootElement;
            if (root.GetProperty("schema").GetString() != "testwizz.ui-evidence/v1" ||
                root.GetProperty("status").GetString() != "passed")
                throw new InvalidDataException("Only passed testwizz.ui-evidence/v1 manifests are accepted.");
            var metadata = root.GetProperty("source");
            var workItem = Positive(metadata, "workItemId");
            var workRevision = Positive(metadata, "workItemRevision");
            var testCase = Positive(metadata, "testCaseId");
            if (!SafeId.IsMatch(id))
                id = $"wi-{workItem}-case-{testCase}-" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest)))[..12];
            if (guides.Any(g => g.Id == id) || !id.StartsWith($"wi-{workItem}-case-{testCase}-", StringComparison.Ordinal))
                throw new InvalidDataException("Journey directory does not match its ADO work-item/test-case IDs.");
            var caseRevision = Positive(metadata, "testCaseRevision");
            var plan = Positive(metadata, "planId");
            if (metadata.GetProperty("relation").GetString() != "selected-not-verified")
                throw new InvalidDataException("Unsupported requirement/test-case relation provenance.");
            var project = Required(metadata, "project", 150);
            var title = Required(root, "title", 250);
            var route = Required(root, "route", 300);
            if (!route.StartsWith("/", StringComparison.Ordinal) || route.StartsWith("//", StringComparison.Ordinal) || route.Contains('?') || route.Contains('#'))
                throw new InvalidDataException("Invalid route in journey evidence.");
            var criteria = root.GetProperty("acceptanceCriteria").EnumerateArray()
                .Select(c => Required(c, "text", 500)).ToArray();
            if (criteria.Length is < 1 or > 5)
                throw new InvalidDataException("Journey evidence requires 1 to 5 explicit criteria.");
            var stepsJson = root.GetProperty("steps");
            if (stepsJson.GetArrayLength() is < 1 or > 6)
                throw new InvalidDataException("Journey evidence requires 1 to 6 steps.");
            var steps = new List<Step>();
            foreach (var item in stepsJson.EnumerateArray())
            {
                var stepId = Positive(item, "stepId");
                if (steps.Any(s => s.Id == stepId) || item.GetProperty("result").GetString() != "passed")
                    throw new InvalidDataException("Duplicate or unverified journey step.");
                var relative = Required(item, "screenshot", 100);
                if (!ImageName.IsMatch(relative) || relative != $"screenshots/step-{stepId:D4}.png")
                    throw new InvalidDataException("Unsafe or mismatched screenshot path.");
                var expectedHash = Required(item, "sha256", 64);
                if (!Hash.IsMatch(expectedHash))
                    throw new InvalidDataException("Invalid screenshot SHA-256.");
                var screenshots = Path.Combine(source, "screenshots");
                var file = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file) ||
                    (File.GetAttributes(screenshots) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(file).Length is < 32 or > 10485760)
                    throw new InvalidDataException("Missing, symlinked or oversized screenshot.");
                var data = File.ReadAllBytes(file);
                if (!data.AsSpan().StartsWith(PngHeader) ||
                    Convert.ToHexStringLower(SHA256.HashData(data)) != expectedHash)
                    throw new InvalidDataException("Screenshot content differs from evidence SHA-256.");
                var target = id + "-step-" + stepId.ToString("D4") + ".png";
                images.Add((file, target, expectedHash));
                string expected = item.TryGetProperty("expected", out var value) ? value.GetString() ?? "" : "";
                if (expected.Length > 500)
                    throw new InvalidDataException("Oversized expected result.");
                steps.Add(new(stepId, Required(item, "action", 500), expected, "images/" + target));
            }
            guides.Add(new(id, title, route, workItem, workRevision, testCase, caseRevision,
                plan, project, criteria, steps));
        }
        var guidesFolder = Path.Combine(output, "user-guides");
        if (Directory.Exists(guidesFolder) &&
            (File.GetAttributes(guidesFolder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Generated user-guides directory must not be a symlink.");
        var imageFolder = Path.Combine(guidesFolder, "images");
        if (Directory.Exists(imageFolder) &&
            (File.GetAttributes(imageFolder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Generated screenshot directory must not be a symlink.");
        Directory.CreateDirectory(imageFolder);
        foreach (var image in images)
        {
            var target = Path.Combine(imageFolder, image.Relative);
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Generated screenshot target must not be a symlink.");
            if (!File.Exists(target) ||
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target))) != image.Sha256)
                File.Copy(image.Source, target, overwrite: true);
        }
        return guides;
    }

    /// <summary>Remove only obsolete TestWizz-named screenshots after successful documentation rendering.</summary>
    public static void CleanupImages(string output, IReadOnlyList<Guide> guides)
    {
        var guidesFolder = Path.Combine(output, "user-guides");
        if (Directory.Exists(guidesFolder) &&
            (File.GetAttributes(guidesFolder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Generated user-guides directory must not be a symlink.");
        var folder = Path.Combine(guidesFolder, "images");
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Generated screenshot directory must not be a symlink.");
        var current = guides.SelectMany(g => g.Steps.Select(s => Path.GetFileName(s.Image)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*.png"))
        {
            var name = Path.GetFileName(path);
            if (!GeneratedImage.IsMatch(name) || current.Contains(name)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Obsolete screenshot must not be a symlink.");
            File.Delete(path);
        }
    }

    private static int Positive(JsonElement obj, string name)
    {
        var n = obj.GetProperty(name).GetInt32();
        if (n <= 0) throw new InvalidDataException("Invalid journey " + name);
        return n;
    }

    private static string Required(JsonElement obj, string name, int max)
    {
        var text = obj.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max ||
            text.IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new InvalidDataException("Missing or unsafe journey " + name);
        return text;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("<", "&lt;")
        .Replace(">", "&gt;").Replace("[", "\\[").Replace("]", "\\]")
        .Replace("*", "\\*").Replace("_", "\\_")
        .Replace("\r", " ").Replace("\n", " ");

    public static string Index(IReadOnlyList<Guide> guides) =>
        "# User guides (review required)\n\nScreenshot-backed drafts from TestWizz evidence. Passing visibility assertions are not acceptance verification.\n\n" +
        string.Join("\n", guides.Select(g => "- [" + Escape(g.Title) + "](" + g.Id + ".md) — WI-" +
            g.WorkItem + ", ADO case #" + g.TestCase)) + "\n";

    public static string Markdown(Guide guide)
    {
        var lines = new List<string> {
            "# " + Escape(guide.Title), "",
            "**Draft — human review required.** Screenshots were captured after Playwright visibility assertions; " +
                "they do not establish full acceptance or usability.", "",
            $"ADO project: {Escape(guide.Project)} · Work item #{guide.WorkItem} (revision {guide.WorkItemRevision}) · " +
                $"Test case #{guide.TestCase} (revision {guide.TestCaseRevision}) · Plan #{guide.Plan}.", "",
            "Requirement-to-test-case relation: **selected, not independently verified**.", "",
            "Route: " + Escape(guide.Route), "",
            "## Acceptance criteria (not individually verified)", "",
        };
        lines.AddRange(guide.Criteria.Select((c, i) => $"- AC{i + 1}: {Escape(c)}"));
        lines.Add("");
        lines.Add("## Instructions");
        lines.Add("");
        foreach (var step in guide.Steps)
        {
            lines.Add($"### Step {step.Id}");
            lines.Add("");
            lines.Add(Escape(step.Action));
            lines.Add("");
            if (!string.IsNullOrWhiteSpace(step.Expected))
            {
                lines.Add("Expected result: " + Escape(step.Expected));
                lines.Add("");
            }
            lines.Add($"![Observed UI after step {step.Id}]({step.Image})");
            lines.Add("");
        }
        return string.Join("\n", lines) + "\n";
    }
}
