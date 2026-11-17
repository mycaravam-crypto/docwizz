namespace DocWizz.Tests.Unit;

// #311: a journey file is a guide's reviewed script; what it cannot express fails before a browser starts.
public class JourneyCaptureTests
{
    static JourneyCapture.Journey Parse(string yaml)
    {
        using var repo = new Sources(("journeys/01 Sign in.yaml", yaml));
        return JourneyCapture.Parse(repo.Files[0]);
    }

    const string SignIn = """
        title: Sign in
        requirement: 7
        intro: You need an account.
        result: The start page opens.
        steps:
          - open: /login
          - fill: Password
            with: ${DOCWIZZ_TEST_PASSWORD}
            note: |
              Ask your administrator
              when you have none.
          - click: css=button[type=submit]
        """;

    [Fact]
    public void A_journey_file_becomes_steps_and_never_prints_what_is_filled_in()
    {
        var journey = Parse(SignIn);
        Assert.Equal(("01-sign-in", "Sign in", 7), (journey.Id, journey.Title, journey.WorkItem));
        Assert.Equal(["Open /login", "Fill in “Password”", "Select “the highlighted control”"], journey.Steps.Select(JourneyCapture.Instruction));
        Assert.Equal("Ask your administrator when you have none.", journey.Steps[1].Note);

        // The value comes from the environment at capture time; unset, the journey fails instead of typing the placeholder.
        Assert.Contains("DOCWIZZ_TEST_PASSWORD is not set", Assert.Throws<InvalidDataException>(() => JourneyCapture.Value(journey.Steps[1])).Message);
        Assert.Equal("plain", JourneyCapture.Value(new("fill", "Name", "plain", "")));
    }

    [Theory]
    [InlineData("steps:\n  - open: /a", "needs a title")]
    [InlineData("title: T\nsteps:\n  - click: Save", "first step must open a page")]
    [InlineData("title: T\nsteps:\n  - open: https://elsewhere.test/", "open needs a path")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - click: Save\n    fill: Name", "exactly one of open, click or fill")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - click: Save\n    typo: x", "exactly one of open, click or fill")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - fill: Name", "fill needs it")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  - click: Save\n    with: x", "'with' belongs to fill")]
    [InlineData("title: T\nsteps:\n  - open: /a\n  -\n", "step 2 is empty")]
    [InlineData("title: T\nunknown: 1\nsteps:\n  - open: /a", "01 Sign in.yaml")]
    public void An_invalid_journey_file_names_its_problem(string yaml, string problem) =>
        Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => Parse(yaml)).Message);

    [Theory]
    [InlineData("sign-in.yaml", "Sign_in.yaml", "would write the same guide (sign-in)")]
    [InlineData("a.yaml", "index.yaml", "'index' is the name of a generated page")]
    public void Journey_files_that_would_overwrite_a_page_are_rejected(string first, string second, string problem)
    {
        using var repo = new Sources((first, "title: A\nsteps:\n  - open: /a"), (second, "title: B\nsteps:\n  - open: /b"));
        Assert.Contains(problem, Assert.Throws<InvalidDataException>(() => JourneyCapture.Load(repo.Root)).Message);
    }

    [Fact]
    public void A_captured_guide_explains_before_during_and_after_and_names_its_journey_file()
    {
        var guide = new JourneyEvidence.Guide("sign-in", "Sign in", "/login", 0, 0, 0, 0, 0, "", [],
            [new(1, "Open /login", "", "images/guide-sign-in-step-01.png", Note: "The page appears when you are signed out.")],
            "You need an account.", "The start page opens.", "images/guide-sign-in-result.png", "01-sign-in.yaml");
        var markdown = JourneyEvidence.Markdown(guide);
        Assert.Contains("`/login`\n\nYou need an account.\n\n1. **Open /login**\n\n   The page appears when you are signed out.\n\n   ![Step 1]", markdown);
        Assert.Contains("**Result:** The start page opens.\n\n![Result](images/guide-sign-in-result.png)", markdown);
        Assert.Contains("Captured by docwizz from `01-sign-in.yaml`", markdown);
        Assert.DoesNotContain("WI-", markdown);   // no requirement given
        Assert.DoesNotContain("Captured by docwizz", JourneyEvidence.Manual([guide]));

        // A captured guide has no work-item revision, so it is never "older" than the export.
        var index = new RequirementIndex.Index(RequirementIndex.Schema, "f", new(RequirementIndex.HistorySchema, "d", "ado", null, null, null, null, false, false, null, []),
            [], [], [new(7, 2, 2, true, "User Story", "Active", "Sign in", null, null, null, null, null, [], "x", [])], []);
        Assert.Empty(GuideStaleness.Stale([guide with { WorkItem = 7 }], index));
    }

    [Fact]
    public void Captured_pictures_are_removed_with_their_guide_and_manual_pictures_stay()
    {
        using var repo = new Sources(("docs/user-guides/images/guide-sign-in-step-01.png", ""), ("docs/user-guides/images/guide-sign-in-result.png", ""),
            ("docs/user-guides/images/team-photo.png", ""));
        JourneyEvidence.CleanupImages(Path.Combine(repo.Root, "docs"), []);
        Assert.Equal(["team-photo.png"], Directory.GetFiles(Path.Combine(repo.Root, "docs/user-guides/images")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_capture_is_kept_for_generations_without_the_running_application_while_its_pictures_exist()
    {
        using var repo = new Sources(("docs/.docwizz/x", ""), ("docs/user-guides/images/guide-sign-in-step-01.png", ""), ("docs/user-guides/images/guide-sign-in-result.png", ""));
        var docs = Path.Join(repo.Root, "docs");
        var guide = new JourneyEvidence.Guide("sign-in", "Sign in", "/login", 7, 0, 0, 0, 0, "", [],
            [new(1, "Open /login", "", "images/guide-sign-in-step-01.png", Note: "Why")], "Intro", "Done", "images/guide-sign-in-result.png", "01-sign-in.yaml", "2026-11-17", "abc");
        var testwizz = guide with { Id = "wi-1-case-1-aaaaaaaaaaaa", JourneyFile = null };
        JourneyCapture.Save(docs, [guide, testwizz]);

        var kept = Assert.Single(JourneyCapture.Retained(docs));   // TestWizz guides come from their evidence, not from here
        Assert.Equal((guide.Title, guide.WorkItem, guide.Captured, guide.Code, "Why"), (kept.Title, kept.WorkItem, kept.Captured, kept.Code, kept.Steps[0].Note));
        Assert.Contains("from `01-sign-in.yaml` on 2026-11-17:", JourneyEvidence.Markdown(kept));

        File.Delete(Path.Join(docs, "user-guides/images/guide-sign-in-result.png"));
        Assert.Empty(JourneyCapture.Retained(docs));
        JourneyCapture.Save(docs, [testwizz]);
        Assert.False(File.Exists(Path.Join(docs, ".docwizz/guides.json")));
    }

    [Fact]
    public void A_captured_guide_is_stale_once_the_code_behind_its_route_changes()
    {
        using var repo = new Sources(("src/Login.vue", "<template>\na</template>"), ("src/Other.vue", "x"));
        var model = new CodeModel(null, [new("r", "route", "/login", "src/router.ts", 1, Route: "/login"), new("c", "component", "Login", "src/Login.vue", 1),
            new("o", "component", "Other", "src/Other.vue", 1)], [new("r", "c", "routes-to")]);
        var flow = new FlowGraph(model);
        var guide = new JourneyEvidence.Guide("sign-in", "Sign in", "/login", 0, 0, 0, 0, 0, "", [], [], JourneyFile: "a.yaml", Captured: "2026-11-17",
            Code: GuideStaleness.Code(repo.Root, model, flow, "/login"));
        Assert.NotNull(guide.Code);
        Assert.Null(GuideStaleness.Code(repo.Root, model, flow, "/unknown"));

        File.WriteAllText(Path.Join(repo.Root, "src/Other.vue"), "changed");   // not behind the route
        File.WriteAllText(Path.Join(repo.Root, "src/Login.vue"), "<template>\r\na</template>");   // other line endings only
        Assert.False(GuideStaleness.CodeChanged(guide, repo.Root, model, flow));
        Assert.Equal("", GuideStaleness.Section([guide], null, g => GuideStaleness.CodeChanged(g, repo.Root, model, flow)));

        File.WriteAllText(Path.Join(repo.Root, "src/Login.vue"), "<template>b</template>");
        Assert.True(GuideStaleness.CodeChanged(guide, repo.Root, model, flow));
        Assert.Contains("- [Sign in](sign-in.md): the code behind `/login` changed since the capture on 2026-11-17",
            GuideStaleness.Section([guide], null, g => GuideStaleness.CodeChanged(g, repo.Root, model, flow)));
        Assert.Contains("**Stale:** the code behind this route changed", JourneyEvidence.Markdown(guide, codeChanged: true));
    }
}
