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

    // ponytail: set per Scan call, read by Doc(); thread it through the scan methods if scans ever run in parallel.
    static bool commentDocs;

    public static (List<Node>, List<Edge>) Scan(string root, IEnumerable<string> files, bool commentDocs = false)
    {
        CSharpScanner.commentDocs = commentDocs;
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

            // External namespaces the file's types use (own and System.* namespaces are dropped below).
            var usings = syntaxRoot.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Alias is null && u.StaticKeyword == default)
                .Select(u => u.NamespaceOrType.ToString()).Distinct().ToList();
            foreach (var decl in syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(d => d.Parent is not BaseTypeDeclarationSyntax))
                if (sm.GetDeclaredSymbol(decl) is { } t)
                    edges.AddRange(usings.Select(u => new Edge(Id(t), $"ns:{u}", "uses-namespace")));
            ScanRegistrations(sm, syntaxRoot, rel, nodes, edges);
            ScanSqlReferences(sm, syntaxRoot, rel, edges);
            ScanPipeline(sm, syntaxRoot, rel, edges);
            ScanExternals(sm, syntaxRoot, rel, nodes, edges);
            ScanConfigurationReads(sm, syntaxRoot, rel, nodes, edges);
        }

        // Entities are what a DbContext persists.
        var entities = edges.Where(e => e.Kind == "persists").Select(e => e.To).ToHashSet();
        nodes = nodes.Select(n => entities.Contains(n.Id) ? n with { Tags = [.. n.Tags ?? [], "entity"] } : n).ToList();
        // Hosted services are what AddHostedService<T>() registers.
        var hosted = edges.Where(e => e.Kind == "hosts").Select(e => e.To).ToHashSet();
        nodes = nodes.Select(n => hosted.Contains(n.Id) ? n with { Tags = [.. n.Tags ?? [], "hosted"] } : n).ToList();

        var own = comp.GlobalNamespace.GetNamespaceMembers().SelectMany(AllNamespaces).Where(n => n.Locations.Any(l => l.IsInSource))
            .Select(n => n.ToDisplayString()).ToHashSet();
        edges.RemoveAll(e => e.Kind == "uses-namespace" && (own.Contains(e.To[3..]) || e.To[3..] is "System" || e.To.StartsWith("ns:System.")));

        // Partial types/methods declare the same symbol more than once; keep the first.
        return (CodeModel.MergeHashes(nodes).DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
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
        if (bases.Contains("Migration") && HasAttr(decl.AttributeLists, "Migration")) tags.Add("migration"); // EF Core migrations
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
            method is { MethodKind: not MethodKind.Constructor } ? Returns(method.ReturnType) : null, Throws(sm, member),
            Responses: tags is not null && member is MethodDeclarationSyntax m ? Responses(sm, m.AttributeLists, m, m.ReturnType, []) : null));
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
            if (name == "AddHostedService" && ma.Name is GenericNameSyntax { TypeArgumentList.Arguments: [var h] }
                && sm.GetTypeInfo(h).Type is { } ht && InSource(ht))
                edges.Add(new(Owner(sm, inv) ?? $"file:{rel}", Id(ht), "hosts"));

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

    // Stored procedures called from code: a literal "EXEC dbo.X ..." anywhere (FromSqlRaw, ExecuteSqlRaw, SqlCommand), or a
    // literal name passed or assigned next to CommandType.StoredProcedure (Dapper `commandType:`, SqlCommand.CommandText).
    // `sqlref:<name>` edges; Sql.Link resolves them to procedure nodes and drops the rest.
    static void ScanSqlReferences(SemanticModel sm, SyntaxNode root, string rel, List<Edge> edges)
    {
        foreach (var lit in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            var text = lit.Token.ValueText;
            var name = System.Text.RegularExpressions.Regex.Match(text, @"^\s*EXEC(?:UTE)?\s+([\w.\[\]]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
                ? m.Groups[1].Value
                : System.Text.RegularExpressions.Regex.IsMatch(text, @"^[\w.\[\]]+$")
                    && (lit.Parent is ArgumentSyntax { Parent: ArgumentListSyntax args } && args.ToString().Contains("StoredProcedure")
                        || lit.Parent is AssignmentExpressionSyntax a && a.Left.ToString().EndsWith("CommandText")
                            && lit.FirstAncestorOrSelf<MemberDeclarationSyntax>()?.ToString().Contains("CommandType.StoredProcedure") == true)
                    ? text : null;
            if (name is not null) edges.Add(new(Owner(sm, lit) ?? $"file:{rel}", $"sqlref:{Sql.Name(name)}", "calls"));
        }
    }

    // The request pipeline: app.UseX(..) / app.UseMiddleware<T>() on the built app or an IApplicationBuilder, in source
    // order. `pipeline` edges from the configuring code (top level: its project) to the middleware class, or to
    // `pipeline:UseX` for framework middleware; the label is where it is registered.
    static void ScanPipeline(SemanticModel sm, SyntaxNode root, string rel, List<Edge> edges)
    {
        bool IsApp(ExpressionSyntax e)
        {
            while (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m }) e = m.Expression;
            return sm.GetSymbolInfo(e).Symbol switch
            {
                ILocalSymbol l => l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax
                    { Initializer.Value: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Build" } } },
                IParameterSymbol p => p.Type.Name is "IApplicationBuilder" or "WebApplication",
                _ => false,
            };
        }
        var uses = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => (Call: i, Member: i.Expression as MemberAccessExpressionSyntax))
            .Where(x => x.Member is { } m && m.Name.Identifier.Text.StartsWith("Use") && IsApp(m.Expression))
            .OrderBy(x => x.Member!.Name.SpanStart); // `app.UseA().UseB()`: A first
        foreach (var (call, m) in uses)
        {
            var to = m!.Name is GenericNameSyntax { Identifier.Text: "UseMiddleware", TypeArgumentList.Arguments: [var t] }
                && sm.GetTypeInfo(t).Type is { } type && InSource(type) ? Id(type) : $"pipeline:{m.Name}";
            edges.Add(new(Owner(sm, call) ?? $"file:{rel}", to, "pipeline", $"{rel}:{Line(call)}"));
        }
    }

    static readonly Dictionary<string, int> ResultCodes = new()
    {
        ["Ok"] = 200, ["Created"] = 201, ["CreatedAtAction"] = 201, ["CreatedAtRoute"] = 201, ["Accepted"] = 202,
        ["AcceptedAtAction"] = 202, ["AcceptedAtRoute"] = 202, ["NoContent"] = 204, ["BadRequest"] = 400,
        ["ValidationProblem"] = 400, ["Unauthorized"] = 401, ["Forbid"] = 403, ["NotFound"] = 404, ["Conflict"] = 409,
        ["UnprocessableEntity"] = 422, ["Problem"] = 500,
    };

    // Response status codes (with a type when stated): [ProducesResponseType], .Produces<T>(..)/.ProducesProblem(..) in the
    // fluent chain, a declared Results<Ok<T>, NotFound> return type, and Ok(x)/NotFound()/Results.X/TypedResults.X in the body.
    static List<string>? Responses(SemanticModel sm, SyntaxList<AttributeListSyntax> attributes, SyntaxNode? body,
        TypeSyntax? declared, IEnumerable<InvocationExpressionSyntax> chain)
    {
        var found = new SortedDictionary<int, string?>();
        void Add(int code, string? type) { if (type is not null || !found.ContainsKey(code)) found[code] = type ?? found.GetValueOrDefault(code); }
        static int? Code(ExpressionSyntax? e) => e is null ? null
            : e is LiteralExpressionSyntax { Token.Value: int i } ? i
            : System.Text.RegularExpressions.Regex.Match(e.ToString(), @"Status(\d{3})") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
        string Name(TypeSyntax t) => sm.GetTypeInfo(t).Type is { TypeKind: not TypeKind.Error } s ? s.ToDisplayString(TypeFormat) : t.ToString();

        foreach (var a in attributes.SelectMany(l => l.Attributes).Where(a => AttrName(a) == "ProducesResponseType"))
        {
            var args = a.ArgumentList?.Arguments.Select(x => x.Expression).ToList() ?? [];
            var type = a.Name is GenericNameSyntax { TypeArgumentList.Arguments: [var g] } ? Name(g)
                : args.OfType<TypeOfExpressionSyntax>().FirstOrDefault() is { } to ? Name(to.Type) : null;
            if (args.Select(Code).FirstOrDefault(c => c is not null) is { } code) Add(code, type);
        }
        foreach (var c in chain)
            if (c.Expression is MemberAccessExpressionSyntax { Name: var n })
            {
                var arg = c.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                switch (n.Identifier.Text)
                {
                    case "Produces": Add(Code(arg) ?? 200, n is GenericNameSyntax { TypeArgumentList.Arguments: [var g] } ? Name(g) : null); break;
                    case "ProducesProblem": Add(Code(arg) ?? 500, null); break;
                    case "ProducesValidationProblem": Add(Code(arg) ?? 400, null); break;
                }
            }
        foreach (var g in declared?.DescendantNodesAndSelf().OfType<SimpleNameSyntax>() ?? [])
            if (ResultCodes.TryGetValue(g.Identifier.Text, out var code) && g.Parent is TypeArgumentListSyntax or TypeSyntax)
                Add(code, g is GenericNameSyntax { TypeArgumentList.Arguments: [var t] } ? Name(t) : null);
        foreach (var inv in body is null ? [] : Body(body).OfType<InvocationExpressionSyntax>())
        {
            var name = inv.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,  // controller helpers: Ok(x), NotFound()
                MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Results" or "TypedResults" }, Name: var n } => n.Identifier.Text,
                _ => null,
            };
            if (name is null || !ResultCodes.TryGetValue(name, out var code)) continue;
            // The value is Ok(x)'s argument, or the last argument of Created*/Accepted*(location.., x).
            var value = (name == "Ok" || (name.StartsWith("Created") || name.StartsWith("Accepted")) && inv.ArgumentList.Arguments.Count > 1)
                && inv.ArgumentList.Arguments.LastOrDefault()?.Expression is { } v ? sm.GetTypeInfo(v).Type : null;
            Add(code, value is null or { TypeKind: TypeKind.Error } ? null : value.ToDisplayString(TypeFormat));
        }
        return found.Count > 0 ? found.Select(kv => kv.Value is null ? $"{kv.Key}" : $"{kv.Key} {kv.Value}").ToList() : null;
    }

    // Minimal-API parameters without a [From*] attribute, bound as ASP.NET infers it: a name in the route template → route,
    // HttpContext/CancellationToken/… → special, IFormFile → form, services → service, simple types → query, else body.
    // ponytail: services are recognised by shape (interface, DbContext, *Service/*Repository/*Client); DI registrations would be exact.
    static string MinimalParam(IParameterSymbol p, string route)
    {
        var declared = Param(p);
        if (declared.StartsWith('[')) return declared;
        var t = p.Type is INamedTypeSymbol { Name: "Nullable", TypeArguments: [var inner] } ? inner : p.Type;
        var bases = new List<string>();
        for (var b = t.BaseType; b is not null && bases.Count < 16; b = b.BaseType) bases.Add(b.Name);
        var source =
            System.Text.RegularExpressions.Regex.IsMatch(route, $@"\{{{p.Name}(\W[^}}]*)?\}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "route"
            : t.Name is "HttpContext" or "HttpRequest" or "HttpResponse" or "CancellationToken" or "ClaimsPrincipal" or "Stream" or "PipeReader" ? "special"
            : t.Name is "IFormFile" or "IFormFileCollection" or "IFormCollection" ? "form"
            : t.TypeKind == TypeKind.Interface || (t.TypeKind == TypeKind.Error && t.Name is ['I', >= 'A' and <= 'Z', ..])
                || bases.Contains("DbContext") || t.Name.EndsWith("Service") || t.Name.EndsWith("Repository") || t.Name.EndsWith("Client") ? "service"
            : t.SpecialType != SpecialType.None || t.TypeKind == TypeKind.Enum
                || t.Name is "Guid" or "DateTimeOffset" or "TimeSpan" or "DateOnly" or "TimeOnly" or "Uri"
                || t is IArrayTypeSymbol { ElementType.SpecialType: not SpecialType.None } ? "query"
            : "body";
        return $"[{source}] {declared}";
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

    static readonly string[] SectionCalls = ["GetSection", "GetRequiredSection", "GetConnectionString", "GetValue", "BindConfiguration"];
    static readonly string[] OptionsCalls = ["Configure", "AddOptions", "ConfigureOptions"];

    // Configuration keys the code reads (`reads`) or binds to an options type (`binds`): GetSection("A:B"), config["A:B"],
    // GetConnectionString("X") (→ ConnectionStrings:X), GetValue<T>("A"), Configure<T>(..GetSection("A")),
    // AddOptions<T>().BindConfiguration("A"), Environment.GetEnvironmentVariable("A__B") (→ A:B).
    // ponytail: configuration types are unresolved (no package refs), so receivers are matched by call and name shape.
    static void ScanConfigurationReads(SemanticModel sm, SyntaxNode root, string rel, List<Node> nodes, List<Edge> edges)
    {
        static string? Literal(BaseArgumentListSyntax? args) =>
            args?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.Value: string v } ? v : null;
        static bool LooksLikeConfig(ExpressionSyntax receiver) =>
            System.Text.RegularExpressions.Regex.IsMatch(receiver.ToString().Split('.').Last(), "config|settings", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // The key an expression reads: its own literal, prefixed by the GetSection(..) it is called on.
        static string? Key(ExpressionSyntax e) => e switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv when SectionCalls.Contains(ma.Name.Identifier.Text)
                && Literal(inv.ArgumentList) is { } lit =>
                Join(Key(ma.Expression), ma.Name.Identifier.Text == "GetConnectionString" ? $"ConnectionStrings:{lit}" : lit),
            ElementAccessExpressionSyntax ea when Literal(ea.ArgumentList) is { } lit
                && (Key(ea.Expression) is not null || LooksLikeConfig(ea.Expression)) => Join(Key(ea.Expression), lit),
            _ => null,
        };
        static string Join(string? prefix, string key) => prefix is null ? key : $"{prefix}:{key}";

        foreach (var n in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            string? key = null;
            if (n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetEnvironmentVariable" } } env
                && Literal(env.ArgumentList) is { } name)
                key = name.Replace("__", ":");
            else if (n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv && SectionCalls.Contains(ma.Name.Identifier.Text)
                && (ma.Name.Identifier.Text != "GetValue" || Key(ma.Expression) is not null || LooksLikeConfig(ma.Expression)))
                key = Key(inv);
            else if (n is ElementAccessExpressionSyntax)
                key = Key(n);
            if (key is null) continue;
            // Only the outermost expression names the full key (`GetSection("A")["B"]` → A:B).
            if (n.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } && Key(outer) is not null
                || n.Parent is ElementAccessExpressionSyntax ea && ea.Expression == n && Key(ea) is not null) continue;

            nodes.Add(new Node(Configuration.Id(key), "config", key, rel, Line(n)));
            var (owner, kind) = OptionsOwner(sm, n) is { } options ? (options, "binds") : (Owner(sm, n), "reads");
            edges.Add(new(owner ?? $"file:{rel}", Configuration.Id(key), kind)); // top-level code: resolved to its project later
        }
    }

    // Configure<T>(..), AddOptions<T>().Bind*(..): the options type T the key is bound to.
    static string? OptionsOwner(SemanticModel sm, SyntaxNode at)
    {
        foreach (var a in at.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
            for (ExpressionSyntax? e = a; e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }; e = ma.Expression)
                if (ma.Name is GenericNameSyntax { TypeArgumentList.Arguments: [var t] } g && OptionsCalls.Contains(g.Identifier.Text)
                    && sm.GetTypeInfo(t).Type is { } type && InSource(type))
                    return Id(type);
        return null;
    }

    // Who talks to an external system: T of an enclosing AddDbContext<T>/AddHttpClient<T>(..), else the enclosing member
    // (method, property, constructor), else the enclosing type.
    static string? Owner(SemanticModel sm, SyntaxNode at)
    {
        foreach (var a in at.AncestorsAndSelf())
        {
            if (a is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax
                    { Identifier.Text: "AddDbContext" or "AddDbContextPool" or "AddDbContextFactory" or "AddHttpClient", TypeArgumentList.Arguments: [.., var t] } } }
                && sm.GetTypeInfo(t).Type is { } type && InSource(type)) return Id(type);
            if (a is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax && sm.GetDeclaredSymbol(a) is { } member) return Id(member);
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
                foreach (var p in lm.Parameters.Where(p => InSource(p.Type) && MinimalParam(p, route).StartsWith("[service]")))
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
        var chain = new List<InvocationExpressionSyntax>();
        for (SyntaxNode n = inv; n.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } ma; n = outer)
        {
            chain.Add(outer);
            if (ma.Name.Identifier.Text is "WithSummary" or "WithDescription"
                && outer.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax l)
                text ??= l.Token.ValueText;
            if (ma.Name.Identifier.Text == "RequireAuthorization") tags.Add("authorize");
            if (ma.Name.Identifier.Text == "AllowAnonymous") tags.Add("anonymous");
        }
        // A statement alone in an `if` (`if (env.IsDevelopment()) { app.MapPost(..) }`) takes the comment above the `if`.
        for (var st = inv.FirstAncestorOrSelf<StatementSyntax>(); st is not null && text is not { Length: > 0 };
             st = (st.Parent is BlockSyntax { Statements.Count: 1 } b ? b : st).Parent is IfStatementSyntax ifs
                 && ifs.Statement == (st.Parent as BlockSyntax ?? st) ? ifs : null)
            text = string.Join(" ", st.GetLeadingTrivia()
                .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                .Select(t => t.ToString().TrimStart('/', '*', ' ').TrimEnd('*', '/', ' '))).Trim();
        if (text is { Length: > 0 }) doc ??= new XElement("member", new XElement("summary", text)).ToString();

        // A method group's handler: its own attributes, return type and body.
        var target = handler is AnonymousFunctionExpressionSyntax ? null
            : signature?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
        var responses = Responses(sm, target?.AttributeLists ?? default, (SyntaxNode?)target ?? handler,
            target?.ReturnType ?? (handler as ParenthesizedLambdaExpressionSyntax)?.ReturnType, chain);
        nodes.Add(new Node(id, "endpoint", $"{verb} {route}", rel, Line(inv), "public", doc,
            Complexity(handler), Hash: Hash(inv), Tags: tags, Route: route, EndLine: EndLine(inv),
            Parameters: signature?.Parameters.Select(p => MinimalParam(p, route)).ToList() is { Count: > 0 } pl ? pl : null,
            Returns: signature is null ? null : Returns(signature.ReturnType), Throws: Throws(sm, handler), Responses: responses));
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

    static IEnumerable<INamespaceSymbol> AllNamespaces(INamespaceSymbol n) => n.GetNamespaceMembers().SelectMany(AllNamespaces).Prepend(n);

    static bool InSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);
    static string Id(ISymbol s) => "cs:" + s.ToDisplayString(IdFormat);
    static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    static int EndLine(SyntaxNode n) => n.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
    static string Vis(ISymbol s) => s.DeclaredAccessibility.ToString().ToLowerInvariant();

    static string? Doc(ISymbol s)
    {
        var xml = s.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(xml)) return InheritSource(s, xml.Trim());
        if (!commentDocs) return null;
        // `comment_docs: true`: a plain `//` block directly above the declaration (no blank line in between) is its summary.
        var text = s.DeclaringSyntaxReferences.Select(r => LeadingComment(r.GetSyntax())).FirstOrDefault(t => t.Length > 0);
        return text is null ? null : new XElement("member", new XElement("summary", text)).ToString();
    }

    // FACT: what a bare <inheritdoc/> can inherit from, recorded on it as source="none|interface|base" so the analyzer
    // can tell an empty one apart (an external base, like ControllerBase, isn't in the model but still has docs).
    static string InheritSource(ISymbol s, string xml)
    {
        if (!xml.Contains("inheritdoc")) return xml;
        XElement doc;
        try { doc = XElement.Parse(xml); }
        catch { return xml; }
        if (doc.Element("inheritdoc") is not { } inherit || inherit.Attribute("cref") is not null) return xml;
        var type = s as INamedTypeSymbol ?? s.ContainingType;
        var fromBase = s switch
        {
            INamedTypeSymbol t => t.BaseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType
                or SpecialType.System_Enum or SpecialType.System_MulticastDelegate) },
            IMethodSymbol m => m.IsOverride || m.MethodKind == MethodKind.Constructor,
            IPropertySymbol p => p.IsOverride,
            IEventSymbol e => e.IsOverride,
            _ => true,
        };
        var fromInterface = s is INamedTypeSymbol nt ? nt.AllInterfaces.Length > 0
            : type is not null && type.AllInterfaces.SelectMany(i => i.GetMembers())
                .Any(im => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(im), s));
        inherit.SetAttributeValue("source", fromBase ? "base" : fromInterface ? "interface" : "none");
        return doc.ToString();
    }

    static string LeadingComment(SyntaxNode n)
    {
        var trivia = n.GetLeadingTrivia();
        var lines = new List<string>();
        var newlines = 0;
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var t = trivia[i];
            if (t.IsKind(SyntaxKind.WhitespaceTrivia)) continue;
            if (t.IsKind(SyntaxKind.EndOfLineTrivia)) { if (++newlines > 1) break; continue; }
            if (!t.IsKind(SyntaxKind.SingleLineCommentTrivia)) break;
            lines.Insert(0, t.ToString()[2..].Trim());
            newlines = 0;
        }
        return string.Join(" ", lines).Trim();
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
