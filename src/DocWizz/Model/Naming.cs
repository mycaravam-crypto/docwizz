using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// How a symbol, an endpoint and a module are named across pages, reports, agent context and the MCP server.
static class Naming
{
    // A symbol id without its scanner prefix: `cs:Shop.Orders.Place(int)` → `Shop.Orders.Place(int)`.
    public static string Display(Node n) => n.Id[(n.Id.IndexOf(':') + 1)..];
    public static string EndpointLabel(Node n) => $"{n.Tags![1]} /{n.Route?.TrimStart('/')}";
    // A module is a folder; `.` for files at the root.
    public static string Folder(string file) => Path.GetDirectoryName(file)?.Replace('\\', '/') is { Length: > 0 } d ? d : ".";
    // A module's page name under modules/.
    public static string Slug(string folder) => folder == "." ? "root" : Regex.Replace(Ascii(folder), @"[^A-Za-z0-9.\-]+", "-");
    // For file names: German umlauts and ß spelled out (Prüfung → Pruefung), other accents dropped (Café → Cafe),
    // so non-English names keep their letters instead of turning into dashes.
    public static string Ascii(string s)
    {
        var spelled = s.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("Ä", "Ae").Replace("Ö", "Oe")
            .Replace("Ü", "Ue").Replace("ß", "ss").Normalize(NormalizationForm.FormD);
        return string.Concat(spelled.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
    }
    // A heading's anchor as Markdig and GitHub make it: letters (umlauts too), digits, `_` and `-` kept, spaces → `-`.
    public static string Anchor(string heading) => Markdig.Helpers.LinkHelper.UrilizeAsGfm(heading);
}
