using RxMatch = System.Text.RegularExpressions.Match;
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
                    var xml = XDocument.Load(file, LoadOptions.SetLineInfo);
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
                    // Versions as resolved: the project's own, a VersionOverride, or Directory.Packages.props (central management).
                    foreach (var p in NuGet(root, file, xml)) Package(id, "nuget", p.Package, p.Version, rel);
                    foreach (var p in xml.Descendants("ProjectReference"))
                        if (p.Attribute("Include")?.Value is { } inc)
                        {
                            var target = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, inc.Replace('\\', '/'))));
                            edges.Add(new(id, $"proj:{target.Replace('\\', '/')}", "references"));
                        }
                }
                else if (Path.GetFileName(file) == "pom.xml")
                {
                    // Maven: artifactId, dependencies (test scope left out); spring-boot-maven-plugin builds an executable jar.
                    var xml = XDocument.Load(file);
                    XElement? El(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
                    var executable = xml.Descendants().Any(e => e.Name.LocalName == "artifactId" && e.Value == "spring-boot-maven-plugin");
                    nodes.Add(new Node(id, "project", El(xml.Root, "artifactId")?.Value ?? Path.GetFileName(Path.GetDirectoryName(file)) ?? rel, rel, 1,
                        Tags: ["maven", executable ? "executable" : El(xml.Root, "packaging")?.Value == "war" ? "executable" : "library"]));
                    foreach (var d in El(xml.Root, "dependencies")?.Elements().Where(e => e.Name.LocalName == "dependency") ?? [])
                        if (El(d, "scope")?.Value != "test" && El(d, "groupId")?.Value is { } g && El(d, "artifactId")?.Value is { } a)
                            Package(id, "maven", $"{g}:{a}", El(d, "version")?.Value, rel);
                }
                else if (Path.GetFileName(file) is "build.gradle" or "build.gradle.kts")
                {
                    // Gradle: `implementation 'g:a:v'` / `implementation("g:a")`; the Spring Boot plugin builds an executable jar.
                    var text = File.ReadAllText(file);
                    nodes.Add(new Node(id, "project", Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file))) ?? rel, rel, 1,
                        Tags: ["gradle", text.Contains("org.springframework.boot") ? "executable" : "library"]));
                    foreach (RxMatch m in Regex.Matches(text, @"\b(?:implementation|api|runtimeOnly|compileOnly)\s*\(?\s*[""']([\w.\-]+):([\w.\-]+)(?::([\w.\-]+))?[""']"))
                        Package(id, "maven", $"{m.Groups[1].Value}:{m.Groups[2].Value}", m.Groups[3].Success ? m.Groups[3].Value : null, rel);
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

    // A NuGet PackageReference with the version it resolves to and the line that declares that version: the project's
    // Version, its VersionOverride, or the PackageVersion in the nearest Directory.Packages.props when packages are
    // managed centrally. Version is null when it can't be read as a literal (an MSBuild property, a missing entry), with
    // Problem saying why; floating versions and ranges are kept as written.
    public record NuGetReference(string Project, string Package, string? Version, string DeclaredIn, int Line, bool Central, string? Problem = null);

    // The PackageReferences of one .csproj, resolved as above; `xml` saves a second parse when the caller has one.
    public static List<NuGetReference> NuGet(string root, string file, XDocument? xml = null)
    {
        xml ??= XDocument.Load(file, LoadOptions.SetLineInfo);
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        var project = Rel(file);
        var props = CentralProps(root, file);
        XDocument? central = null;
        try { if (props is not null) central = XDocument.Load(props, LoadOptions.SetLineInfo); }
        catch (System.Xml.XmlException) { }
        static XElement? Switch(XDocument? d) => d?.Descendants().LastOrDefault(e => e.Name.LocalName == "ManagePackageVersionsCentrally");
        // The project's own setting wins over the one in Directory.Packages.props.
        var managed = (Switch(xml) ?? Switch(central))?.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        static string? Value(XElement e, string name) => e.Attribute(name)?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
        static int Line(XElement e) => ((System.Xml.IXmlLineInfo)e).LineNumber;

        var refs = new List<NuGetReference>();
        foreach (var p in xml.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            if (p.Attribute("Include")?.Value is not { } name) continue;
            NuGetReference Ref(string? version, string declaredIn, int line, bool isCentral, string? problem = null) =>
                version is not null && version.Contains("$(") ? new(project, name, null, declaredIn, line, isCentral, $"version comes from an MSBuild property ({version})")
                : new(project, name, version, declaredIn, line, isCentral, version is null ? problem ?? "no version declared" : null);
            if (Value(p, "VersionOverride") is { } over) refs.Add(Ref(over, project, Line(p), isCentral: true));
            else if (!managed) refs.Add(Ref(Value(p, "Version"), project, Line(p), isCentral: false));
            else if (central?.Descendants().LastOrDefault(e => e.Name.LocalName == "PackageVersion"
                && string.Equals(e.Attribute("Include")?.Value, name, StringComparison.OrdinalIgnoreCase)) is { } pv)
                refs.Add(Ref(Value(pv, "Version"), Rel(props!), Line(pv), isCentral: true));
            else refs.Add(Ref(null, project, Line(p), isCentral: true, $"centrally managed, but no PackageVersion in {(props is null ? "a Directory.Packages.props" : Rel(props))}"));
        }
        return refs;
    }

    // The Directory.Packages.props MSBuild imports for a project: the nearest one in its folder or above, within root.
    static string? CentralProps(string root, string file)
    {
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(file)); dir is not null && dir.Length >= top.Length; dir = Path.GetDirectoryName(dir))
            if (Path.Combine(dir, "Directory.Packages.props") is var f && File.Exists(f)) return f;
        return null;
    }

    // The project a source file belongs to: the nearest project file in one of its parent folders.
    public static Node? Of(IEnumerable<Node> projects, string file) =>
        projects.Where(p => file.StartsWith(Folder(p.File))).MaxBy(p => Folder(p.File).Length);

    static string Folder(string file) => Path.GetDirectoryName(file) is { Length: > 0 } d ? d.Replace('\\', '/') + "/" : "";
}
