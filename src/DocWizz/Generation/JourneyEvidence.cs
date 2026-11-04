using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Offline TestWizz screenshot evidence; never executes tests or calls an external provider.</summary>
static class JourneyEvidence
{
    public sealed record Callout(int Number, int StepId, string Target, int X, int Y);
    public sealed record Step(int Id, string Action, string Expected, string Image,
        IReadOnlyList<Callout>? Callouts = null);
    public sealed record Guide(string Id, string Title, string Route, int WorkItem, int WorkItemRevision,
        int TestCase, int TestCaseRevision, int Plan, string Project,
        IReadOnlyList<string> Criteria, IReadOnlyList<Step> Steps);

    private static readonly Regex SafeId = new(@"^wi-[0-9]+-case-[0-9]+-[a-f0-9]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex GeneratedImage = new(@"^wi-[0-9]+-case-[0-9]+-[a-f0-9]{12}-step-[0-9]{4}(-guide)?\.png$", RegexOptions.CultureInvariant);
    private static readonly Regex GuideImage = new(@"^screenshots/step-[0-9]{4}-guide\.png$", RegexOptions.CultureInvariant);
    private static readonly Regex ImageName = new(@"^screenshots/step-[0-9]{4}\.png$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private const int ScreenshotViewportWidth = 1440;
    private const int ScreenshotViewportHeight = 900;
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
            int[] allStepIds = stepsJson.EnumerateArray().Select(i => Positive(i, "stepId")).ToArray();
            // A callout number is the one-based position of a reviewed journey action,
            // never an arbitrary marker index local to this screenshot.
            var stepIdByNumber = allStepIds.Select((id, position) => (id, number: position + 1))
                .ToDictionary(x => x.number, x => x.id);
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
                var callouts = new List<Callout>();
                if (item.TryGetProperty("guideScreenshot", out _))
                {
                    string guideRelative = Required(item, "guideScreenshot", 100);
                    if (!GuideImage.IsMatch(guideRelative) ||
                        guideRelative != $"screenshots/step-{stepId:D4}-guide.png")
                        throw new InvalidDataException("Unsafe or mismatched annotated screenshot path.");
                    string guideHash = Required(item, "guideSha256", 64);
                    if (!Hash.IsMatch(guideHash) || !item.TryGetProperty("callouts", out var markers) ||
                        markers.ValueKind != JsonValueKind.Array || markers.GetArrayLength() is < 1 or > 6)
                        throw new InvalidDataException("Invalid guide screenshot integrity or callout data.");
                    string guideFile = Path.Join(source, "screenshots", $"step-{stepId:D4}-guide.png");
                    if (!File.Exists(guideFile) || (File.GetAttributes(guideFile) & FileAttributes.ReparsePoint) != 0 ||
                        new FileInfo(guideFile).Length is < 32 or > 10485760)
                        throw new InvalidDataException("Missing or unsafe annotated screenshot.");
                    byte[] guideData = File.ReadAllBytes(guideFile);
                    if (!guideData.AsSpan().StartsWith(PngHeader) ||
                        Convert.ToHexStringLower(SHA256.HashData(guideData)) != guideHash)
                        throw new InvalidDataException("Annotated screenshot SHA-256 does not match evidence.");
                    foreach (var marker in markers.EnumerateArray())
                    {
                        int number = Positive(marker, "number");
                        int referencedStep = Positive(marker, "stepId");
                        int x = Coordinate(marker, "x", ScreenshotViewportWidth);
                        int y = Coordinate(marker, "y", ScreenshotViewportHeight);
                        string label = Required(marker, "target", 200);
                        if (!stepIdByNumber.TryGetValue(number, out int mappedStepId) ||
                            referencedStep != mappedStepId ||
                            callouts.Any(c => c.Number == number))
                            throw new InvalidDataException("Unreviewed or invalid callout coordinates or step mapping.");
                        callouts.Add(new(number, referencedStep, label, x, y));
                    }
                    if (!callouts.Any(c => c.Number == steps.Count + 1))
                        throw new InvalidDataException("Current action has no numbered callout.");
                    target = id + "-step-" + stepId.ToString("D4") + "-guide.png";
                    images.Add((guideFile, target, guideHash));
                }
                else
                {
                    if (item.TryGetProperty("callouts", out _) || item.TryGetProperty("guideSha256", out _))
                        throw new InvalidDataException("Incomplete guide callout metadata.");
                    images.Add((file, target, expectedHash)); // Backward-compatible unannotated evidence.
                }
                string expected = item.TryGetProperty("expected", out var value) ? value.GetString() ?? "" : "";
                if (expected.Length > 500)
                    throw new InvalidDataException("Oversized expected result.");
                steps.Add(new(stepId, Required(item, "action", 500), expected, "images/" + target, callouts));
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
        if (!obj.TryGetProperty(name, out var value) || !value.TryGetInt32(out int number) || number <= 0)
            throw new InvalidDataException("Invalid journey " + name);
        return number;
    }

    private static int Coordinate(JsonElement obj, string name, int limit)
    {
        if (!obj.TryGetProperty(name, out var value) || !value.TryGetInt32(out int number) ||
            number < 0 || number >= limit)
            throw new InvalidDataException("Invalid callout coordinate " + name);
        return number;
    }

    private static string Required(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing journey " + name);
        string? text = value.GetString();
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
        foreach (var (step, index) in guide.Steps.Select((step, index) => (step, index)))
        {
            lines.Add($"### Step {index + 1}");
            lines.Add("");
            lines.Add(Escape(step.Action) + $" (ADO test step #{step.Id})");
            lines.Add("");
            if (!string.IsNullOrWhiteSpace(step.Expected))
            {
                lines.Add("Expected result: " + Escape(step.Expected));
                lines.Add("");
            }
            lines.Add(step.Callouts is { Count: > 0 }
                ? $"![Controls numbered for instruction {index + 1}]({step.Image})"
                : $"![Observed UI after step {step.Id}]({step.Image})");
            lines.Add("");
            if (step.Callouts is { Count: > 0 })
            {
                lines.Add("Numbered controls visible in this screenshot:");
                lines.AddRange(step.Callouts.Select(c =>
                    $"- **{c.Number}** — {Escape(c.Target)}" +
                    (c.Number == index + 1 ? " (current instruction)" :
                     c.Number < index + 1 ? " (previous instruction)" : " (upcoming instruction)")));
                lines.Add("");
            }
        }
        return string.Join("\n", lines) + "\n";
    }
}
