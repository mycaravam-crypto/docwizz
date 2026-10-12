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
    public static string Slug(string folder) => folder == "." ? "root" : Regex.Replace(folder, @"[^A-Za-z0-9.\-]+", "-");
}
