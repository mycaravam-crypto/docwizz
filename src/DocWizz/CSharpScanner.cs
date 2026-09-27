using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class CSharpScanner
{
    static readonly SymbolDisplayFormat IdFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;
    static readonly string[] HttpVerbs = ["HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch"];
    static readonly string[] MapVerbs = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods"];

    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files)
    {
        var parse = new CSharpParseOptions(documentationMode: DocumentationMode.Parse);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: f)).ToList();

        // ponytail: BCL refs only; ASP.NET/EF/NuGet types stay unresolved, detected by name. Add package refs if resolution matters.
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        // SDK ImplicitUsings live in obj/, which we don't read; supply the common set.
        var implicitUsings = CSharpSyntaxTree.ParseText(string.Join("\n", new[] {
            "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
            "System.Threading", "System.Threading.Tasks" }.Select(u => $"global using {u};")), parse);
        var comp = CSharpCompilation.Create("scan", trees.Append(implicitUsings), refs);

        var nodes = new List<Node>();
        var edges = new List<Edge>();

        foreach (var tree in trees)
        {
            var sm = comp.GetSemanticModel(tree);
            var rel = Path.GetRelativePath(root, tree.FilePath);
            var syntaxRoot = tree.GetRoot();

            foreach (var decl in syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (sm.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;
                var typeId = Id(type);
                var tags = Roles(decl, type);
                var classAuth = Auth(decl.AttributeLists);

                nodes.Add(new Node(typeId, Kind(type), type.Name, rel, Line(decl),
                    Vis(type), Doc(type), Hash: Hash(decl), Tags: tags,
                    Route: RouteArg(decl.AttributeLists, "Route"), EndLine: EndLine(decl)));

                if (type.BaseType is { } bt && InSource(bt)) edges.Add(new(typeId, Id(bt), "inherits"));
                foreach (var i in type.Interfaces.Where(InSource)) edges.Add(new(typeId, Id(i), "implements"));
                foreach (var im in type.AllInterfaces.Where(InSource).SelectMany(i => i.GetMembers()))
                    if (type.FindImplementationForInterfaceMember(im) is { } impl && InSource(impl))
                        edges.Add(new(Id(impl), Id(im), "implements"));

                // Constructor injection: primary ctor or explicit ctors.
                foreach (var ctor in type.InstanceConstructors)
                    foreach (var p in ctor.Parameters.Where(p => InSource(p.Type) && !SymbolEqualityComparer.Default.Equals(p.Type, type)))
                        edges.Add(new(typeId, Id(p.Type), "injects"));

                if (decl is not TypeDeclarationSyntax td) continue;
                var classRoute = RouteArg(decl.AttributeLists, "Route")
                    ?.Replace("[controller]", type.Name.Replace("Controller", ""), StringComparison.OrdinalIgnoreCase);
                foreach (var member in td.Members)
                    ScanMember(sm, member, typeId, classRoute, classAuth, rel, nodes, edges);
            }

            foreach (var d in syntaxRoot.DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                if (sm.GetDeclaredSymbol(d) is not { DelegateInvokeMethod: { } invoke } del) continue;
                nodes.Add(new Node(Id(del), "delegate", del.Name, rel, Line(d), Vis(del), Doc(del), Params: invoke.Parameters.Length,
                    Hash: Hash(d), EndLine: EndLine(d), Parameters: invoke.Parameters.Select(Param).ToList() is { Count: > 0 } pl ? pl : null,
                    Returns: Returns(invoke.ReturnType)));
                if (del.ContainingType is { } owner) edges.Add(new(Id(owner), Id(del), "contains"));
            }

            ScanRegistrations(sm, syntaxRoot, rel, nodes, edges);
            ScanExternals(sm, syntaxRoot, rel, nodes, edges);
        }

        // Entities are what a DbContext persists.
        var entities = edges.Where(e => e.Kind == "persists").Select(e => e.To).ToHashSet();
        nodes = nodes.Select(n => entities.Contains(n.Id) ? n with { Tags = [.. n.Tags ?? [], "entity"] } : n).ToList();

        // Partial types/methods declare the same symbol more than once; keep the first.
        return (nodes.DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
    }

    // Framework concepts and roles. Base types and interfaces outside the source (ASP.NET, EF, hosting) are unresolved,
    // so they match by name. ponytail: name heuristics for service/repository/options; add attribute/config rules if noisy.
    static List<string>? Roles(BaseTypeDeclarationSyntax decl, INamedTypeSymbol type)
    {
        var bases = new List<string>();
        for (var b = type.BaseType; b is not null && bases.Count < 16; b = b.BaseType) bases.Add(b.Name);
        var interfaces = type.AllInterfaces.Select(i => i.Name).ToList();
        var tags = new List<string>();
        if (HasAttr(decl.AttributeLists, "ApiController") || bases.Contains("ControllerBase") || bases.Contains("Controller")) tags.Add("controller");
        if (bases.Contains("DbContext")) tags.Add("dbcontext");
        if (bases.Contains("BackgroundService") || interfaces.Contains("IHostedService")) tags.Add("background-service");
        if (interfaces.Contains("IMiddleware") || type.GetMembers().OfType<IMethodSymbol>()
                .Any(m => m.Name is "Invoke" or "InvokeAsync" && m.Parameters.FirstOrDefault()?.Type.Name == "HttpContext"))
            tags.Add("middleware");
        if (bases.Contains("Hub")) tags.Add("hub");
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            if (type.Name.EndsWith("Repository") || interfaces.Any(i => i.EndsWith("Repository"))) tags.Add("repository");
            else if (type.Name.EndsWith("Service") && !tags.Contains("background-service")) tags.Add("service");
            if (type.Name.EndsWith("Options") || type.Name.EndsWith("Settings")) tags.Add("options");
        }
        return tags.Count > 0 ? tags : null;
    }

    static void ScanMember(SemanticModel sm, MemberDeclarationSyntax member, string typeId, string? classRoute,
        string? classAuth, string rel, List<Node> nodes, List<Edge> edges)
    {
        ISymbol? sym = member switch
        {
            EventFieldDeclarationSyntax ef => sm.GetDeclaredSymbol(ef.Declaration.Variables[0]),
            BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax => sm.GetDeclaredSymbol(member),
            _ => null
        };
        if (sym is null) return;

        var id = Id(sym);
        var ps = (sym as IMethodSymbol)?.Parameters.Length;
        string? route = null;
        List<string>? tags = null;
        if (member is MethodDeclarationSyntax md && HttpVerbs.FirstOrDefault(v => HasAttr(md.AttributeLists, v)) is { } verb)
        {
            tags = ["endpoint", verb[4..].ToUpperInvariant()];
            if ((Auth(md.AttributeLists) ?? classAuth) is { } auth) tags.Add(auth);
            // Full route: [Route] on the class + the verb's template, unless the template is absolute.
            route = RouteArg(md.AttributeLists, verb) ?? "";
            if (classRoute is not null && !route.StartsWith('/') && !route.StartsWith("~/"))
                route = route.Length == 0 ? classRoute : $"{classRoute}/{route}";
        }
        if (sym is IPropertySymbol { Type: INamedTypeSymbol { Name: "DbSet", TypeArguments: [var entity] } } && InSource(entity))
            edges.Add(new(typeId, Id(entity), "persists"));

        var method = sym as IMethodSymbol;
        nodes.Add(new Node(id, Kind(sym), sym.Name, rel, Line(member), Vis(sym), Doc(sym),
            Complexity(member), ps, Hash(member), tags, route, EndLine(member),
            method?.Parameters.Select(Param).ToList() is { Count: > 0 } pl ? pl : null,
            method is { MethodKind: not MethodKind.Constructor } ? Returns(method.ReturnType) : null, Throws(sm, member)));
        edges.Add(new(typeId, id, "contains"));

        AddCalls(sm, member, id, edges);
    }

    static void AddCalls(SemanticModel sm, SyntaxNode body, string from, List<Edge> edges)
    {
        foreach (var inv in Body(body).OfType<InvocationExpressionSyntax>())
            if (Resolve(sm.GetSymbolInfo(inv)) is IMethodSymbol target && InSource(target))
                edges.Add(new(from, Id(target.ReducedFrom ?? target.OriginalDefinition), "calls"));

        foreach (var oc in Body(body).OfType<BaseObjectCreationExpressionSyntax>())
            if (sm.GetTypeInfo(oc).Type is INamedTypeSymbol created && InSource(created))
                edges.Add(new(from, Id(created.OriginalDefinition), "creates"));

        // C# events: `x.Changed += h` subscribes; any other use (Changed(..), Changed?.Invoke(..)) raises it.
        // `accesses`: the member uses an injected dependency (field, property or primary-constructor parameter) —
        // how a request reaches a DbContext whose own members (DbSet, SaveChanges) are outside the source.
        foreach (var r in Body(body).OfType<IdentifierNameSyntax>())
        {
            var symbol = sm.GetSymbolInfo(r).Symbol;
            var own = r.Parent is not MemberAccessExpressionSyntax ma || ma.Name != r || ma.Expression is ThisExpressionSyntax;
            if (own && (symbol switch
                {
                    IFieldSymbol f => f.Type,
                    IPropertySymbol p => p.Type,
                    IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } } p => p.Type,
                    _ => null,
                }) is INamedTypeSymbol used && InSource(used))
                edges.Add(new(from, Id(used.OriginalDefinition), "accesses"));
            if (symbol is not IEventSymbol ev || !InSource(ev)) continue;
            ExpressionSyntax e = r.Parent is MemberAccessExpressionSyntax m && m.Name == r ? m : r;
            var kind = e.Parent is AssignmentExpressionSyntax a && a.Left == e
                ? a.IsKind(SyntaxKind.AddAssignmentExpression) ? "subscribes" : null
                : "publishes";
            if (kind is not null) edges.Add(new(from, Id(ev), kind));
        }
    }

    // Minimal-API handlers inside Map*() belong to their endpoint node, not the enclosing method.
    static bool IsMapCall(SyntaxNode n) =>
        n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } && MapVerbs.Contains(ma.Name.Identifier.Text);

    static IEnumerable<SyntaxNode> Body(SyntaxNode n) => n.DescendantNodes(d => d == n || !IsMapCall(d));

    // services.AddScoped<I, T>() and app.MapGet("/route", ...), wherever they appear.
    static void ScanRegistrations(SemanticModel sm, SyntaxNode root, string rel, List<Node> nodes, List<Edge> edges)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            var name = ma.Name.Identifier.Text;

            if (name is "AddScoped" or "AddTransient" or "AddSingleton" && ma.Name is GenericNameSyntax { TypeArgumentList.Arguments: [var i, var t] }
                && sm.GetTypeInfo(i).Type is { } it && sm.GetTypeInfo(t).Type is { } tt && InSource(it) && InSource(tt))
                edges.Add(new(Id(it), Id(tt), "registers"));

            if (!IsMapCall(inv) || inv.ArgumentList.Arguments is not [{ Expression: LiteralExpressionSyntax lit }, _, ..]) continue;
            var (prefix, groupAuth) = Group(sm, ma.Expression, 0);
            var route = JoinRoute(prefix, lit.Token.ValueText);
            if (name != "MapMethods")
                ScanMinimalEndpoint(sm, inv, inv.ArgumentList.Arguments[1].Expression, name[3..].ToUpperInvariant(), route, groupAuth, rel, nodes, edges);
            else if (inv.ArgumentList.Arguments.Count > 2)
                // MapMethods("/route", ["PATCH", ...], handler)
                foreach (var verb in inv.ArgumentList.Arguments[1].DescendantNodes().OfType<LiteralExpressionSyntax>())
                    ScanMinimalEndpoint(sm, inv, inv.ArgumentList.Arguments[2].Expression, verb.Token.ValueText.ToUpperInvariant(), route, groupAuth, rel, nodes, edges);
        }
    }

    // External systems the code talks to (see Externals): known calls and creations, HttpClient base addresses.
    static void ScanExternals(SemanticModel sm, SyntaxNode root, string rel, List<Node> nodes, List<Edge> edges)
    {
        void Connect(SyntaxNode at, string key, string name, string category)
        {
            nodes.Add(Externals.Node(key, name, category, "detected", rel, Line(at)));
            if (Owner(sm, at) is { } from) edges.Add(new(from, $"ext:{key}", "connects", "detected"));
        }
        static bool LiteralUri(ExpressionSyntax e, out Uri? uri)
        {
            uri = null;
            return e is ObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax lit }, ..] }
                && Uri.TryCreate(lit.Token.ValueText, UriKind.Absolute, out uri);
        }

        foreach (var n in root.DescendantNodes())
        {
            var (name, qualified) = n switch
            {
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } =>
                    (ma.Name.Identifier.Text, $"{ma.Expression.ToString().Split('.').Last()}.{ma.Name.Identifier.Text}"),
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax id } => (id.Identifier.Text, null),
                ObjectCreationExpressionSyntax oc => (TypeName(oc.Type), null),
                _ => ((string?)null, (string?)null),
            };
            if (name is not null && Externals.ByCall(name, qualified) is { } k) Connect(n, k.Key, k.Name, k.Category);

            // `BaseAddress = new Uri("https://erp.example.com/")`, in an AddHttpClient<T>(..) registration or anywhere.
            if (n is AssignmentExpressionSyntax a && a.Left.ToString().EndsWith("BaseAddress") && LiteralUri(a.Right, out var u))
                Connect(n, $"http:{u!.Host}", u.Host, "http-api");
            // AddHttpClient<T>() without a literal address: an HTTP API whose address lives elsewhere (configuration).
            if (n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax
                    { Identifier.Text: "AddHttpClient", TypeArgumentList.Arguments: [.., var client] } } } add
                && !add.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(x => x.Left.ToString().EndsWith("BaseAddress") && LiteralUri(x.Right, out _)))
                Connect(n, $"http:{TypeName(client)}", $"HTTP API used by {TypeName(client)}", "http-api");
        }
    }

    // Who talks to an external system: T of an enclosing AddDbContext<T>/AddHttpClient<T>(..), else the enclosing type.
    static string? Owner(SemanticModel sm, SyntaxNode at)
    {
        foreach (var a in at.AncestorsAndSelf())
        {
            if (a is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax
                    { Identifier.Text: "AddDbContext" or "AddDbContextPool" or "AddDbContextFactory" or "AddHttpClient", TypeArgumentList.Arguments: [.., var t] } } }
                && sm.GetTypeInfo(t).Type is { } type && InSource(type)) return Id(type);
            if (a is BaseTypeDeclarationSyntax decl && sm.GetDeclaredSymbol(decl) is { } s) return Id(s);
        }
        return null;
    }

    static string TypeName(TypeSyntax t) => t switch
    {
        QualifiedNameSyntax q => TypeName(q.Right),
        SimpleNameSyntax s => s.Identifier.Text,
        _ => t.ToString(),
    };

    // `app.MapGroup("/api").MapGroup("/x")`, possibly via a local (`var g = app.MapGroup(..)`), → "/api/x";
    // Auth is set when any group in the chain calls RequireAuthorization().
    // ponytail: groups passed in as parameters stay unresolved; follow the call site if that matters.
    static (string Prefix, bool Auth) Group(SemanticModel sm, ExpressionSyntax receiver, int depth)
    {
        if (depth > 8) return ("", false);
        if (receiver is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } inv)
        {
            var (prefix, auth) = Group(sm, call.Expression, depth + 1);
            auth |= call.Name.Identifier.Text == "RequireAuthorization";
            return call.Name.Identifier.Text == "MapGroup" && inv.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax l }, ..]
                ? (JoinRoute(prefix, l.Token.ValueText), auth) : (prefix, auth);
        }
        if (sm.GetSymbolInfo(receiver).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var init })
            return Group(sm, init, depth + 1);
        return ("", false);
    }

    static string JoinRoute(string prefix, string route) =>
        prefix.Length == 0 ? route : "/" + string.Join('/', new[] { prefix, route }.Select(p => p.Trim('/')).Where(p => p.Length > 0));

    static void ScanMinimalEndpoint(SemanticModel sm, InvocationExpressionSyntax inv, ExpressionSyntax handler,
        string verb, string route, bool groupAuth, string rel, List<Node> nodes, List<Edge> edges)
    {
        var id = $"cs:endpoint:{verb} {route}";
        string? doc = null;
        IMethodSymbol? signature = null;

        if (handler is AnonymousFunctionExpressionSyntax lambda)
        {
            // Handler parameters are what minimal APIs inject.
            if (sm.GetSymbolInfo(lambda).Symbol is IMethodSymbol lm)
            {
                signature = lm;
                foreach (var p in lm.Parameters.Where(p => InSource(p.Type)))
                    edges.Add(new(id, Id(p.Type), "injects"));
            }
            AddCalls(sm, lambda, id, edges);
        }
        else if (Resolve(sm.GetSymbolInfo(handler)) is IMethodSymbol method && InSource(method))
        {
            signature = method;
            edges.Add(new(id, Id(method), "calls"));
            doc = Doc(method);
        }

        // Docs: .WithSummary/.WithDescription in the fluent chain, else a comment above the statement.
        string? text = null;
        var tags = new List<string> { "endpoint", verb, "minimal-api" };
        if (groupAuth) tags.Add("authorize");
        for (SyntaxNode n = inv; n.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } ma; n = outer)
        {
            if (ma.Name.Identifier.Text is "WithSummary" or "WithDescription"
                && outer.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax l)
                text ??= l.Token.ValueText;
            if (ma.Name.Identifier.Text == "RequireAuthorization") tags.Add("authorize");
            if (ma.Name.Identifier.Text == "AllowAnonymous") tags.Add("anonymous");
        }
        text ??= string.Join(" ", (inv.FirstAncestorOrSelf<StatementSyntax>()?.GetLeadingTrivia() ?? default)
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .Select(t => t.ToString().TrimStart('/', '*', ' ').TrimEnd('*', '/', ' '))).Trim();
        if (text is { Length: > 0 }) doc ??= new XElement("member", new XElement("summary", text)).ToString();

        nodes.Add(new Node(id, "endpoint", $"{verb} {route}", rel, Line(inv), "public", doc,
            Complexity(handler), Hash: Hash(inv), Tags: tags, Route: route, EndLine: EndLine(inv),
            Parameters: signature?.Parameters.Select(Param).ToList() is { Count: > 0 } pl ? pl : null,
            Returns: signature is null ? null : Returns(signature.ReturnType), Throws: Throws(sm, handler)));
    }

    // 1 + decision points.
    static int Complexity(SyntaxNode n) => 1 + Body(n).Count(d => d switch
    {
        IfStatementSyntax or ConditionalExpressionSyntax or WhileStatementSyntax or ForStatementSyntax
            or ForEachStatementSyntax or DoStatementSyntax or CatchClauseSyntax or CaseSwitchLabelSyntax
            or SwitchExpressionArmSyntax or ConditionalAccessExpressionSyntax => true,
        BinaryExpressionSyntax b => b.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
            or SyntaxKind.CoalesceExpression,
        _ => false
    });

    static string? Auth(SyntaxList<AttributeListSyntax> lists) =>
        HasAttr(lists, "AllowAnonymous") ? "anonymous" : HasAttr(lists, "Authorize") ? "authorize" : null;

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    static string Param(IParameterSymbol p)
    {
        var from = (p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ParameterSyntax)?.AttributeLists
            .SelectMany(l => l.Attributes).Select(AttrName).FirstOrDefault(n => n.StartsWith("From") && n.Length > 4);
        return $"{(from is null ? "" : $"[{from[4..].ToLowerInvariant()}] ")}{p.Name}: {p.Type.ToDisplayString(TypeFormat)}";
    }

    // Task<ActionResult<T>> → T. Task / void → null (nothing returned).
    static string? Returns(ITypeSymbol t)
    {
        while (t is INamedTypeSymbol { Name: "Task" or "ValueTask" or "ActionResult", TypeArguments: [var inner] }) t = inner;
        var name = t.ToDisplayString(TypeFormat);
        return t.SpecialType == SpecialType.System_Void || t is INamedTypeSymbol { Name: "Task" or "ValueTask", Arity: 0 } || name.Length == 0
            ? null : name;
    }

    // Exception types thrown directly in the body (`throw new X(..)`, `?? throw new X(..)`).
    static List<string>? Throws(SemanticModel sm, SyntaxNode n)
    {
        var types = Body(n).Select(d => d switch { ThrowStatementSyntax s => s.Expression, ThrowExpressionSyntax e => e.Expression, _ => null })
            .OfType<ExpressionSyntax>().Select(e => sm.GetTypeInfo(e).Type?.Name).OfType<string>().Where(t => t.Length > 0).Distinct().ToList();
        return types.Count > 0 ? types : null;
    }

    static bool HasAttr(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Any(a => AttrName(a) == name);

    static string? RouteArg(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).FirstOrDefault(a => AttrName(a) == name)
            ?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;

    static string AttrName(AttributeSyntax a)
    {
        var n = a.Name.ToString().Split('.').Last();
        return n.EndsWith("Attribute") ? n[..^9] : n;
    }

    // Unresolved external types break overload resolution; a single candidate is still the right target.
    static ISymbol? Resolve(SymbolInfo si) => si.Symbol ?? (si.CandidateSymbols.Length == 1 ? si.CandidateSymbols[0] : null);

    static bool InSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);
    static string Id(ISymbol s) => "cs:" + s.ToDisplayString(IdFormat);
    static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    static int EndLine(SyntaxNode n) => n.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
    static string Vis(ISymbol s) => s.DeclaredAccessibility.ToString().ToLowerInvariant();

    static string? Doc(ISymbol s)
    {
        var xml = s.GetDocumentationCommentXml();
        return string.IsNullOrWhiteSpace(xml) ? null : xml.Trim();
    }

    static string Kind(ISymbol s) => s switch
    {
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        _ => s.Kind.ToString().ToLowerInvariant()
    };

    static string Hash(SyntaxNode n) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n.WithoutTrivia().ToString())))[..12].ToLowerInvariant();
}
