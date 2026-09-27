using System.Text.Json;
using System.Xml.Linq;

// .csproj and package.json → project nodes (tags: ecosystem, SDK, target frameworks), `references` between projects, `depends-on` external packages.
static class Projects
{
    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files)
    {
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        void Package(string project, string ecosystem, string name, string? version, string rel)
        {
            var id = $"pkg:{ecosystem}:{name}";
            if (nodes.All(n => n.Id != id)) nodes.Add(new Node(id, "package", name, rel, 1, Tags: [ecosystem]));
            edges.Add(new(project, id, "depends-on", version));
        }

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var id = $"proj:{rel}";
            try
            {
                if (file.EndsWith(".csproj"))
                {
                    var xml = XDocument.Load(file);
                    var sdk = xml.Root?.Attribute("Sdk")?.Value;
                    var frameworks = xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                        .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    nodes.Add(new Node(id, "project", Path.GetFileNameWithoutExtension(file), rel, 1,
                        Tags: [.. (string[])(sdk is null ? ["dotnet"] : ["dotnet", sdk]), .. frameworks]));
                    foreach (var p in xml.Descendants("PackageReference"))
                        if (p.Attribute("Include")?.Value is { } name) Package(id, "nuget", name, p.Attribute("Version")?.Value, rel);
                    foreach (var p in xml.Descendants("ProjectReference"))
                        if (p.Attribute("Include")?.Value is { } inc)
                        {
                            var target = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, inc.Replace('\\', '/'))));
                            edges.Add(new(id, $"proj:{target.Replace('\\', '/')}", "references"));
                        }
                }
                else
                {
                    var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                    var name = json.TryGetProperty("name", out var n) ? n.GetString() : null;
                    nodes.Add(new Node(id, "project", name ?? Path.GetFileName(Path.GetDirectoryName(file)) ?? rel, rel, 1, Tags: ["npm"]));
                    foreach (var section in new[] { "dependencies", "devDependencies" })
                        if (json.TryGetProperty(section, out var deps))
                            foreach (var d in deps.EnumerateObject()) Package(id, "npm", d.Name, d.Value.GetString(), rel);
                }
            }
            catch (Exception e) when (e is System.Xml.XmlException or JsonException or IOException)
            {
                Console.Error.WriteLine($"docwizz: skipped {rel}: {e.Message}");
            }
        }
        return (nodes, edges);
    }

    // The project a source file belongs to: the nearest project file in one of its parent folders.
    public static Node? Of(IEnumerable<Node> projects, string file) =>
        projects.Where(p => file.StartsWith(Folder(p.File))).MaxBy(p => Folder(p.File).Length);

    static string Folder(string file) => Path.GetDirectoryName(file) is { Length: > 0 } d ? d.Replace('\\', '/') + "/" : "";
}
