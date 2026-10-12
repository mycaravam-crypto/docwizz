using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// Optional runtime capture: reuse the routes detected by the existing frontend scanner.
// Reuses an optional user-provided browser session; no login automation, passwords or AI.
static class ScreenshotCapture
{
    // Letters of any script count (/größen-prüfung); percent escapes, parameters and wildcards do not.
    static readonly Regex StaticPath = new(@"^/(?:[\p{L}\p{M}\p{N}._~-]+/?)*$", RegexOptions.CultureInvariant);

    public static bool IsStaticRoute(string? route) =>
        route is not null && StaticPath.IsMatch(route) &&
        !route.Split('/').Any(part => part is "." or "..");

    public static List<string> Routes(CodeModel model) =>
        model.Nodes.Where(n => n.Kind == "route").Select(n => n.Route)
            .Where(IsStaticRoute).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    // Routes from a hash-history router (createWebHashHistory, createHashRouter, ...) are opened as <base>/#/route.
    public static HashSet<string> HashRoutes(CodeModel model) =>
        model.Nodes.Where(n => n.Kind == "route" && n.Route is not null && n.Tags?.Contains("hash") == true)
            .Select(n => n.Route!).ToHashSet(StringComparer.Ordinal);

    public static Uri RouteUrl(Uri target, string route, bool hash) =>
        hash ? new Uri(target, "#" + route) : new Uri(target, route.TrimStart('/'));

    // Hash avoids collisions between routes such as /a-b and /a/b.
    public static string FileName(string route)
    {
        var slug = Regex.Replace(Naming.Ascii(route.Trim('/')).ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        if (slug.Length == 0) slug = "home";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route)))[..10].ToLowerInvariant();
        return $"{slug}-{hash}.png";
    }

    // Returns a capture failure reason; a client-side SPA guard can redirect after an HTTP 200.
    public static string? NavigationFailure(Uri requested, Uri landed, int? status, bool hash = false)
    {
        if (status is null) return "no HTTP response";
        if (status >= 400) return $"HTTP {status}";
        if (requested.Scheme != landed.Scheme || requested.Authority != landed.Authority)
            return $"redirected to another origin ({PathOf(landed)}) (needs authentication?)";
        if (PathOf(requested).TrimEnd('/') != PathOf(landed).TrimEnd('/'))
            return $"redirected to {PathOf(landed)} (needs authentication?)";
        if (hash && HashPathOf(requested).TrimEnd('/') != HashPathOf(landed).TrimEnd('/'))
            return $"redirected to #{HashPathOf(landed)} (needs authentication?)";
        return null;
    }

    // The path as written: the browser reports /größen-prüfung percent-encoded, possibly with other hex casing.
    static string PathOf(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath).Normalize(NormalizationForm.FormC);

    // The route inside the fragment, without its query: #/orders?tab=1 → /orders.
    static string HashPathOf(Uri uri)
    {
        var fragment = uri.Fragment.TrimStart('#');
        var query = fragment.IndexOf('?');
        if (query >= 0) fragment = fragment[..query];
        return fragment.Length == 0 ? "/" : Uri.UnescapeDataString(fragment).Normalize(NormalizationForm.FormC);
    }

    // With a session, an API call answered 401/403 means the page rendered without (enough) access, even though the
    // document itself came back 200 (SPA servers answer every path with index.html). Only the path is reported.
    public static string? ApiFailure(IEnumerable<(int Status, string Url)> apiResponses) =>
        apiResponses.Where(r => r.Status is 401 or 403)
            .Select(r => $"API answered {r.Status} for {(Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u.AbsolutePath : "?")} (session not accepted?)")
            .FirstOrDefault();

    // Visiting these would end the saved session for every route after them.
    static readonly Regex SessionEnding = new(@"(^|/)(log-?out|sign-?out|log-?off|abmelden)(/|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Pages that show a password field on purpose, even when signed in.
    static readonly Regex PasswordPage = new(@"(^|/)(log-?in|sign-?in|sign-?up|register|anmelden|registrieren|auth|password|passwort|reset)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool EndsSession(string route) => SessionEnding.IsMatch(route);

    // A password field on a page that has no reason for one: the app fell back to its login form without redirecting.
    public static string? LoginFormFailure(string route, bool signedIn, bool passwordFieldVisible) =>
        signedIn && passwordFieldVisible && !PasswordPage.IsMatch(route) ? "shows a login form (session not accepted?)" : null;

    // Each route starts in a new context. The saved file is read, never written; with a session, the next context starts
    // from the previous one's state instead, because apps that rotate refresh tokens invalidate the file's token on first use.
    public static BrowserNewContextOptions ContextOptions(string? storageStateFile, string? carriedState = null) => new()
    {
        ViewportSize = new() { Width = 1440, Height = 900 },
        StorageStatePath = carriedState is null ? storageStateFile : null,
        StorageState = carriedState
    };

    // Lets the SPA finish auth checks, guards and data loading: network idle (bounded, as polling apps never go idle),
    // then a short pause plus any extra delay the user asked for.
    static async Task Settle(IPage page, int delayMs)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 }); }
        catch (PlaywrightException) { }
        await page.WaitForTimeoutAsync(400 + delayMs);
    }

    // Captures static routes (hash-router routes as /#/route) anonymously or with a provided session, waiting delayMs
    // extra per page; returns only successful images.
    public static async Task<Dictionary<string, string>> Run(CodeModel model, string outDir, string baseUrl,
        string? storageStateFile = null, int delayMs = 0)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var routes = Routes(model);
        var hashRoutes = HashRoutes(model);
        var skipped = model.Nodes.Count(n => n.Kind == "route" && !IsStaticRoute(n.Route));
        if (skipped > 0) Console.Error.WriteLine($"docwizz: screenshots: skipped {skipped} dynamic/unsupported route(s)");
        foreach (var route in routes.Where(EndsSession))
            Console.Error.WriteLine($"docwizz: screenshot skipped: {route} would end the session");
        routes = routes.Where(r => !EndsSession(r)).ToList();
        if (routes.Count == 0) return images;

        var target = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var folder = Path.Combine(outDir, "screenshots");
        Directory.CreateDirectory(folder);

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            string? session = null; // in memory only

            foreach (var route in routes)
            {
                var hash = hashRoutes.Contains(route);
                var url = RouteUrl(target, route, hash);
                var fileName = FileName(route);
                var fullPath = Path.Combine(folder, fileName);
                try
                {
                    // Separate contexts prevent routes from altering one another's cookies and storage.
                    await using var context = await browser.NewContextAsync(ContextOptions(storageStateFile, session));
                    try
                    {
                        var page = await context.NewPageAsync();
                        var apiResponses = new ConcurrentQueue<(int, string)>();
                        page.Response += (_, r) =>
                        {
                            if (r.Request.ResourceType is "fetch" or "xhr") apiResponses.Enqueue((r.Status, r.Url));
                        };
                        var response = await page.GotoAsync(url.ToString(),
                            new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
                        // A SPA guard may redirect after the initial (successful) document load.
                        await Settle(page, delayMs);
                        var failure = NavigationFailure(url, new Uri(page.Url), response?.Status, hash) ??
                            (storageStateFile is null ? null : ApiFailure(apiResponses)) ??
                            LoginFormFailure(route, storageStateFile is not null, await page.Locator("input[type=password]:visible").CountAsync() > 0);
                        if (failure is not null)
                        {
                            if (File.Exists(fullPath)) File.Delete(fullPath);
                            Console.Error.WriteLine($"docwizz: screenshot skipped: {route} {failure}");
                            continue;
                        }
                        await page.ScreenshotAsync(new() { Path = fullPath, FullPage = true, Timeout = 10000 });
                        // Also reject redirects triggered while rendering the screenshot.
                        failure = NavigationFailure(url, new Uri(page.Url), response?.Status, hash);
                        if (failure is not null)
                        {
                            File.Delete(fullPath);
                            Console.Error.WriteLine($"docwizz: screenshot skipped: {route} {failure}");
                            continue;
                        }
                        images[route] = $"screenshots/{fileName}";
                    }
                    finally
                    {
                        // Hand refreshed cookies and tokens to the next route, even when this one was skipped.
                        if (storageStateFile is not null)
                            try { session = await context.StorageStateAsync(); }
                            catch (PlaywrightException) { }
                    }
                }
                catch (Exception e)
                {
                    // Never reuse an outdated screenshot from a previous successful run.
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    Console.Error.WriteLine($"docwizz: screenshot skipped for {route}: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"docwizz: screenshots unavailable: {e.Message}");
        }

        return images;
    }
}
