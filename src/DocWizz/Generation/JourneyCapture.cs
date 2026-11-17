using System.Text.RegularExpressions;
using Microsoft.Playwright;
using YamlDotNet.Serialization;

// User guides captured by docwizz itself: one reviewed journey file per guide, played against the running application.
// A step's picture is taken before its action, with the control outlined where the browser reports it. A control that
// is missing or ambiguous fails the generation, so a guide never describes a click the application does not offer.
static class JourneyCapture
{
    public sealed record Action(string Kind, string Target, string? Value, string Note);
    public sealed record Journey(string Id, string File, string Title, string Intro, string Result, int WorkItem, IReadOnlyList<Action> Steps);

    sealed class Source
    {
        public string? Title { get; set; }
        public string? Intro { get; set; }
        public string? Result { get; set; }
        public int Requirement { get; set; }
        public List<Dictionary<string, string>>? Steps { get; set; }
    }

    static readonly string[] Kinds = ["open", "click", "fill"];
    static readonly Regex Variable = new(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.CultureInvariant);

    /// <summary>The journey files (*.yaml) of a directory, in file-name order: the order they are played in.</summary>
    /// <exception cref="InvalidDataException">A file is not a valid journey; the message names the file and the step.</exception>
    public static IReadOnlyList<Journey> Load(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        Journey[] journeys = [.. Directory.GetFiles(directory, "*.yaml").Order(StringComparer.Ordinal).Select(Parse)];
        // The id names the guide's page and pictures: two files must not share one, nor take the index's or manual's.
        if (journeys.GroupBy(j => j.Id).FirstOrDefault(g => g.Count() > 1) is { } same)
            throw new InvalidDataException($"{string.Join(" and ", same.Select(j => j.File))} would write the same guide ({same.Key}): rename one");
        if (journeys.FirstOrDefault(j => j.Id is "index" or "manual") is { } reserved)
            throw new InvalidDataException($"{reserved.File}: '{reserved.Id}' is the name of a generated page: rename the file");
        return journeys;
    }

    public static Journey Parse(string file)
    {
        var name = Path.GetFileName(file);
        InvalidDataException Invalid(string problem) => new($"{name}: {problem}");
        Source source;
        try { source = new DeserializerBuilder().WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance).Build().Deserialize<Source>(File.ReadAllText(file)) ?? new(); }
        catch (YamlDotNet.Core.YamlException e) { throw Invalid(e.InnerException?.Message ?? e.Message); }
        if (string.IsNullOrWhiteSpace(source.Title)) throw Invalid("needs a title");
        if (source.Requirement < 0) throw Invalid("requirement must be a work item number");
        var steps = new List<Action>();
        foreach (var (step, number) in (source.Steps ?? []).Select((s, i) => (s, i + 1)))
        {
            var kinds = step.Keys.Intersect(Kinds).ToList();
            if (kinds.Count != 1 || step.Keys.Except([.. Kinds, "with", "note"]).FirstOrDefault() is not null || string.IsNullOrWhiteSpace(step[kinds[0]]))
                throw Invalid($"step {number} needs exactly one of open, click or fill, and otherwise only with and note");
            var kind = kinds[0];
            if (step.ContainsKey("with") != (kind == "fill")) throw Invalid($"step {number}: 'with' belongs to fill, and fill needs it");
            if (kind == "open" && (!step[kind].StartsWith('/') || step[kind].StartsWith("//"))) throw Invalid($"step {number}: open needs a path such as /login");
            steps.Add(new(kind, step[kind].Trim(), step.GetValueOrDefault("with"), OneLine(step.GetValueOrDefault("note"))));
        }
        if (steps.FirstOrDefault()?.Kind != "open") throw Invalid("the first step must open a page, e.g. open: /login");
        var id = Regex.Replace(Naming.Ascii(Path.GetFileNameWithoutExtension(file)).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (id.Length == 0) throw Invalid("the file name needs letters or digits");
        return new(id, name, OneLine(source.Title), OneLine(source.Intro), OneLine(source.Result), source.Requirement, steps);
    }

    static string OneLine(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    // What the reader is told to do. A filled value is never printed: it may be a credential.
    public static string Instruction(Action step) => step.Kind switch
    {
        "open" => $"Open {step.Target}",
        "fill" => $"Fill in “{Label(step)}”",
        _ => $"Select “{Label(step)}”",
    };

    static string Label(Action step) => step.Target.StartsWith("css=", StringComparison.Ordinal) ? "the highlighted control" : step.Target;

    // `${NAME}` reads the environment, so credentials stay out of the journey file.
    public static string Value(Action step) => Variable.Match(step.Value!) is { Success: true } m
        ? Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? throw new InvalidDataException($"environment variable {m.Groups[1].Value} is not set")
        : step.Value!;

    /// <summary>Plays every journey in one browser session, so a sign-in journey signs the later ones in.</summary>
    /// <returns>The guides, their pictures written to user-guides/images.</returns>
    /// <exception cref="InvalidDataException">A page or control was not found; nothing is rendered from a failed journey.</exception>
    public static async Task<IReadOnlyList<JourneyEvidence.Guide>> Run(IReadOnlyList<Journey> journeys, CodeModel model, string outDir,
        string baseUrl, string? storageStateFile = null, int delayMs = 0)
    {
        var target = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var hash = ScreenshotCapture.HashRoutes(model).Count > 0;
        var images = Path.Combine(outDir, "user-guides", "images");
        Directory.CreateDirectory(images);
        var guides = new List<JourneyEvidence.Guide>();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(ScreenshotCapture.ContextOptions(storageStateFile));
        var page = await context.NewPageAsync();
        foreach (var journey in journeys)
        {
            var steps = new List<JourneyEvidence.Step>();
            async Task<string> Shot(string name)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(images, $"guide-{journey.Id}-{name}.png") });
                return $"images/guide-{journey.Id}-{name}.png";
            }
            foreach (var (step, number) in journey.Steps.Select((s, i) => (s, i + 1)))
                try
                {
                    string image;
                    if (step.Kind == "open")
                    {
                        var response = await page.GotoAsync(ScreenshotCapture.RouteUrl(target, step.Target, hash).ToString(), new() { Timeout = 15000 });
                        if (response is { Status: >= 400 }) throw new InvalidDataException($"HTTP {response.Status}");
                        await ScreenshotCapture.Settle(page, delayMs);
                        image = await Shot($"step-{number:D2}");
                    }
                    else
                    {
                        var control = await Find(page, step);
                        await control.ScrollIntoViewIfNeededAsync();
                        await control.EvaluateAsync(Outline);
                        image = await Shot($"step-{number:D2}");
                        await page.EvaluateAsync("() => document.getElementById('docwizz-outline')?.remove()");
                        if (step.Kind == "fill") await control.FillAsync(Value(step)); else await control.ClickAsync();
                        await ScreenshotCapture.Settle(page, delayMs);
                    }
                    steps.Add(new(number, Instruction(step), "", image, Note: step.Note));
                }
                catch (Exception e) when (e is PlaywrightException or TimeoutException or InvalidDataException)
                {
                    throw new InvalidDataException($"{journey.File}: step {number} ({step.Kind} {step.Target}): {e.Message.Split('\n')[0]}");
                }
            guides.Add(new(journey.Id, journey.Title, journey.Steps[0].Target, journey.WorkItem, 0, 0, 0, 0, "", [], steps,
                journey.Intro, journey.Result, await Shot("result"), journey.File));
        }
        return guides;
    }

    // Drawn in the page for the screenshot only, around the control's own rectangle: it cannot drift from the control.
    const string Outline = """
        control => { const b = control.getBoundingClientRect(), d = document.createElement('div'); d.id = 'docwizz-outline';
          d.style.cssText = `position:fixed;left:${b.x - 6}px;top:${b.y - 6}px;width:${b.width + 12}px;height:${b.height + 12}px;` +
            'box-sizing:border-box;border:3px solid #d6336c;border-radius:8px;box-shadow:0 0 0 5px rgba(214,51,108,.25);z-index:2147483647;pointer-events:none';
          document.documentElement.append(d); }
        """;

    // The one visible control with this name: a field by its label or placeholder, anything else by its role or text.
    // `css=` selects by CSS where a control has no usable name.
    static async Task<ILocator> Find(IPage page, Action step)
    {
        var name = step.Target;
        ILocator[] candidates = name.StartsWith("css=", StringComparison.Ordinal) ? [page.Locator(name[4..])]
            : step.Kind == "fill" ? [page.GetByLabel(name, new() { Exact = true }), page.GetByPlaceholder(name, new() { Exact = true })]
            : [.. new[] { AriaRole.Button, AriaRole.Link, AriaRole.Tab, AriaRole.Menuitem }.Select(role => page.GetByRole(role, new() { Name = name, Exact = true })),
                page.GetByText(name, new() { Exact = true })];
        for (var waited = 0; ; waited += 250)
        {
            foreach (var candidate in candidates)
            {
                var visible = candidate.Filter(new() { Visible = true });
                var count = await visible.CountAsync();
                if (count == 1) return visible;
                if (count > 1) throw new InvalidDataException($"{count} visible controls match; use a more specific name or css=<selector>");
            }
            if (waited >= 5000) throw new InvalidDataException("no visible control with this name");
            await page.WaitForTimeoutAsync(250);
        }
    }
}
