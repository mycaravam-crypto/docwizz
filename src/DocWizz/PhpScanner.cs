using RxMatch = System.Text.RegularExpressions.Match;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Plain PHP: classes, interfaces, traits, enums and free functions, with PHPDoc, complexity and signatures;
// constructor property promotion (`__construct(private Foo $foo)`) as both a typed property and `injects`; calls
// through `$this->`, `self::`/`static::`/`parent::` and known-typed properties; traits mixed into a class; and
// namespace `use` imports. No framework awareness (routes, DI containers, ORMs) -- that is a deliberate follow-up,
// not this scanner's job.
// ponytail: a masking tokenizer plus regexes, not a PHP parser, same approach as JavaScanner; types/calls resolve
// by simple name globally, not by honouring `use` aliases, so two same-named classes in different namespaces can
// collide. Grouped `use {A, B}` imports and multi-variable `public $a, $b;` declarations are not parsed. Dynamic
// calls (`$this->$method()`, `call_user_func`) stay invisible, same limitation JavaScanner documents for Java.
static partial class PhpScanner
{
    static readonly string[] TypeModifiers = ["abstract", "final", "readonly"];

    record PType(string Id, string Name, int Start, int BodyStart, int End, string Kind);

    /// <summary>
    /// Scans plain PHP files into the shared code model: classes, interfaces, traits, enums, free functions,
    /// their members, calls and namespace imports.
    /// </summary>
    /// <param name="root">Repository root; node file paths are recorded relative to it.</param>
    /// <param name="files">Absolute paths of the `.php` files to scan.</param>
    /// <returns>The nodes and edges found, ready to merge into the rest of the model.</returns>
    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files)
    {
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var parsed = files.Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'), Text: File.ReadAllText(f))).ToList();

        var typesByName = new Dictionary<string, string>();
        var perFile = new List<(string Rel, string Text, string Code, string Namespace, List<PType> Types)>();
        foreach (var (rel, text) in parsed)
        {
            var code = Mask(text);
            var ns = NamespaceRe().Match(code) is { Success: true } nm ? nm.Groups[1].Value + "\\" : "";
            var types = new List<PType>();
            foreach (RxMatch m in TypeRe().Matches(code))
            {
                var open = code.IndexOf('{', m.Index + m.Length - 1);
                if (open < 0) continue; // interface/trait without a body shouldn't happen, but skip rather than crash
                var end = Close(code, open);
                var id = $"php:{ns}{m.Groups[2].Value}";
                types.Add(new(id, m.Groups[2].Value, m.Index, open, end, m.Groups[1].Value));
                typesByName.TryAdd(m.Groups[2].Value, id);
            }
            perFile.Add((rel, text, code, ns, types));
        }

        var methodsOf = new Dictionary<string, List<(string Id, string Name, int Arity)>>();
        var deferredCalls = new List<(string From, string Type, string Method, int Arity)>();
        var traitsOf = new Dictionary<string, List<string>>(); // class id → trait ids it `use`s, for call resolution
        foreach (var (rel, text, code, ns, types) in perFile)
        {
            int Line(int i) => text.AsSpan(0, i).Count('\n') + 1;

            foreach (var t in types)
            {
                var head = Preamble(code, t.Start);
                var header = code[t.Start..t.BodyStart];
                nodes.Add(new Node(t.Id, t.Kind, t.Name, rel, Line(t.Start), "public", Doc(text, head),
                    Hash: Hash(code[t.Start..t.End]), EndLine: Line(t.End)));

                // `extends Foo` (single, classes and interfaces alike) and `implements A, B` (classes only).
                if (ExtendsRe().Match(header) is { Success: true } ext && typesByName.TryGetValue(ext.Groups[1].Value.Split('\\').Last(), out var baseId))
                    edges.Add(new(t.Id, baseId, "inherits"));
                if (ImplementsRe().Match(header) is { Success: true } impl)
                    foreach (var name in impl.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(n => n.Split('\\').Last()))
                        if (typesByName.TryGetValue(name, out var ifaceId))
                            edges.Add(new(t.Id, ifaceId, "implements"));

                var properties = new Dictionary<string, string>(); // name → simple type, known declared/promoted properties
                var injected = new List<string>();
                foreach (var (start, end) in Members(code, t.BodyStart + 1, t.End))
                {
                    var segment = code[start..Math.Min(end + 1, code.Length)];
                    var trimmed = segment.Trim();

                    if (TraitUseRe().Match(trimmed) is { Success: true } tu)
                    {
                        foreach (var traitName in tu.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Select(n => n.Split('\\').Last()))
                            if (typesByName.TryGetValue(traitName, out var traitId))
                            {
                                edges.Add(new(t.Id, traitId, "uses-trait"));
                                (traitsOf.TryGetValue(t.Id, out var traitList) ? traitList : traitsOf[t.Id] = []).Add(traitId);
                            }
                        continue;
                    }

                    if (!trimmed.Contains('(') && PropertyRe().Match(trimmed) is { Success: true } pr)
                    {
                        properties[pr.Groups[3].Value] = SimpleType(pr.Groups[2].Value);
                        continue;
                    }

                    var mm = MethodRe().Match(segment);
                    if (!mm.Success) continue;
                    var name = mm.Groups[2].Value;
                    var isCtor = name == "__construct";
                    var parameters = SplitTop(mm.Groups[3].Value).Select(Param).Where(x => x.Name.Length > 0).ToList();
                    var id = $"{t.Id}.{name}({string.Join(", ", parameters.Select(x => x.Type))})";
                    var memberHead = text[start..(start + mm.Index)];
                    var bodyIndex = segment.IndexOf('{', mm.Index + mm.Length - 1);
                    var bodyCode = bodyIndex >= 0 ? segment[bodyIndex..] : "";

                    // Promoted constructor params (`private Foo $foo`) are both a property and, if their type is
                    // locally known, an injected dependency -- PHP's equivalent of Java's Lombok/final-field DI.
                    if (isCtor)
                        foreach (var p in parameters.Where(x => x.Promoted))
                        {
                            properties[p.Name] = SimpleType(p.Type);
                            if (typesByName.TryGetValue(SimpleType(p.Type), out var dep)) injected.Add(dep);
                        }

                    nodes.Add(new Node(id, isCtor ? "constructor" : "method", name, rel, Line(start + mm.Index),
                        Visibility(mm.Groups[1].Value), Doc(text, memberHead),
                        Complexity: 1 + DecisionRe().Matches(bodyCode).Count, Params: parameters.Count, Hash: Hash(segment),
                        EndLine: Line(end), Parameters: parameters.Count > 0 ? [.. parameters.Select(x => x.Display)] : null,
                        Returns: mm.Groups[4].Success ? Returns(mm.Groups[4].Value) : null));
                    edges.Add(new(t.Id, id, "contains"));
                    (methodsOf.TryGetValue(t.Id, out var list) ? list : methodsOf[t.Id] = []).Add((id, name, parameters.Count));

                    // `$this->x()` / `$field->x()`: target's type from params or known properties.
                    var locals = parameters.ToDictionary(x => x.Name, x => SimpleType(x.Type));
                    foreach (RxMatch call in InstanceCallRe().Matches(bodyCode))
                    {
                        var target = call.Groups[1].Value;
                        var type = target == "this" ? t.Name : locals.GetValueOrDefault(target) ?? properties.GetValueOrDefault(target);
                        if (type is not null && typesByName.TryGetValue(type, out var tid))
                            deferredCalls.Add((id, tid, call.Groups[2].Value, CountArgs(bodyCode, call.Index + call.Length - 1)));
                    }
                    // `$this->field->x()`: the dominant DI idiom (constructor-promoted or declared property).
                    foreach (RxMatch call in ThisPropertyCallRe().Matches(bodyCode))
                    {
                        var type = properties.GetValueOrDefault(call.Groups[1].Value);
                        if (type is not null && typesByName.TryGetValue(type, out var tid))
                            deferredCalls.Add((id, tid, call.Groups[2].Value, CountArgs(bodyCode, call.Index + call.Length - 1)));
                    }
                    // `self::x()` / `static::x()` / `parent::x()` / `ClassName::x()`.
                    foreach (RxMatch call in StaticCallRe().Matches(bodyCode))
                    {
                        var target = call.Groups[1].Value;
                        var type = target is "self" or "static" ? t.Name
                            : target == "parent" ? (ExtendsRe().Match(header) is { Success: true } pe ? pe.Groups[1].Value.Split('\\').Last() : null)
                            : target;
                        if (type is not null && typesByName.TryGetValue(type, out var tid))
                            deferredCalls.Add((id, tid, call.Groups[2].Value, CountArgs(bodyCode, call.Index + call.Length - 1)));
                    }
                }
                foreach (var dep in injected.Distinct().Where(d => d != t.Id)) edges.Add(new(t.Id, dep, "injects"));
            }

            // Free functions: `function name(...)` that isn't inside any type's body.
            foreach (RxMatch fm in FunctionRe().Matches(code))
            {
                if (types.Any(t => fm.Index > t.BodyStart && fm.Index < t.End)) continue;
                var name = fm.Groups[2].Value;
                var parameters = SplitTop(fm.Groups[3].Value).Select(Param).Where(x => x.Name.Length > 0).ToList();
                var id = $"php:{ns}{name}({string.Join(", ", parameters.Select(x => x.Type))})";
                var open = code.IndexOf('{', fm.Index + fm.Length - 1);
                var end = open >= 0 ? Close(code, open) : fm.Index + fm.Length;
                var head = Preamble(code, fm.Index);
                nodes.Add(new Node(id, "function", name, rel, Line(fm.Index), "public", Doc(text, head),
                    Complexity: 1 + DecisionRe().Matches(code[(open >= 0 ? open : fm.Index)..end]).Count,
                    Params: parameters.Count, Hash: Hash(code[fm.Index..end]), EndLine: Line(end),
                    Parameters: parameters.Count > 0 ? [.. parameters.Select(x => x.Display)] : null,
                    Returns: fm.Groups[4].Success ? Returns(fm.Groups[4].Value) : null));
            }
        }

        foreach (var (from, type, method, arity) in deferredCalls)
        {
            // A trait's methods are mixed into whatever class `use`s it, so a call the class doesn't declare
            // itself may still resolve through one of its traits.
            var own = methodsOf.GetValueOrDefault(type) ?? [];
            var fromTraits = traitsOf.GetValueOrDefault(type)?.SelectMany(tr => methodsOf.GetValueOrDefault(tr) ?? []) ?? [];
            var candidates = own.Concat(fromTraits).Where(m => m.Name == method).ToList();
            var hit = candidates.Where(m => m.Arity == arity).ToList() is [var one] ? one.Id : candidates.Count == 1 ? candidates[0].Id : null;
            edges.Add(hit is not null ? new(from, hit, "calls") : new(from, type, "accesses"));
        }

        // `use Foo\Bar\Baz;` outside any type's body: a namespace import, resolved to a local type by simple name
        // when there is one, otherwise recorded as an external namespace reference (as JavaScanner does for Java).
        foreach (var (_, _, code, ns, types) in perFile)
        {
            var fileId = types.FirstOrDefault()?.Id; // attribute file-level imports to the file's first type, if any
            if (fileId is null) continue;
            foreach (RxMatch m in UseRe().Matches(code))
            {
                if (types.Any(t => m.Index > t.Start && m.Index < t.End)) continue; // trait use inside a class body
                var fqn = m.Groups[1].Value;
                var simple = fqn.Split('\\').Last();
                edges.Add(typesByName.TryGetValue(simple, out var target) && target != fileId
                    ? new(fileId, target, "imports")
                    : new(fileId, $"ns:{fqn.Replace('\\', '.')}", "uses-namespace"));
            }
        }

        return (CodeModel.MergeHashes(nodes).DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
    }

    // Comments (`//`, `#`, `/* */`) and string/heredoc contents blanked (same length, newlines kept).
    static string Mask(string s)
    {
        var sb = new StringBuilder(s);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/' || s[i] == '#' && !(i + 1 < s.Length && s[i + 1] == '['))
            { while (i < s.Length && s[i] != '\n') sb[i++] = ' '; }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal); end = end < 0 ? s.Length : end + 2;
                for (; i < end; i++) if (s[i] != '\n') sb[i] = ' ';
                i--;
            }
            else if (s[i] is '"' or '\'')
            {
                var q = s[i];
                for (i++; i < s.Length && s[i] != q && s[i] != '\n'; i++) { if (s[i] == '\\') sb[i++] = ' '; if (i < s.Length) sb[i] = ' '; }
            }
            else if (s[i] == '<' && s.AsSpan(i).StartsWith("<<<"))
            {
                var hm = Regex.Match(s[i..], @"^<<<\s*'?(\w+)'?\r?\n");
                if (hm.Success)
                {
                    var marker = hm.Groups[1].Value;
                    var bodyStart = i + hm.Length;
                    var close = Regex.Match(s[bodyStart..], $@"^[ \t]*{marker}\b", RegexOptions.Multiline);
                    var e = close.Success ? bodyStart + close.Index : s.Length;
                    for (var j = bodyStart; j < e; j++) if (s[j] != '\n') sb[j] = ' ';
                    i = e - 1;
                }
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

    // Depth-0 statements/blocks directly inside a type's body: `name;`-terminated or `{...}`-delimited, same
    // splitting JavaScanner uses for Java members.
    static IEnumerable<(int Start, int End)> Members(string code, int bodyStart, int bodyEnd)
    {
        var members = new List<(int, int)>();
        var depth = 0; var memberStart = bodyStart;
        for (var i = bodyStart; i < bodyEnd; i++)
        {
            var c = code[i];
            if (c == '{') { if (depth == 0) members.Add((memberStart, Close(code, i))); depth++; }
            else if (c == '}') { depth--; if (depth == 0) memberStart = i + 1; }
            else if (c == ';' && depth == 0) { members.Add((memberStart, i)); memberStart = i + 1; }
        }
        return members;
    }

    // Attributes (`#[...]`) and PHPDoc back to the previous statement/block boundary.
    static string Preamble(string code, int start)
    {
        var from = start;
        while (from > 0 && code[from - 1] is not (';' or '{' or '}')) from--;
        return code[from..start];
    }

    static string Visibility(string modifiers) =>
        modifiers.Contains("private") ? "private" : modifiers.Contains("protected") ? "protected" : "public";

    // `[?]Type $name[,] [= default]` or `[private] [readonly] [?]Type &$name` (promoted ctor param).
    static (string Type, string Name, string Display, bool Promoted) Param(string p)
    {
        var promoted = Regex.IsMatch(p, @"\b(public|protected|private)\b");
        var bare = Regex.Replace(p, @"\b(public|protected|private|readonly)\b", "").Trim();
        var m = Regex.Match(bare, @"^(?:(\??[\w\\|]+)\s+)?&?\.{0,3}\$(\w+)(?:\s*=.*)?$");
        if (!m.Success) return ("", "", "", false);
        var type = m.Groups[1].Success ? m.Groups[1].Value.TrimStart('?') : "mixed";
        return (type, m.Groups[2].Value, $"${m.Groups[2].Value}: {type}", promoted);
    }

    static string? Returns(string type)
    {
        var t = type.Trim().TrimStart('?');
        return t is "" or "void" ? null : t;
    }

    static string SimpleType(string type) => type.Split('|').First().Split('\\').Last().Trim();

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

    // PHPDoc → the same XML shape every other scanner's docs use. `@param Type $name desc` (PHP orders type before
    // the `$name`, unlike Javadoc which has no type to repeat since the signature already carries it).
    static string? Doc(string text, string head)
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
                "param" when Regex.Match(tag.Groups[2].Value, @"^\S+\s+\$(\w+)\s*(.*)$", RegexOptions.Singleline) is { Success: true } pm =>
                    $"<param name=\"{SecurityElement.Escape(pm.Groups[1].Value)}\">{SecurityElement.Escape(pm.Groups[2].Value.Trim())}</param>",
                "return" => $"<returns>{SecurityElement.Escape(Regex.Replace(tag.Groups[2].Value.Trim(), @"^\S+\s*", ""))}</returns>",
                "throws" => $"<exception>{SecurityElement.Escape(Regex.Replace(tag.Groups[2].Value.Trim(), @"^\S+\s*", ""))}</exception>",
                _ => "",
            });
        return sb.Append("</member>").ToString();
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(s, @"\s+", " ").Trim())))[..12].ToLowerInvariant();

    [GeneratedRegex(@"^\s*namespace\s+([\w\\]+)\s*;", RegexOptions.Multiline)] private static partial Regex NamespaceRe();
    [GeneratedRegex(@"^\s*use\s+([\w\\]+)(?:\s+as\s+\w+)?\s*;", RegexOptions.Multiline)] private static partial Regex UseRe();
    [GeneratedRegex(@"\b(?:abstract\s+|final\s+|readonly\s+)*\b(class|interface|trait|enum)\s+(\w+)")] private static partial Regex TypeRe();
    [GeneratedRegex(@"\bextends\s+([\w\\]+)")] private static partial Regex ExtendsRe();
    [GeneratedRegex(@"\bimplements\s+([\w\\,\s]+?)\s*$")] private static partial Regex ImplementsRe();
    [GeneratedRegex(@"^use\s+([\w\\,\s]+);$")] private static partial Regex TraitUseRe();
    [GeneratedRegex(@"^((?:(?:public|protected|private|static|readonly)\s+)*)(\??[\w\\|]+)\s+\$(\w+)\s*(?:=.*)?;$")] private static partial Regex PropertyRe();
    [GeneratedRegex(@"((?:(?:public|protected|private|abstract|final|static)\s+)*)function\s*&?\s*(\w+)\s*\(((?:[^()]|\([^()]*\))*)\)\s*(?::\s*([\w\\|?]+))?\s*[{;]")] private static partial Regex MethodRe();
    [GeneratedRegex(@"(?:^|[^\w])((?:(?:abstract|final)\s+)*)function\s*&?\s*(\w+)\s*\(((?:[^()]|\([^()]*\))*)\)\s*(?::\s*([\w\\|?]+))?\s*\{")] private static partial Regex FunctionRe();
    [GeneratedRegex(@"\b(?:if|elseif|for|foreach|while|case|catch)\b|&&|\|\||\?(?!\?)")] private static partial Regex DecisionRe();
    [GeneratedRegex(@"\$(\w+)->(\w+)\s*\(")] private static partial Regex InstanceCallRe();
    [GeneratedRegex(@"\$this->(\w+)->(\w+)\s*\(")] private static partial Regex ThisPropertyCallRe();
    [GeneratedRegex(@"\b(self|static|parent|[A-Z]\w*)::(\w+)\s*\(")] private static partial Regex StaticCallRe();
}
