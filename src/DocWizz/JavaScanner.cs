using RxMatch = System.Text.RegularExpressions.Match;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Java / Spring: types, methods and constructors with Javadoc, complexity and signatures; Spring roles from annotations
// (controller, service, repository, entity, configuration); endpoints from @*Mapping with parameter sources, auth and
// the returned type; injection (constructor, @Autowired fields, Lombok's final fields); calls through injected fields;
// `implements` down to methods, so flows dispatch to implementations; @Value / @ConfigurationProperties reads.
// ponytail: a masking tokenizer plus regexes, not a Java parser; overloads resolve by name and argument count, calls on
// locals, chains and statics stay invisible. Swap for a real parser (JavaParser via a sidecar) if that matters.
static partial class JavaScanner
{
    static readonly string[] Mappings = ["GetMapping", "PostMapping", "PutMapping", "DeleteMapping", "PatchMapping", "RequestMapping"];
    static readonly string[] Wrappers = ["ResponseEntity", "Mono", "Flux", "CompletableFuture", "Optional", "HttpEntity"];

    record JType(string Id, string Name, int Start, int BodyStart, int End, string Kind, string? Parent);

    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files)
    {
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var parsed = files.Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'), Text: File.ReadAllText(f))).ToList();

        // Pass 1: every type, so injections and calls can resolve by simple name across files.
        var typesByName = new Dictionary<string, string>();
        var perFile = new List<(string Rel, string Text, string Code, List<JType> Types)>();
        foreach (var (rel, text) in parsed)
        {
            var code = Mask(text);
            var pkg = PackageRe().Match(code) is { Success: true } pm ? pm.Groups[1].Value + "." : "";
            var types = new List<JType>();
            foreach (RxMatch m in TypeRe().Matches(code))
            {
                var open = m.Index + m.Length - 1;
                var end = Close(code, open);
                var parent = types.LastOrDefault(t => t.BodyStart < m.Index && m.Index < t.End);
                var id = parent is null ? $"java:{pkg}{m.Groups[2].Value}" : $"{parent.Id}.{m.Groups[2].Value}";
                types.Add(new(id, m.Groups[2].Value, m.Index, open, end, m.Groups[1].Value, parent?.Id));
                typesByName.TryAdd(m.Groups[2].Value, id);
            }
            perFile.Add((rel, text, code, types));
        }

        var methodsOf = new Dictionary<string, List<(string Id, string Name, int Arity)>>();
        var deferredCalls = new List<(string From, string Type, string Method, int Arity)>();
        var interfaces = new List<(string Type, string Interface)>();
        foreach (var (rel, text, code, types) in perFile)
        {
            int Line(int i) => text.AsSpan(0, i).Count('\n') + 1;
            foreach (var t in types)
            {
                var head = Preamble(text, code, t.Start);
                var annotations = Annotations(head);
                var header = code[t.Start..t.BodyStart];
                var tags = new List<string>();
                if (annotations.ContainsKey("RestController") || annotations.ContainsKey("Controller")) tags.Add("controller");
                if (annotations.ContainsKey("Service")) tags.Add("service");
                if (annotations.ContainsKey("Repository") || Regex.IsMatch(header, @"\bextends\s+(Jpa|Crud|PagingAndSorting|Mongo|Reactive\w*)Repository\b")) tags.Add("repository");
                if (annotations.ContainsKey("Entity") || annotations.ContainsKey("Document")) tags.Add("entity");
                if (annotations.ContainsKey("Configuration")) tags.Add("configuration");
                if (annotations.ContainsKey("ConfigurationProperties")) tags.Add("options");
                if (annotations.ContainsKey("SpringBootApplication")) tags.Add("application");
                nodes.Add(new Node(t.Id, t.Kind is "@interface" ? "interface" : t.Kind, t.Name, rel, Line(t.Start), Visibility(head + header), Doc(head),
                    Hash: Hash(code[t.Start..t.End]), Tags: tags.Count > 0 ? tags : null, EndLine: Line(t.End)));
                if (t.Parent is not null) edges.Add(new(t.Parent, t.Id, "contains"));

                // extends / implements between repository types (generic arguments dropped).
                foreach (RxMatch r in Regex.Matches(Regex.Replace(header, @"<[^<>]*(<[^<>]*>[^<>]*)*>", ""), @"\b(extends|implements)\s+([\w.,\s]+?)(?=\bimplements\b|\bpermits\b|$)"))
                    foreach (var name in r.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(n => n.Split('.').Last()))
                        if (typesByName.TryGetValue(name, out var target))
                        {
                            var isInterface = r.Groups[1].Value == "implements" || t.Kind == "interface";
                            edges.Add(new(t.Id, target, isInterface ? "implements" : "inherits"));
                            if (isInterface) interfaces.Add((t.Id, target));
                        }
                if (annotations.TryGetValue("ConfigurationProperties", out var prefix) && Arg(prefix, "prefix") is { } p)
                    edges.Add(new(t.Id, Configuration.Id(p), "binds"));

                // Members at depth 1 of this type's body.
                var classRoute = annotations.TryGetValue("RequestMapping", out var rm) ? Arg(rm, "value", "path") ?? "" : null;
                var classAuth = Auth(annotations);
                var fields = new Dictionary<string, string>(); // name → type (simple name)
                var injected = new List<string>();
                var body = t.BodyStart + 1;
                var depth = 0;
                var members = new List<(int Start, int End)>();
                var memberStart = body;
                for (var i = body; i < t.End; i++)
                {
                    var c = code[i];
                    if (c == '{') { if (depth == 0) members.Add((memberStart, Close(code, i))); depth++; }
                    else if (c == '}') { depth--; if (depth == 0) memberStart = i + 1; }
                    else if (c == ';' && depth == 0) { members.Add((memberStart, i)); memberStart = i + 1; }
                }
                foreach (var (start, end) in members)
                {
                    var segment = code[start..Math.Min(end + 1, code.Length)];
                    if (types.Any(n => n.Parent == t.Id && n.Start >= start && n.Start < end)) continue; // nested type: handled on its own
                    // Field: `[@Autowired] [private] [final] Type name [= ..];`
                    var bare = AnnotationRe().Replace(segment, "");
                    if (FieldRe().Match(bare) is { Success: true } f && !bare.Contains('('))
                    {
                        var type = SimpleType(f.Groups[2].Value);
                        fields[f.Groups[3].Value] = type;
                        var fieldAnnotations = Annotations(text[start..end]);
                        if ((fieldAnnotations.ContainsKey("Autowired") || fieldAnnotations.ContainsKey("Inject")
                                || f.Groups[1].Value.Contains("final") && (annotations.ContainsKey("RequiredArgsConstructor") || annotations.ContainsKey("AllArgsConstructor")))
                            && typesByName.TryGetValue(type, out var dep))
                            injected.Add(dep);
                        if (fieldAnnotations.TryGetValue("Value", out var v) && Regex.Match(v, @"\$\{([^}:]+)") is { Success: true } key)
                            edges.Add(new(t.Id, Configuration.Id(key.Groups[1].Value), "reads"));
                        continue;
                    }
                    var mm = MethodRe().Match(segment);
                    if (!mm.Success) continue;
                    var name = mm.Groups[3].Value;
                    var isCtor = name == t.Name && string.IsNullOrWhiteSpace(mm.Groups[2].Value);
                    if (!isCtor && mm.Groups[2].Value.Trim() is "" or "new" or "return" or "else" || name is "if" or "for" or "while" or "switch" or "catch" or "synchronized") continue;
                    var parameters = SplitTop(mm.Groups[4].Value).Select(Param).Where(x => x.Name.Length > 0).ToList();
                    var id = $"{t.Id}.{name}({string.Join(", ", parameters.Select(x => x.Type))})";
                    var memberHead = text[start..(start + mm.Index)];
                    var ann = Annotations(memberHead);
                    var bodyIndex = segment.IndexOf('{', mm.Index + mm.Length - 1);
                    var bodyCode = bodyIndex >= 0 ? segment[bodyIndex..] : "";
                    var vis = Visibility(segment[..(mm.Index + mm.Length)]) is var vv && vv == "internal" && t.Kind == "interface" ? "public" : vv;

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
                    var returns = isCtor ? null : Returns(mm.Groups[2].Value);
                    nodes.Add(new Node(id, isCtor ? "constructor" : "method", name, rel, Line(start + mm.Index), vis, Doc(memberHead),
                        Complexity: 1 + DecisionRe().Matches(bodyCode).Count, Params: parameters.Count, Hash: Hash(segment), Tags: mtags, Route: route,
                        EndLine: Line(end), Parameters: parameters.Count > 0 ? [.. parameters.Select(x => x.Display)] : null,
                        Returns: returns is "void" ? null : returns,
                        Throws: mm.Groups[5].Success ? [.. mm.Groups[5].Value.Split(',', StringSplitOptions.TrimEntries).Select(x => x.Split('.').Last())] : null));
                    edges.Add(new(t.Id, id, "contains"));
                    (methodsOf.TryGetValue(t.Id, out var list) ? list : methodsOf[t.Id] = []).Add((id, name, parameters.Count));
                    if (isCtor)
                        injected.AddRange(parameters.Select(x => SimpleType(x.Type)).Where(typesByName.ContainsKey).Select(x => typesByName[x]));

                    // Calls through fields and parameters whose type is in the source: `repo.save(x)`, `this.repo.findAll()`.
                    var locals = parameters.ToDictionary(x => x.Name, x => SimpleType(x.Type));
                    foreach (RxMatch call in CallRe().Matches(bodyCode))
                    {
                        var target = call.Groups[1].Value;
                        var type = locals.GetValueOrDefault(target) ?? fields.GetValueOrDefault(target);
                        if (type is not null && typesByName.TryGetValue(type, out var tid))
                            deferredCalls.Add((id, tid, call.Groups[2].Value, CountArgs(bodyCode, call.Index + call.Length - 1)));
                    }
                }
                foreach (var dep in injected.Distinct().Where(d => d != t.Id)) edges.Add(new(t.Id, dep, "injects"));
                // Field uses in members count as `accesses`, like C# (how a request reaches a repository).
            }
        }

        // Calls resolve once every type's methods are known: same name, same argument count (else the only overload).
        foreach (var (from, type, method, arity) in deferredCalls)
        {
            var candidates = methodsOf.GetValueOrDefault(type)?.Where(m => m.Name == method).ToList() ?? [];
            var hit = candidates.Where(m => m.Arity == arity).ToList() is [var one] ? one.Id : candidates.Count == 1 ? candidates[0].Id : null;
            edges.Add(hit is not null ? new(from, hit, "calls") : new(from, type, "accesses"));
        }
        // Interface methods → their implementations, by name and arity.
        foreach (var (impl, iface) in interfaces)
            foreach (var im in methodsOf.GetValueOrDefault(iface) ?? [])
                if (methodsOf.GetValueOrDefault(impl)?.FirstOrDefault(m => m.Name == im.Name && m.Arity == im.Arity) is { Id: not null } found)
                    edges.Add(new(found.Id, im.Id, "implements"));
        // External packages each file's top-level types import, as C# `using`s are: own packages and java.* dropped.
        var own = perFile.Select(f => PackageRe().Match(f.Code) is { Success: true } m ? m.Groups[1].Value : "").ToHashSet();
        foreach (var (_, _, code, types) in perFile)
        {
            var packages = ImportRe().Matches(code).Select(m => string.Join(".", m.Groups[1].Value.Split('.').TakeWhile(s => !char.IsUpper(s[0]))))
                .Where(p => p.Length > 0 && !own.Contains(p) && p != "java" && !p.StartsWith("java.")).Distinct().ToList();
            foreach (var t in types.Where(t => t.Parent is null))
                edges.AddRange(packages.Select(p => new Edge(t.Id, $"ns:{p}", "uses-namespace")));
        }
        return (CodeModel.MergeHashes(nodes).DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
    }

    // Comments and string/char contents blanked (same length, newlines kept), so braces and keywords can be matched.
    static string Mask(string s)
    {
        var sb = new StringBuilder(s);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') sb[i++] = ' '; }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal); end = end < 0 ? s.Length : end + 2;
                for (; i < end; i++) if (s[i] != '\n') sb[i] = ' ';
                i--;
            }
            else if (s[i] is '"' or '\'')
            {
                var q = s[i];
                if (q == '"' && s.AsSpan(i).StartsWith("\"\"\"")) { var e = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal); e = e < 0 ? s.Length : e + 3; for (var j = i + 3; j < e - 3; j++) if (s[j] != '\n') sb[j] = ' '; i = e - 1; continue; }
                for (i++; i < s.Length && s[i] != q && s[i] != '\n'; i++) { if (s[i] == '\\') sb[i++] = ' '; if (i < s.Length) sb[i] = ' '; }
            }
        }
        return sb.ToString();
    }

    static int Close(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return i;
        return code.Length - 1;
    }

    // The annotations and Javadoc before a declaration: back to the previous statement or block boundary.
    static string Preamble(string text, string code, int start)
    {
        var from = start;
        while (from > 0 && code[from - 1] is not (';' or '{' or '}')) from--;
        return text[from..start];
    }

    static Dictionary<string, string> Annotations(string head)
    {
        var result = new Dictionary<string, string>();
        foreach (RxMatch m in AnnotationRe().Matches(Regex.Replace(head, @"/\*.*?\*/|//[^\n]*", "", RegexOptions.Singleline)))
            result.TryAdd(m.Groups[1].Value.Split('.').Last(), m.Groups[2].Value);
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

    static string Visibility(string s) =>
        Regex.IsMatch(s, @"\bpublic\b") ? "public" : Regex.IsMatch(s, @"\bprotected\b") ? "protected" : Regex.IsMatch(s, @"\bprivate\b") ? "private" : "internal";

    // `@PathVariable Long id` → ("Long", "id", "[route] id: Long").
    static (string Type, string Name, string Display) Param(string p)
    {
        var source = Regex.Match(p, @"@(PathVariable|RequestParam|RequestBody|RequestHeader|ModelAttribute)\b") is { Success: true } a
            ? a.Groups[1].Value switch { "PathVariable" => "route", "RequestParam" => "query", "RequestBody" => "body", "RequestHeader" => "header", _ => "form" } : null;
        var bare = Regex.Replace(p, @"@[\w.]+(\s*\((?:[^()]|\([^()]*\))*\))?", "").Replace("final ", "").Trim();
        var m = Regex.Match(bare, @"^(.+?)\s+(\w+)$");
        if (!m.Success) return ("", "", "");
        var type = Regex.Replace(m.Groups[1].Value, @"\s+", "");
        return (type, m.Groups[2].Value, $"{(source is null ? "" : $"[{source}] ")}{m.Groups[2].Value}: {type}");
    }

    static string? Returns(string type)
    {
        var t = Regex.Replace(type, @"\b(public|protected|private|static|final|abstract|synchronized|default|native)\b|<[^<>]*>\s+(?=\w)", " ").Trim();
        t = Regex.Replace(t, @"\s+", "");
        while (Regex.Match(t, @"^(\w+)<(.+)>$") is { Success: true } m && Wrappers.Contains(m.Groups[1].Value)) t = m.Groups[2].Value;
        return t.Length == 0 ? null : t;
    }

    static string SimpleType(string type) => Regex.Replace(type, @"<.*$|\[\]", "").Split('.').Last().Trim();

    static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '<' or '(' or '{' or '[') depth++;
            else if (s[i] is '>' or ')' or '}' or ']') depth--;
            else if (s[i] == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        if (s[start..].Trim().Length > 0) parts.Add(s[start..]);
        return parts;
    }

    static int CountArgs(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
            if (code[i] == '(') depth++;
            else if (code[i] == ')' && --depth == 0) return SplitTop(code[(open + 1)..i]).Count;
        return 0;
    }

    // The last Javadoc before the declaration → the same XML shape C# docs use.
    static string? Doc(string head)
    {
        var m = Regex.Matches(head, @"/\*\*(.*?)\*/", RegexOptions.Singleline).LastOrDefault();
        if (m is null) return null;
        var lines = m.Groups[1].Value.Split('\n').Select(l => Regex.Replace(l, @"^\s*\*\s?", "").Trim()).ToList();
        var summary = string.Join(" ", lines.TakeWhile(l => !l.StartsWith('@'))).Trim();
        var sb = new StringBuilder("<member>");
        if (summary.Length > 0) sb.Append($"<summary>{SecurityElement.Escape(summary)}</summary>");
        foreach (var tag in Regex.Matches(string.Join("\n", lines.SkipWhile(l => !l.StartsWith('@'))), @"@(param|return|throws)\s+(.*?)(?=\n@|\z)", RegexOptions.Singleline).Cast<RxMatch>())
            sb.Append(tag.Groups[1].Value switch
            {
                "param" when tag.Groups[2].Value.Split(' ', 2) is [var n, var d] => $"<param name=\"{SecurityElement.Escape(n)}\">{SecurityElement.Escape(d.Trim())}</param>",
                "return" => $"<returns>{SecurityElement.Escape(tag.Groups[2].Value.Trim())}</returns>",
                "throws" => $"<exception>{SecurityElement.Escape(tag.Groups[2].Value.Trim())}</exception>",
                _ => "",
            });
        return sb.Append("</member>").ToString();
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(s, @"\s+", " ").Trim())))[..12].ToLowerInvariant();

    [GeneratedRegex(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline)] private static partial Regex PackageRe();
    [GeneratedRegex(@"^\s*import\s+(?:static\s+)?([\w.]+?)(?:\.\*)?\s*;", RegexOptions.Multiline)] private static partial Regex ImportRe();
    [GeneratedRegex(@"\b(class|interface|enum|record|@interface)\s+(\w+)[^;{()]*?(?:\([^)]*\)[^;{]*?)?\{")] private static partial Regex TypeRe();
    [GeneratedRegex(@"^\s*((?:(?:private|protected|public|static|final|transient|volatile)\s+)*)([\w.]+(?:<[^;=()]*>)?(?:\[\])*)\s+(\w+)\s*(?:=[^;]*)?;\s*$", RegexOptions.Singleline)] private static partial Regex FieldRe();
    [GeneratedRegex(@"(?:^|\s)((?:(?:public|protected|private|static|final|abstract|synchronized|default|native)\s+)*(?:<[^>]+>\s+)?)([\w.]+(?:<[^(){};]*>)?(?:\[\])*\s+)?(\w+)\s*\(((?:[^()]|\([^()]*\))*)\)\s*(?:throws\s+([\w.,\s]+?))?\s*[{;]")] private static partial Regex MethodRe();
    [GeneratedRegex(@"@([\w.]+)\s*(?:\(((?:[^()]|\([^()]*\))*)\))?")] private static partial Regex AnnotationRe();
    [GeneratedRegex(@"\b(?:if|for|while|case|catch)\b|&&|\|\||\?(?!\?)")] private static partial Regex DecisionRe();
    [GeneratedRegex(@"(?:\bthis\.)?\b(\w+)\.(\w+)\s*\(")] private static partial Regex CallRe();
}
