using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

record Node(string Id, string Kind, string Name, string File, int Line,
    string? Visibility = null, string? Doc = null, int? Complexity = null,
    int? Params = null, string? Hash = null, List<string>? Tags = null, string? Route = null);

record Edge(string From, string To, string Kind);

record Model(string? Commit, List<Node> Nodes, List<Edge> Edges);

static class CSharpScanner
{
    static readonly SymbolDisplayFormat IdFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;
    static readonly string[] HttpVerbs = ["HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch"];
    static readonly string[] MapVerbs = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"];

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
                var tags = new List<string>();
                if (HasAttr(decl.AttributeLists, "ApiController") || type.BaseType?.Name is "ControllerBase" or "Controller")
                    tags.Add("controller");
                if (type.BaseType?.Name == "DbContext") tags.Add("dbcontext");

                nodes.Add(new Node(typeId, Kind(type), type.Name, rel, Line(decl),
                    Vis(type), Doc(type), Hash: Hash(decl), Tags: tags.Count > 0 ? tags : null,
                    Route: RouteArg(decl.AttributeLists, "Route")));

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
                foreach (var member in td.Members)
                    ScanMember(sm, member, typeId, rel, nodes, edges);
            }

            ScanRegistrations(sm, syntaxRoot, rel, nodes, edges);
        }

        // Partial types/methods declare the same symbol more than once; keep the first.
        return (nodes.DistinctBy(n => n.Id).ToList(), edges.Distinct().ToList());
    }

    static void ScanMember(SemanticModel sm, MemberDeclarationSyntax member, string typeId, string rel,
        List<Node> nodes, List<Edge> edges)
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
            route = RouteArg(md.AttributeLists, verb) ?? "";
        }
        if (sym is IPropertySymbol { Type: INamedTypeSymbol { Name: "DbSet", TypeArguments: [var entity] } } && InSource(entity))
            edges.Add(new(typeId, Id(entity), "dbset"));

        nodes.Add(new Node(id, Kind(sym), sym.Name, rel, Line(member), Vis(sym), Doc(sym),
            Complexity(member), ps, Hash(member), tags, route));
        edges.Add(new(typeId, id, "contains"));

        AddCalls(sm, member, id, edges);
    }

    static void AddCalls(SemanticModel sm, SyntaxNode body, string from, List<Edge> edges)
    {
        foreach (var inv in Body(body).OfType<InvocationExpressionSyntax>())
            if (Resolve(sm.GetSymbolInfo(inv)) is IMethodSymbol target && InSource(target))
                edges.Add(new(from, Id(target.ReducedFrom ?? target.OriginalDefinition), "calls"));
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

            if (IsMapCall(inv) && inv.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax lit }, _, ..])
                ScanMinimalEndpoint(sm, inv, name[3..].ToUpperInvariant(), lit.Token.ValueText, rel, nodes, edges);
        }
    }

    static void ScanMinimalEndpoint(SemanticModel sm, InvocationExpressionSyntax inv, string verb, string route,
        string rel, List<Node> nodes, List<Edge> edges)
    {
        var id = $"cs:endpoint:{verb} {route}";
        var handler = inv.ArgumentList.Arguments[1].Expression;
        string? doc = null;

        if (handler is AnonymousFunctionExpressionSyntax lambda)
        {
            // Handler parameters are what minimal APIs inject.
            if (sm.GetSymbolInfo(lambda).Symbol is IMethodSymbol lm)
                foreach (var p in lm.Parameters.Where(p => InSource(p.Type)))
                    edges.Add(new(id, Id(p.Type), "injects"));
            AddCalls(sm, lambda, id, edges);
        }
        else if (Resolve(sm.GetSymbolInfo(handler)) is IMethodSymbol method && InSource(method))
        {
            edges.Add(new(id, Id(method), "calls"));
            doc = Doc(method);
        }

        // Docs: .WithSummary/.WithDescription in the fluent chain, else a comment above the statement.
        string? text = null;
        for (SyntaxNode n = inv; n.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } ma; n = outer)
            if (ma.Name.Identifier.Text is "WithSummary" or "WithDescription"
                && outer.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax l)
                text ??= l.Token.ValueText;
        text ??= string.Join(" ", (inv.FirstAncestorOrSelf<StatementSyntax>()?.GetLeadingTrivia() ?? default)
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .Select(t => t.ToString().TrimStart('/', '*', ' ').TrimEnd('*', '/', ' '))).Trim();
        if (text is { Length: > 0 }) doc ??= new XElement("member", new XElement("summary", text)).ToString();

        nodes.Add(new Node(id, "endpoint", $"{verb} {route}", rel, Line(inv), "public", doc,
            Complexity(handler), Hash: Hash(inv), Tags: ["endpoint", verb, "minimal-api"], Route: route));
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
