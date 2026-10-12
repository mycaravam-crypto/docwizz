using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Java / Spring: types, methods and constructors with Javadoc, complexity and signatures; Spring roles from annotations
// (controller, service, repository, entity, configuration); endpoints from @*Mapping with parameter sources, auth and
// the returned type; injection (constructor, @Autowired fields, Lombok's final fields); calls through fields,
// parameters, locals, `this` and type names (statics); `implements` down to methods, so flows dispatch to
// implementations; @Value / @ConfigurationProperties reads.
// The syntax comes from a real parser: scanner-vue/java.mjs runs tree-sitter-java (WebAssembly; nothing is compiled or
// executed) and returns each file's declarations. This class turns them into nodes and edges. What syntax alone can't
// tell stays out: a call on a chain (`a.b().c()`) or an expression has no known receiver type, and overloads resolve by
// name and argument count (else the only overload), not by argument types.
static class JavaScanner
{
    static readonly string[] Mappings = ["GetMapping", "PostMapping", "PutMapping", "DeleteMapping", "PatchMapping", "RequestMapping"];
    static readonly string[] Wrappers = ["ResponseEntity", "Mono", "Flux", "CompletableFuture", "Optional", "HttpEntity"];

    // java.mjs output, one record per kind of declaration.
    record JFile(string Path, string? Package, List<string> Imports, bool Errors, List<JType> Types);
    record JType(string Name, string Kind, int? Parent, List<string> Modifiers, List<JAnnotation> Annotations, string? Javadoc,
        List<string> Extends, List<string> Implements, int Line, int EndLine, string Hash, List<JParam> Components, List<JField> Fields, List<JMethod> Methods);
    record JAnnotation(string Name, string Args);
    record JField(List<string> Names, string Type, List<string> Modifiers, List<JAnnotation> Annotations);
    record JParam(string Name, string Type, List<JAnnotation> Annotations);
    record JMethod(string Name, bool Constructor, bool Compact, List<string> Modifiers, List<JAnnotation> Annotations, string? Javadoc, string? Returns,
        List<JParam> Parameters, List<string> Throws, bool Abstract, int Line, int EndLine, string Hash, int Complexity, List<JCall> Calls, List<JLocal> Locals);
    record JCall(string? Receiver, string Name, int Args, int Line);
    record JLocal(string Name, string Type);
    record Syntax(List<JFile> Files);

    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files)
    {
        var list = files.ToList();
        if (list.Count == 0 || Parse(list) is not { } parsed) return ([], []);
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        if (parsed.Count(f => f.Errors) is var broken and > 0)
            Console.Error.WriteLine($"docwizz: {broken} Java file{(broken == 1 ? " has" : "s have")} syntax errors; what parsed is used, the rest may be missing");

        // Pass 1: every type, so injections and calls can resolve by simple name across files.
        var typesByName = new Dictionary<string, string>();
        var perFile = new List<(string Rel, JFile File, List<string> Ids)>();
        foreach (var f in parsed)
        {
            var ids = new List<string>();
            foreach (var t in f.Types)
            {
                var id = t.Parent is { } p ? $"{ids[p]}.{t.Name}" : $"java:{(f.Package is null ? "" : f.Package + ".")}{t.Name}";
                ids.Add(id);
                typesByName.TryAdd(t.Name, id);
            }
            perFile.Add((Path.GetRelativePath(root, f.Path).Replace('\\', '/'), f, ids));
        }

        var methodsOf = new Dictionary<string, List<(string Id, string Name, int Arity)>>();
        var deferredCalls = new List<(string From, string Type, string Method, int Arity, bool Self)>();
        var interfaces = new List<(string Type, string Interface)>();
        foreach (var (rel, file, ids) in perFile)
        {
            for (var i = 0; i < file.Types.Count; i++)
            {
                var t = file.Types[i];
                var tid = ids[i];
                var annotations = Annotations(t.Annotations);
                var tags = new List<string>();
                if (annotations.ContainsKey("RestController") || annotations.ContainsKey("Controller")) tags.Add("controller");
                if (annotations.ContainsKey("Service")) tags.Add("service");
                if (annotations.ContainsKey("Repository") || t.Extends.Any(e => Regex.IsMatch(e, @"^(Jpa|Crud|PagingAndSorting|Mongo|Reactive\w*)Repository$"))) tags.Add("repository");
                if (annotations.ContainsKey("Entity") || annotations.ContainsKey("Document")) tags.Add("entity");
                if (annotations.ContainsKey("Configuration")) tags.Add("configuration");
                if (annotations.ContainsKey("ConfigurationProperties")) tags.Add("options");
                if (annotations.ContainsKey("SpringBootApplication")) tags.Add("application");
                nodes.Add(new Node(tid, t.Kind is "@interface" ? "interface" : t.Kind, t.Name, rel, t.Line, Visibility(t.Modifiers), Doc(t.Javadoc),
                    Hash: t.Hash, Tags: tags.Count > 0 ? tags : null, EndLine: t.EndLine));
                if (t.Parent is { } parent) edges.Add(new(ids[parent], tid, "contains"));

                // extends / implements between types in the source (generic arguments already dropped).
                foreach (var (name, keyword) in t.Extends.Select(e => (e, "extends")).Concat(t.Implements.Select(e => (e, "implements"))))
                    if (typesByName.TryGetValue(name, out var target))
                    {
                        var isInterface = keyword == "implements" || t.Kind == "interface";
                        edges.Add(new(tid, target, isInterface ? "implements" : "inherits"));
                        if (isInterface) interfaces.Add((tid, target));
                    }
                if (annotations.TryGetValue("ConfigurationProperties", out var prefix) && Arg(prefix, "prefix") is { } p)
                    edges.Add(new(tid, Configuration.Id(p), "binds"));

                var classRoute = annotations.TryGetValue("RequestMapping", out var rm) ? Arg(rm, "value", "path") ?? "" : null;
                var classAuth = Auth(annotations);
                var fields = new Dictionary<string, string>();   // name → type (simple name)
                var injected = new List<string>();
                foreach (var c in t.Components) fields[c.Name] = SimpleType(c.Type);   // a record's components are its fields
                foreach (var f in t.Fields)
                {
                    var type = SimpleType(f.Type);
                    var fieldAnnotations = Annotations(f.Annotations);
                    foreach (var name in f.Names) fields[name] = type;
                    if ((fieldAnnotations.ContainsKey("Autowired") || fieldAnnotations.ContainsKey("Inject")
                            || f.Modifiers.Contains("final") && !f.Modifiers.Contains("static") && (annotations.ContainsKey("RequiredArgsConstructor") || annotations.ContainsKey("AllArgsConstructor")))
                        && typesByName.TryGetValue(type, out var dep))
                        injected.Add(dep);
                    if (fieldAnnotations.TryGetValue("Value", out var v) && Regex.Match(v, @"\$\{([^}:]+)") is { Success: true } key)
                        edges.Add(new(tid, Configuration.Id(key.Groups[1].Value), "reads"));
                }

                foreach (var m in t.Methods)
                {
                    var parameters = (m.Compact ? t.Components : m.Parameters).Where(x => x.Name.Length > 0).Select(Param).ToList();
                    var id = $"{tid}.{m.Name}({string.Join(", ", parameters.Select(x => x.Type))})";
                    var ann = Annotations(m.Annotations);
                    var vis = Visibility(m.Modifiers) is var vv && vv == "internal" && t.Kind == "interface" ? "public" : vv;

                    List<string>? mtags = null;
                    string? route = null;
                    if (Mappings.FirstOrDefault(ann.ContainsKey) is { } mapping && classRoute is not null | tags.Contains("controller"))
                    {
                        var verb = mapping == "RequestMapping"
                            ? Regex.Match(ann[mapping], @"RequestMethod\.(\w+)") is { Success: true } rv ? rv.Groups[1].Value : "GET"
                            : mapping[..^7].ToUpperInvariant();
                        mtags = ["endpoint", verb.ToUpperInvariant()];
                        if ((Auth(ann) ?? classAuth) is { } auth) mtags.Add(auth);
                        route = string.Join('/', new[] { classRoute ?? "", Arg(ann[mapping], "value", "path") ?? "" }.Select(x => x.Trim('/')).Where(x => x.Length > 0));
                    }
                    var returns = m.Constructor || m.Returns is null ? null : Returns(m.Returns);
                    nodes.Add(new Node(id, m.Constructor ? "constructor" : "method", m.Name, rel, m.Line, vis, Doc(m.Javadoc),
                        Complexity: m.Complexity, Params: parameters.Count, Hash: m.Hash, Tags: mtags, Route: route,
                        EndLine: m.EndLine, Parameters: parameters.Count > 0 ? [.. parameters.Select(x => x.Display)] : null,
                        Returns: returns is "void" ? null : returns, Throws: m.Throws.Count > 0 ? m.Throws : null));
                    edges.Add(new(tid, id, "contains"));
                    (methodsOf.TryGetValue(tid, out var own) ? own : methodsOf[tid] = []).Add((id, m.Name, parameters.Count));
                    if (m.Constructor)
                        injected.AddRange(parameters.Select(x => SimpleType(x.Type)).Where(typesByName.ContainsKey).Select(x => typesByName[x]));

                    // Calls whose receiver's type is in the source: a parameter, local or field (`repo.save(x)`,
                    // `this.repo.findAll()`), `this` or no receiver (this type), or a type name (a static call).
                    var locals = m.Locals.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => SimpleType(g.First().Type));
                    foreach (var x in parameters) locals[x.Name] = SimpleType(x.Type);
                    foreach (var call in m.Calls)
                    {
                        var receiver = call.Receiver is { } r && r.StartsWith("this.") ? r[5..] : call.Receiver;
                        if (receiver is null or "this") { deferredCalls.Add((id, tid, call.Name, call.Args, true)); continue; }
                        var type = call.Receiver!.StartsWith("this.") ? fields.GetValueOrDefault(receiver)
                            : locals.GetValueOrDefault(receiver) ?? fields.GetValueOrDefault(receiver) ?? (typesByName.ContainsKey(receiver) ? receiver : null);
                        if (type is not null && typesByName.TryGetValue(type, out var target))
                            deferredCalls.Add((id, target, call.Name, call.Args, false));
                    }
                }
                foreach (var dep in injected.Distinct().Where(d => d != tid)) edges.Add(new(tid, dep, "injects"));
            }
        }

        // Calls resolve once every type's methods are known: same name, same argument count (else the only overload). A
        // call on another type that matches no method still says the code uses that type; one on this type (inherited
        // or a constructor's `this(..)`) is left out.
        foreach (var (from, type, method, arity, self) in deferredCalls)
        {
            var candidates = methodsOf.GetValueOrDefault(type)?.Where(m => m.Name == method).ToList() ?? [];
            var hit = candidates.Where(m => m.Arity == arity).ToList() is [var one] ? one.Id : candidates.Count == 1 ? candidates[0].Id : null;
            if (hit is not null) { if (hit != from) edges.Add(new(from, hit, "calls")); }
            else if (!self) edges.Add(new(from, type, "accesses"));
        }
        // Interface methods → their implementations, by name and arity.
        foreach (var (impl, iface) in interfaces)
            foreach (var im in methodsOf.GetValueOrDefault(iface) ?? [])
                if (methodsOf.GetValueOrDefault(impl)?.FirstOrDefault(m => m.Name == im.Name && m.Arity == im.Arity) is { Id: not null } found)
                    edges.Add(new(found.Id, im.Id, "implements"));
        // External packages each file's top-level types import, as C# `using`s are: own packages and java.* dropped.
        var ownPackages = perFile.Select(f => f.File.Package ?? "").ToHashSet();
        foreach (var (_, file, ids) in perFile)
        {
            var packages = file.Imports.Select(i => string.Join(".", i.Split('.').TakeWhile(s => s.Length > 0 && !char.IsUpper(s[0]))))
                .Where(p => p.Length > 0 && !ownPackages.Contains(p) && p != "java" && !p.StartsWith("java.")).Distinct().ToList();
            foreach (var tid in ids.Where((_, i) => file.Types[i].Parent is null))
                edges.AddRange(packages.Select(p => new Edge(tid, $"ns:{p}", "uses-namespace")));
        }
        return (CodeModel.MergeHashes(nodes).DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
    }

    // Runs scanner-vue/java.mjs over the files. Missing Node or scanner → warn and skip, the rest of the model still works.
    static List<JFile>? Parse(List<string> files)
    {
        var script = Frontend.FindScanner() is { } index ? Path.Combine(Path.GetDirectoryName(index)!, "java.mjs") : null;
        if (script is null || !File.Exists(script) || !Directory.Exists(Path.Combine(Path.GetDirectoryName(script)!, "node_modules", "tree-sitter-java")))
        {
            Console.Error.WriteLine("docwizz: Java skipped — scanner-vue not found or not installed (npm ci in scanner-vue/)");
            return null;
        }
        try
        {
            var psi = new ProcessStartInfo("node", [script]) { RedirectStandardInput = true, RedirectStandardOutput = true };
            var p = Process.Start(psi)!;
            p.StandardInput.Write(string.Join('\n', files.Select(Path.GetFullPath)));
            p.StandardInput.Close();
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"exit code {p.ExitCode}");
            return JsonSerializer.Deserialize<Syntax>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Files;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"docwizz: Java skipped — the Java parser failed: {e.Message}");
            return null;
        }
    }

    // Annotation name → its argument text; the first of a name wins.
    static Dictionary<string, string> Annotations(List<JAnnotation> list)
    {
        var result = new Dictionary<string, string>();
        foreach (var a in list) result.TryAdd(a.Name, a.Args);
        return result;
    }

    // `"/x"`, `value = "/x"`, `path = {"/x"}` → /x; a bare string wins.
    static string? Arg(string args, params string[] names)
    {
        if (Regex.Match(args, @"^\s*\{?\s*""([^""]*)""") is { Success: true } bare) return bare.Groups[1].Value;
        foreach (var n in names)
            if (Regex.Match(args, $@"\b{n}\s*=\s*\{{?\s*""([^""]*)""") is { Success: true } m) return m.Groups[1].Value;
        return null;
    }

    static string? Auth(Dictionary<string, string> a) =>
        a.ContainsKey("PermitAll") ? "anonymous" : a.ContainsKey("PreAuthorize") || a.ContainsKey("Secured") || a.ContainsKey("RolesAllowed") ? "authorize" : null;

    static string Visibility(List<string> modifiers) =>
        modifiers.Contains("public") ? "public" : modifiers.Contains("protected") ? "protected" : modifiers.Contains("private") ? "private" : "internal";

    // `@PathVariable Long id` → ("Long", "id", "[route] id: Long").
    static (string Type, string Name, string Display) Param(JParam p)
    {
        var source = p.Annotations.Select(a => a.Name switch
        {
            "PathVariable" => "route", "RequestParam" => "query", "RequestBody" => "body", "RequestHeader" => "header", "ModelAttribute" => "form", _ => null,
        }).FirstOrDefault(x => x is not null);
        return (p.Type, p.Name, $"{(source is null ? "" : $"[{source}] ")}{p.Name}: {p.Type}");
    }

    // The declared return type with framework wrappers unwrapped: ResponseEntity<List<Item>> → List<Item>.
    static string? Returns(string type)
    {
        var t = type;
        while (Regex.Match(t, @"^(\w+)<(.+)>$") is { Success: true } m && Wrappers.Contains(m.Groups[1].Value)) t = m.Groups[2].Value;
        return t.Length == 0 ? null : t;
    }

    static string SimpleType(string type) => Regex.Replace(type, @"<.*$|\[\]|\.\.\.$", "").Split('.').Last().Trim();

    // Javadoc → the same XML shape C# docs use.
    static string? Doc(string? javadoc)
    {
        if (javadoc is null || Regex.Match(javadoc, @"/\*\*(.*?)\*/", RegexOptions.Singleline) is not { Success: true } m) return null;
        var lines = m.Groups[1].Value.Split('\n').Select(l => Regex.Replace(l, @"^\s*\*\s?", "").Trim()).ToList();
        var summary = string.Join(" ", lines.TakeWhile(l => !l.StartsWith('@'))).Trim();
        var sb = new StringBuilder("<member>");
        if (summary.Length > 0) sb.Append($"<summary>{SecurityElement.Escape(summary)}</summary>");
        foreach (var tag in Regex.Matches(string.Join("\n", lines.SkipWhile(l => !l.StartsWith('@'))), @"@(param|return|throws)\s+(.*?)(?=\n@|\z)", RegexOptions.Singleline).Cast<System.Text.RegularExpressions.Match>())
            sb.Append(tag.Groups[1].Value switch
            {
                "param" when tag.Groups[2].Value.Split(' ', 2) is [var n, var d] => $"<param name=\"{SecurityElement.Escape(n)}\">{SecurityElement.Escape(d.Trim())}</param>",
                "return" => $"<returns>{SecurityElement.Escape(tag.Groups[2].Value.Trim())}</returns>",
                "throws" => $"<exception>{SecurityElement.Escape(tag.Groups[2].Value.Trim())}</exception>",
                _ => "",
            });
        return sb.Append("</member>").ToString();
    }
}
