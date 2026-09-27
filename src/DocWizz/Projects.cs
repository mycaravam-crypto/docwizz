using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// .csproj and package.json → project nodes (tags: ecosystem, SDK, target frameworks, role), `references` between projects,
// `depends-on` external packages. .sln/.slnx add the solution folder a project sits in (tag `sln:API/Public`).
static class Projects
{
    static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest.TestFramework", "MSTest"];

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

        var solutions = new List<string>();
        foreach (var file in files)
        {
            if (file.EndsWith(".sln") || file.EndsWith(".slnx")) { solutions.Add(file); continue; }
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
                    string Prop(string name) => xml.Descendants().LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
                    var packages = xml.Descendants("PackageReference").Select(p => p.Attribute("Include")?.Value).ToList();
                    // Test first: test projects are often OutputType Exe too. The Web and Worker SDKs default to Exe.
                    var role = Prop("IsTestProject").Equals("true", StringComparison.OrdinalIgnoreCase) || sdk?.StartsWith("MSTest.Sdk") == true
                        || packages.Any(p => TestPackages.Contains(p, StringComparer.OrdinalIgnoreCase)) ? "test"
                        : Prop("OutputType") is "Exe" or "WinExe"
                            || sdk is "Microsoft.NET.Sdk.Web" or "Microsoft.NET.Sdk.Worker" or "Microsoft.NET.Sdk.BlazorWebAssembly" ? "executable"
                        : "library";
                    nodes.Add(new Node(id, "project", Path.GetFileNameWithoutExtension(file), rel, 1,
                        Tags: [.. (string[])(sdk is null ? ["dotnet"] : ["dotnet", sdk]), .. frameworks, role]));
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
        foreach (var sln in solutions)
            foreach (var (path, folder) in SolutionFolders(sln))
            {
                var target = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sln)!, path)));
                var i = nodes.FindIndex(n => n.Id == $"proj:{target.Replace('\\', '/')}");
                if (i >= 0 && folder.Length > 0) nodes[i] = nodes[i] with { Tags = [.. nodes[i].Tags ?? [], $"sln:{folder}"] };
            }
        return (nodes, edges);
    }

    // Project path (relative to the solution) → its solution folder ("API/Public"; "" at the root).
    static IEnumerable<(string Path, string Folder)> SolutionFolders(string file)
    {
        var text = File.ReadAllText(file);
        if (file.EndsWith(".slnx"))
        {
            XDocument xml;
            try { xml = XDocument.Parse(text); } catch (System.Xml.XmlException) { yield break; }
            foreach (var p in xml.Descendants("Project"))
                if (p.Attribute("Path")?.Value is { } path)
                    yield return (path.Replace('\\', '/'), p.Parent?.Name.LocalName == "Folder" ? p.Parent.Attribute("Name")?.Value.Trim('/') ?? "" : "");
            yield break;
        }
        // Project("{type}") = "Name", "path", "{guid}"; solution folders have type 2150E333-…; NestedProjects: {child} = {parent}.
        var entries = Regex.Matches(text, """^Project\("\{([^}]+)\}"\)\s*=\s*"([^"]*)"\s*,\s*"([^"]*)"\s*,\s*"\{([^}]+)\}""", RegexOptions.Multiline)
            .Select(m => (Type: m.Groups[1].Value.ToUpperInvariant(), Name: m.Groups[2].Value, Path: m.Groups[3].Value, Guid: m.Groups[4].Value.ToUpperInvariant()))
            .ToList();
        var nestedSection = Regex.Match(text, @"GlobalSection\(NestedProjects\)(.*?)EndGlobalSection", RegexOptions.Singleline).Groups[1].Value;
        var nested = Regex.Matches(nestedSection, @"\{([^}]+)\}\s*=\s*\{([^}]+)\}")
            .ToDictionary(m => m.Groups[1].Value.ToUpperInvariant(), m => m.Groups[2].Value.ToUpperInvariant());
        var folders = entries.Where(e => e.Type == "2150E333-8FDC-42A3-9474-1A3956D46DE8").ToDictionary(e => e.Guid, e => e.Name);
        string FolderOf(string guid) => nested.TryGetValue(guid, out var p) && folders.TryGetValue(p, out var name)
            ? FolderOf(p) is { Length: > 0 } up ? $"{up}/{name}" : name : "";
        foreach (var e in entries.Where(e => !folders.ContainsKey(e.Guid)))
            yield return (e.Path.Replace('\\', '/'), FolderOf(e.Guid));
    }

    // The project a source file belongs to: the nearest project file in one of its parent folders.
    public static Node? Of(IEnumerable<Node> projects, string file) =>
        projects.Where(p => file.StartsWith(Folder(p.File))).MaxBy(p => Folder(p.File).Length);

    static string Folder(string file) => Path.GetDirectoryName(file) is { Length: > 0 } d ? d.Replace('\\', '/') + "/" : "";
}
