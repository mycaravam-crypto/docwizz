using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// Optional runtime capture: reuse the routes detected by the existing frontend scanner.
// No navigation crawler, credentials, test data or AI is involved.
static class ScreenshotCapture
{
    static readonly Regex StaticPath = new(@"^/(?:[a-zA-Z0-9._~-]+/?)*$", RegexOptions.CultureInvariant);

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
        var slug = Regex.Replace(route.Trim('/').ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        if (slug.Length == 0) slug = "home";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route)))[..10].ToLowerInvariant();
        return $"{slug}-{hash}.png";
    }

    public static async Task<Dictionary<string, string>> Run(CodeModel model, string outDir, string baseUrl)
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
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 900 }
            });

            foreach (var route in routes)
            {
                var url = new Uri(target, route.TrimStart('/'));
                var fileName = FileName(route);
                var fullPath = Path.Combine(folder, fileName);
                var page = await context.NewPageAsync();
                try
                {
                    var response = await page.GotoAsync(url.ToString(),
                        new() { WaitUntil = WaitUntilState.Load, Timeout = 10000 });
                    // Reject HTTP errors and redirects to another page (e.g. login).
                    var landed = new Uri(page.Url);
                    if (response is null || response.Status >= 400 ||
                        landed.Authority != url.Authority || landed.Scheme != url.Scheme ||
                        landed.AbsolutePath.TrimEnd('/') != url.AbsolutePath.TrimEnd('/'))
                    {
                        Console.Error.WriteLine($"docwizz: screenshot skipped for {route}: HTTP error or redirect");
                        continue;
                    }
                    await page.WaitForTimeoutAsync(400);
                    await page.ScreenshotAsync(new() { Path = fullPath, FullPage = true, Timeout = 10000 });
                    images[route] = $"screenshots/{fileName}";
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"docwizz: screenshot skipped for {route}: {e.Message}");
                }
                finally
                {
                    await page.CloseAsync();
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
