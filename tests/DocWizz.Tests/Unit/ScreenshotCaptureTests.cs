using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class ScreenshotCaptureTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/materials", true)]
    [InlineData("/inventory/item-1", true)]
    [InlineData("/größen-prüfung", true)]
    [InlineData("/gr%C3%B6%C3%9Fe", false)]
    [InlineData("/materials/:id", false)]
    [InlineData("/orders/{id}", false)]
    [InlineData("/items/*", false)]
    [InlineData("//external.example", false)]
    [InlineData("/../admin", false)]
    [InlineData("/a/%2e%2e", false)]
    [InlineData("/list?secret=x", false)]
    [InlineData("https://example.test/page", false)]
    public void Only_static_relative_paths_can_be_captured(string route, bool expected) =>
        Assert.Equal(expected, ScreenshotCapture.IsStaticRoute(route));

    [Fact]
    public void Reuses_and_deduplicates_discovered_routes()
    {
        var model = Model([
            N("route:/materials", "src/router.ts", "route", route: "/materials"),
            N("route:/orders/:id", "src/router.ts", "route", route: "/orders/:id"),
            N("route:/home", "src/router.ts", "route", route: "/"),
            N("route:/materials-2", "src/router2.ts", "route", route: "/materials")
        ]);
        Assert.Equal(["/", "/materials"], ScreenshotCapture.Routes(model));
    }

    [Fact]
    public void Screenshot_names_are_safe_deterministic_and_collision_resistant()
    {
        Assert.Equal(ScreenshotCapture.FileName("/a/b"), ScreenshotCapture.FileName("/a/b"));
        Assert.NotEqual(ScreenshotCapture.FileName("/a/b"), ScreenshotCapture.FileName("/a-b"));
        Assert.Matches(@"^[a-z0-9-]+\.png$", ScreenshotCapture.FileName("/a/b"));
        Assert.StartsWith("home-", ScreenshotCapture.FileName("/"));
        Assert.StartsWith("groessen-pruefung-", ScreenshotCapture.FileName("/größen-prüfung"));
    }

    [Theory]
    [InlineData(200, "/orders", "/login?redirect=/orders", "redirected to /login (needs authentication?)")]
    [InlineData(200, "/orders", "/dashboard", "redirected to /dashboard (needs authentication?)")]
    [InlineData(200, "/orders", "/orders?tab=all", null)]
    [InlineData(401, "/orders", "/orders", "HTTP 401")]
    [InlineData(403, "/orders", "/orders", "HTTP 403")]
    [InlineData(404, "/orders", "/orders", "HTTP 404")]
    [InlineData(500, "/orders", "/orders", "HTTP 500")]
    public void Guard_redirects_and_http_failures_are_not_captured(int status, string requested, string landed, string? reason)
    {
        var origin = "http://localhost:5173";
        Assert.Equal(reason, ScreenshotCapture.NavigationFailure(new Uri(origin + requested), new Uri(origin + landed), status));
    }

    [Fact]
    public void Umlaut_routes_match_the_percent_encoded_url_the_browser_reports()
    {
        var requested = new Uri(new Uri("http://localhost:5173/"), "größen-prüfung");
        Assert.Null(ScreenshotCapture.NavigationFailure(requested, new Uri("http://localhost:5173/gr%c3%b6%c3%9fen-pr%c3%bcfung"), 200));
        Assert.Equal("redirected to /anmeldung-für-kunden (needs authentication?)", ScreenshotCapture.NavigationFailure(
            requested, new Uri("http://localhost:5173/anmeldung-f%C3%BCr-kunden"), 200));
    }

    [Fact]
    public void Cross_origin_redirect_is_not_accepted_even_when_path_is_unchanged()
    {
        var reason = ScreenshotCapture.NavigationFailure(
            new Uri("http://localhost:5173/orders"), new Uri("https://other.example/orders?token=secret"), 200);
        Assert.Contains("redirected to another origin", reason);
        Assert.DoesNotContain("token=secret", reason);
        Assert.Equal("no HTTP response", ScreenshotCapture.NavigationFailure(
            new Uri("http://localhost/orders"), new Uri("http://localhost/orders"), null));
    }

    [Fact]
    public void Saved_login_state_is_applied_to_each_isolated_context()
    {
        var anonymous = ScreenshotCapture.ContextOptions(null);
        Assert.Null(anonymous.StorageStatePath);
        Assert.Equal(1440, anonymous.ViewportSize?.Width);
        Assert.Equal(900, anonymous.ViewportSize?.Height);

        var authenticated = ScreenshotCapture.ContextOptions("/tmp/sample-auth-state.json");
        Assert.Equal("/tmp/sample-auth-state.json", authenticated.StorageStatePath);
        Assert.Equal(1440, authenticated.ViewportSize?.Width);
    }

    [Fact]
    public void Later_routes_start_from_the_previous_routes_session_not_the_file()
    {
        // Apps that rotate refresh tokens invalidate the file's token on the first route.
        var carried = ScreenshotCapture.ContextOptions("/tmp/sample-auth-state.json", "{\"cookies\":[],\"origins\":[]}");
        Assert.Null(carried.StorageStatePath);
        Assert.Equal("{\"cookies\":[],\"origins\":[]}", carried.StorageState);
        Assert.Equal(1440, carried.ViewportSize?.Width);
    }

    [Theory]
    [InlineData("/logout", true)]
    [InlineData("/auth/sign-out", true)]
    [InlineData("/Abmelden", true)]
    [InlineData("/", false)]
    [InlineData("/logout-history", false)]
    [InlineData("/catalog", false)]
    public void Routes_that_end_the_session_are_recognized(string route, bool expected) =>
        Assert.Equal(expected, ScreenshotCapture.EndsSession(route));

    [Theory]
    [InlineData("/orders", true, true, "shows a login form (session not accepted?)")]
    [InlineData("/orders", true, false, null)]
    [InlineData("/orders", false, true, null)]
    [InlineData("/login", true, true, null)]
    [InlineData("/settings/password", true, true, null)]
    public void A_login_form_on_a_signed_in_route_is_not_captured(string route, bool signedIn, bool passwordField, string? reason) =>
        Assert.Equal(reason, ScreenshotCapture.LoginFormFailure(route, signedIn, passwordField));

    [Fact]
    public void Generated_markdown_and_html_link_only_successfully_captured_images()
    {
        using var repo = new Sources(("src/router.ts", "// routes"));
        var outDir = Path.Combine(repo.Root, "docs");
        var image = "screenshots/materials.png";
        Directory.CreateDirectory(Path.Combine(outDir, "screenshots"));
        File.WriteAllBytes(Path.Combine(outDir, image),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/Ss8AAAAASUVORK5CYII="));
        var model = Model([
            N("route:/materials", "src/router.ts", "route", route: "/materials"),
            N("route:/missing", "src/router.ts", "route", route: "/missing")
        ]);
        var config = Config();
        var arch = Architecture.Check(model, config);
        Generator Make(IReadOnlyDictionary<string, string> shots) =>
            new(repo.Root, outDir, model, [], arch, config, new Dictionary<string, string>())
            { WriteHtml = true, Screenshots = shots };

        Make(new Dictionary<string, string>
        {
            ["/materials"] = image,
            ["/missing"] = "screenshots/not-created.png"
        }).Run();

        var markdown = File.ReadAllText(Path.Combine(outDir, "frontend.md"));
        var html = File.ReadAllText(Path.Combine(outDir, "frontend.html"));
        Assert.Contains("![Screenshot of /materials](screenshots/materials.png)", markdown);
        Assert.Contains("| `/materials` |", markdown);
        Assert.Contains("| `/missing` |", markdown);
        Assert.Contains("src=\"screenshots/materials.png\"", html);
        Assert.DoesNotContain("not-created.png", markdown);
        Assert.DoesNotContain("not-created.png", html);

        Make(new Dictionary<string, string>()).Run();
        Assert.DoesNotContain("## Screenshots", File.ReadAllText(Path.Combine(outDir, "frontend.md")));
    }
}
