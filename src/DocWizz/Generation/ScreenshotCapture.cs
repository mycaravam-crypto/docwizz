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
    public static string? NavigationFailure(Uri requested, Uri landed, int? status)
    {
        if (status is null) return "no HTTP response";
        if (status >= 400) return $"HTTP {status}";
        if (requested.Scheme != landed.Scheme || requested.Authority != landed.Authority)
            return $"redirected to another origin ({PathOf(landed)}) (needs authentication?)";
        if (PathOf(requested).TrimEnd('/') != PathOf(landed).TrimEnd('/'))
            return $"redirected to {PathOf(landed)} (needs authentication?)";
        return null;
    }

    // The path as written: the browser reports /größen-prüfung percent-encoded, possibly with other hex casing.
    static string PathOf(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath).Normalize(NormalizationForm.FormC);

    // Each route starts in a new context; Playwright loads (but does not modify) the saved session.
    public static BrowserNewContextOptions ContextOptions(string? storageStateFile) => new()
    {
        ViewportSize = new() { Width = 1440, Height = 900 },
        StorageStatePath = storageStateFile
    };

    // Captures static routes anonymously or with a provided session, returning only successful images.
    public static async Task<Dictionary<string, string>> Run(CodeModel model, string outDir, string baseUrl,
        string? storageStateFile = null)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var routes = Routes(model);
        var skipped = model.Nodes.Count(n => n.Kind == "route" && !IsStaticRoute(n.Route));
        if (skipped > 0) Console.Error.WriteLine($"docwizz: screenshots: skipped {skipped} dynamic/unsupported route(s)");
        if (routes.Count == 0) return images;

        var target = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var folder = Path.Combine(outDir, "screenshots");
        Directory.CreateDirectory(folder);

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

            foreach (var route in routes)
            {
                var url = new Uri(target, route.TrimStart('/'));
                var fileName = FileName(route);
                var fullPath = Path.Combine(folder, fileName);
                try
                {
                    // Separate contexts prevent routes from altering one another's session.
                    await using var context = await browser.NewContextAsync(ContextOptions(storageStateFile));
                    var page = await context.NewPageAsync();
                    var response = await page.GotoAsync(url.ToString(),
                        new() { WaitUntil = WaitUntilState.Load, Timeout = 10000 });
                    // A SPA guard may redirect after the initial (successful) document load.
                    await page.WaitForTimeoutAsync(400);
                    var failure = NavigationFailure(url, new Uri(page.Url), response?.Status);
                    if (failure is not null)
                    {
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        Console.Error.WriteLine($"docwizz: screenshot skipped: {route} {failure}");
                        continue;
                    }
                    await page.ScreenshotAsync(new() { Path = fullPath, FullPage = true, Timeout = 10000 });
                    // Also reject redirects triggered while rendering the screenshot.
                    failure = NavigationFailure(url, new Uri(page.Url), response?.Status);
                    if (failure is not null)
                    {
                        File.Delete(fullPath);
                        Console.Error.WriteLine($"docwizz: screenshot skipped: {route} {failure}");
                        continue;
                    }
                    images[route] = $"screenshots/{fileName}";
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
