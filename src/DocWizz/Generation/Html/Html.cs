using System.Text.RegularExpressions;
using Markdig;

// HTML twins of the generated Markdown, written next to it: the same tree, so relative links to sources keep working.
// Links between generated pages go to their HTML twin; Mermaid blocks render with mermaid.js (from a CDN, the code stays
// readable offline); search.js holds the search index as a script, which works from file:// where fetch() does not.
// Every page carries the whole site in its sidebar, so each one stands alone and opens from disk.
static class Html
{
    static readonly (string Page, string Label)[] Top =
        [("index.md", "Overview"), ("architecture.md", "Architecture"), ("api.md", "API"), ("frontend.md", "Frontend"), ("quality.md", "Quality")];
    // Pinned: a new Mermaid release must not change every diagram unannounced.
    public const string MermaidUrl = "https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.esm.min.mjs";

    // The page's `# ` heading, else its file name.
    public static string Title(string rel, string markdown)
    {
        var heading = Regex.Match(markdown, @"^# (.+)$", RegexOptions.Multiline);
        return heading.Success ? heading.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(rel);
    }

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub) // same anchors as the Markdown links
        .UseAdvancedExtensions().Build();

    // `pages`: every generated Markdown page (relative, `/`-separated) and its title. `hierarchy`, when the model has
    // projects, adds the solution → project → module breadcrumb and sidebar tree.
    public static string Page(string rel, string markdown, IReadOnlyDictionary<string, string> pages, ProjectHierarchy? hierarchy = null)
    {
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var body = Markdown.ToHtml(markdown.Replace(Generator.Marker, ""), Pipeline);
        body = Regex.Replace(body, @"href=""([^""#:?]+)\.md(#[^""]*)?""", m =>
        {
            var target = Path.GetRelativePath(".", Path.Combine(dir, m.Groups[1].Value + ".md")).Replace('\\', '/');
            return pages.ContainsKey(target) ? $"href=\"{m.Groups[1].Value}.html{m.Groups[2].Value}\"" : m.Value;
        });
        // Wide tables scroll inside a wrapper; a table made display: block to scroll shrinks to its content instead.
        body = Regex.Replace(body, @"<table\b[^>]*>", "<div class=\"table-wrap\">$0").Replace("</table>", "</table></div>");
        var up = string.Concat(Enumerable.Repeat("../", rel.Count(c => c == '/')));
        string Href(string page) => up + Path.ChangeExtension(page, ".html");
        string Link(string page, string label) =>
            $"<a href=\"{Href(page)}\"{(page == rel ? " aria-current=\"page\"" : "")}>{Enc(label)}</a>";
        var nav = string.Concat(Top.Where(n => pages.ContainsKey(n.Page) || n.Page == "index.md").Select(n =>
            $"<a href=\"{Href(n.Page)}\"{(n.Page == rel || Section(rel) == n.Page ? " aria-current=\"page\"" : "")}>{Enc(n.Label)}</a>"));
        var title = Title(rel, markdown);
        var groups = Navigation(pages, hierarchy);
        var side = Sidebar(rel, groups, Href);
        var pager = Pager(rel, groups, pages, Href);
        var toc = TableOfContents(body);
        var category = rel.StartsWith("modules/") ? "Module" : rel.StartsWith("views/") ? "Architecture view"
            : rel.StartsWith("projects/") ? "Project" : rel.StartsWith("solutions/") ? "Solution" : "Documentation";
        var trail = hierarchy?.Breadcrumbs(rel, Link, title) is { Length: > 0 } path ? path
            : rel.StartsWith("views/") ? Link("architecture.md", "Architecture") + CrumbSeparator + Crumb(title)
            : rel.StartsWith("modules/") ? Crumb("Modules") + CrumbSeparator + Crumb(title)
            : rel == "index.md" ? "" : Crumb(title);
        var breadcrumbs = $"<a href=\"{up}index.html\">Docs</a>" + (trail.Length > 0 ? CrumbSeparator + trail : "");
        return $$"""
            <!doctype html>
            {{Generator.Marker}}
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
            <script>try { const t = localStorage.getItem('docwizz.report.theme'); if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t; } catch (_) {}</script>
            <style>
            {{HtmlDesign.Css}}
            </style></head><body>
            {{HtmlDesign.Icons}}
            <a class="skip" href="#content">Skip to content</a>
            <div class="reading-progress" id="reading-progress" aria-hidden="true"></div>
            <header><button class="icon-button" id="nav-toggle" type="button" aria-label="Open navigation" aria-controls="site-nav" aria-expanded="false"><svg class="ico" aria-hidden="true"><use href="#i-menu"/></svg></button><a class="brand" href="{{up}}index.html"><span class="brand-mark" aria-hidden="true">DW</span><span class="brand-name">Doc<span>Wizz</span></span></a><nav class="top-nav" aria-label="Primary">{{nav}}</nav>
            <div class="header-actions"><div class="search"><svg class="ico" aria-hidden="true"><use href="#i-search"/></svg><input id="q" type="search" placeholder="Search docs…" aria-label="Search"
              role="combobox" aria-expanded="false" aria-controls="hits" aria-autocomplete="list" autocomplete="off"><kbd aria-hidden="true">/</kbd><ul id="hits" role="listbox"></ul></div><button class="icon-button" id="theme-toggle" type="button" aria-label="Switch theme" title="Switch theme"><svg class="ico" aria-hidden="true"><use href="#i-moon"/></svg></button></div></header>
            <div class="backdrop" id="nav-backdrop" hidden></div>
            <div class="layout">
            <nav class="side" id="site-nav" aria-label="Site">
            <div class="side-top"><span>Navigation</span><span class="side-actions"><button class="text-button" id="nav-expand" type="button" title="Expand all">Expand all</button><button class="text-button" id="nav-collapse" type="button" title="Collapse all">Collapse</button><button class="icon-button" id="nav-close" type="button" aria-label="Close navigation"><svg class="ico" aria-hidden="true"><use href="#i-close"/></svg></button></span></div>
            <div class="filter-wrap"><svg class="ico" aria-hidden="true"><use href="#i-filter"/></svg><input id="side-filter" type="search" placeholder="Filter navigation" aria-label="Filter navigation pages" autocomplete="off"></div>
            <p class="side-empty" id="side-empty" hidden>No pages match.</p>
            <div class="side-scroll" id="side-scroll">{{side}}</div>
            </nav>
            <main id="content">
            <nav class="breadcrumbs" aria-label="Breadcrumb">{{breadcrumbs}}</nav>
            <span class="report-label">{{Enc(category)}}</span>
            {{body}}
            {{pager}}
            <footer class="generator">Generated by docwizz {{Enc(AppVersion.Number)}}</footer>
            </main>
            <aside class="toc" aria-label="On this page">{{toc}}</aside>
            </div>
            <button class="to-top" id="to-top" type="button" aria-label="Back to top" title="Back to top" hidden><svg class="ico" aria-hidden="true"><use href="#i-up"/></svg></button>
            <script src="{{up}}search.js"></script>
            <script>{{HtmlDesign.Script}}</script>
            <script>
            // Exact names first, then prefixes, then substrings; shorter names before longer ones.
            const q = document.getElementById('q'), hits = document.getElementById('hits');
            let active = -1;
            const rank = (name, t) => { const n = name.toLowerCase(); return n === t ? 0 : n.startsWith(t) ? 1 : n.includes(t) ? 2 : -1; };
            function render() {
              const t = q.value.trim().toLowerCase();
              active = -1; q.removeAttribute('aria-activedescendant');
              const items = (t.length < 2 ? [] : (window.docwizzSearch || []).map(e => [rank(e.name, t), e]).filter(r => r[0] >= 0)
                .sort((a, b) => a[0] - b[0] || a[1].name.length - b[1].name.length || a[1].name.localeCompare(b[1].name))
                .slice(0, 20)).map(([, e], i) => {
                  const li = document.createElement('li'), a = document.createElement('a'), k = document.createElement('small');
                  li.id = 'hit-' + i; li.setAttribute('role', 'option'); li.setAttribute('aria-selected', 'false');
                  a.href = '{{up}}' + e.page.replace(/\.md(#|$)/, '.html$1'); a.textContent = e.name; a.tabIndex = -1; k.textContent = ' ' + e.kind;
                  a.append(k); li.append(a); return li;
                });
              if (t.length >= 2 && !items.length) {
                const li = document.createElement('li'); li.className = 'none'; li.textContent = 'No matches'; items.push(li);
              }
              hits.replaceChildren(...items); q.setAttribute('aria-expanded', String(items.length > 0));
            }
            function select(i) {
              const options = hits.querySelectorAll('[role=option]');
              if (!options.length) return;
              active = (i + options.length) % options.length;
              options.forEach((o, j) => o.setAttribute('aria-selected', String(j === active)));
              options[active].scrollIntoView({ block: 'nearest' }); q.setAttribute('aria-activedescendant', options[active].id);
            }
            q.addEventListener('input', render);
            q.addEventListener('keydown', e => {
              if (e.key === 'ArrowDown') { e.preventDefault(); select(active + 1); }
              else if (e.key === 'ArrowUp') { e.preventDefault(); select(active < 0 ? -1 : active - 1); }
              else if (e.key === 'Enter') { const a = hits.querySelectorAll('[role=option] a')[Math.max(active, 0)]; if (a) location.href = a.href; }
              else if (e.key === 'Escape') { q.value = ''; render(); q.blur(); }
            });
            document.addEventListener('keydown', e => {
              const el = document.activeElement;
              if (e.key === '/' && el !== q && !/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) && !el.isContentEditable) { e.preventDefault(); q.focus(); }
            });
            document.addEventListener('click', e => { if (!e.target.closest('.search')) { hits.replaceChildren(); q.setAttribute('aria-expanded', 'false'); } });
            </script>
            <script type="module">
            if (document.querySelector('.mermaid')) {
              const { default: mermaid } = await import('{{MermaidUrl}}');
              const dark = getComputedStyle(document.documentElement).colorScheme.includes('dark');
              mermaid.initialize({ startOnLoad: false, theme: dark ? 'dark' : 'default' });
              await mermaid.run();
            }
            </script>
            </body></html>
            """;
    }

    // Explicit headings improve reading navigation on long reports.
    static string TableOfContents(string body)
    {
        var headings = Regex.Matches(body, @"<h2 id=""([^""]+)"">(.*?)</h2>", RegexOptions.Singleline)
            .Select(m => (Id: Enc(m.Groups[1].Value), Label: Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")))
            .ToList();
        if (headings.Count == 0) return "";
        return "<div class=\"toc-inner\"><div class=\"toc-title\">On this page</div><ul>"
            + string.Concat(headings.Select(h => $"<li><a href=\"#{h.Id}\">{h.Label}</a></li>")) + "</ul></div>";
    }

    static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    public const string CrumbSeparator = "<span class=\"sep\" aria-hidden=\"true\">›</span>";
    public static string Crumb(string text) => $"<span>{Enc(text)}</span>";

    // Views in the order they zoom in (C4: context → containers → components), then the supporting views.
    static readonly string[] ViewOrder = ["context", "containers", "components", "data", "deployment", "packages"];
    static readonly Dictionary<string, string> PageIcons = new()
    {
        ["index.md"] = "home", ["architecture-description.md"] = "book", ["architecture.md"] = "layers",
        ["api.md"] = "api", ["frontend.md"] = "monitor", ["quality.md"] = "check",
    };

    // The top-level section a page belongs to, for the header navigation.
    static string? Section(string rel) => rel.StartsWith("views/") ? "architecture.md" : null;

    // The sidebar: Documentation, Architecture views, then Projects (solution → project → module) when the model has
    // projects, else a flat Modules list. Same groups on every page, so the order never shifts while browsing.
    static List<(string Name, string Icon, List<NavNode> Items)> Navigation(IReadOnlyDictionary<string, string> pages, ProjectHierarchy? hierarchy)
    {
        var docs = Top.Where(t => pages.ContainsKey(t.Page)).Select(t => t.Page)
            .Concat(pages.Keys.Where(k => !k.Contains('/') && Top.All(t => t.Page != k)).Order(StringComparer.Ordinal))
            .ToList();
        // The written architecture description reads best right after the overview.
        if (docs.Remove("architecture-description.md")) docs.Insert(Math.Min(1, docs.Count), "architecture-description.md");
        var groups = new List<(string, string, List<NavNode>)>
        {
            ("Documentation", "book", docs.Select(p => new NavNode(
                Top.FirstOrDefault(t => t.Page == p).Label ?? pages[p], p, PageIcons.GetValueOrDefault(p, "page"))).ToList()),
            ("Architecture views", "eye", pages.Keys.Where(k => k.StartsWith("views/"))
                .OrderBy(k => Array.IndexOf(ViewOrder, Path.GetFileNameWithoutExtension(k)) is var i and >= 0 ? i : ViewOrder.Length)
                .ThenBy(k => pages[k], StringComparer.OrdinalIgnoreCase).Select(k => new NavNode(pages[k], k, "view")).ToList()),
        };
        var tree = hierarchy?.Navigation() ?? [];
        var inTree = new HashSet<string>(Flatten(tree).Select(n => n.Page!), StringComparer.Ordinal);
        if (tree.Count > 0) groups.Add(("Projects", "box", tree.Where(n => Exists(n, pages)).ToList()));
        var modules = pages.Where(p => p.Key.StartsWith("modules/") && !inTree.Contains(p.Key))
            .OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new NavNode(p.Value, p.Key, "module")).ToList();
        groups.Add((tree.Count > 0 ? "Other modules" : "Modules", "folder", modules));
        // Generated pages outside the known folders stay reachable.
        var rest = pages.Keys.Where(k => k.Contains('/') && !k.StartsWith("views/") && !k.StartsWith("modules/") && !inTree.Contains(k))
            .Order(StringComparer.Ordinal).Select(k => new NavNode(pages[k], k)).ToList();
        groups.Add(("More", "page", rest));
        return groups.Where(g => g.Item3.Count > 0).ToList();
    }

    // Tree nodes point only at pages that were generated; a node whose page is missing keeps its children.
    static bool Exists(NavNode node, IReadOnlyDictionary<string, string> pages) =>
        node.Page is null || pages.ContainsKey(node.Page) || (node.Children?.Any(c => Exists(c, pages)) ?? false);

    static IEnumerable<NavNode> Flatten(IEnumerable<NavNode> nodes) => nodes.SelectMany(n =>
        (n.Page is null ? [] : new[] { n }).Concat(Flatten(n.Children ?? [])));

    static string Sidebar(string rel, List<(string Name, string Icon, List<NavNode> Items)> groups, Func<string, string> href)
    {
        var b = new System.Text.StringBuilder();
        bool Holds(NavNode n) => n.Page == rel || (n.Children?.Any(Holds) ?? false);
        void Node(NavNode n, string parentKey)
        {
            var key = n.Page ?? parentKey + "/" + n.Label;
            var children = n.Children ?? [];
            var open = children.Count > 0 && Holds(n);
            var current = n.Page == rel;
            var search = (n.Label + " " + n.Hint).ToLowerInvariant();
            b.Append($"<li class=\"node{(open ? " open" : "")}\" data-key=\"{Enc(key)}\" data-match=\"{Enc(search)}\"><div class=\"row{(current ? " current" : "")}\">");
            b.Append(children.Count > 0
                ? $"<button class=\"twisty\" type=\"button\" aria-expanded=\"{(open ? "true" : "false")}\" aria-label=\"Toggle {Enc(n.Label)}\"><svg class=\"ico\" aria-hidden=\"true\"><use href=\"#i-chevron\"/></svg></button>"
                : "<span class=\"twisty-space\"></span>");
            var inner = $"<svg class=\"ico kind-{n.Icon}\" aria-hidden=\"true\"><use href=\"#i-{n.Icon}\"/></svg><span class=\"label\">{Enc(n.Label)}</span>"
                + (n.Badge is null ? "" : $"<span class=\"badge badge-{Enc(n.Badge)}\">{Enc(n.Badge)}</span>")
                + (children.Count > 0 ? $"<span class=\"count\" title=\"{children.Count} {(children.Count == 1 ? "item" : "items")}\">{children.Count}</span>" : "");
            var tip = n.Hint is { Length: > 0 } h && h != n.Label ? $" title=\"{Enc(h)}\"" : "";
            b.Append(n.Page is null
                ? $"<button class=\"link folder-toggle\" type=\"button\"{tip}>{inner}</button>"
                : $"<a class=\"link\" href=\"{href(n.Page)}\"{tip}{(current ? " aria-current=\"page\"" : "")}>{inner}</a>");
            b.Append("</div>");
            if (children.Count > 0)
            {
                b.Append("<ul role=\"group\">");
                foreach (var c in children) Node(c, key);
                b.Append("</ul>");
            }
            b.Append("</li>");
        }
        foreach (var (name, icon, items) in groups)
        {
            var open = name != "Modules" && name != "Other modules" && name != "More" || items.Any(Holds);
            b.Append($"<details class=\"nav-group\" data-key=\"group:{Enc(name)}\"{(open ? " open" : "")}><summary><svg class=\"ico\" aria-hidden=\"true\"><use href=\"#i-{icon}\"/></svg>{Enc(name)}<span class=\"count\">{(name == "Projects" ? Flatten(items).Count(n => n.Icon == "project") : Flatten(items).Count())}</span></summary><ul class=\"tree\">");
            foreach (var n in items) Node(n, "group:" + name);
            b.Append("</ul></details>");
        }
        return b.ToString();
    }

    // Previous and next page in sidebar order, so a reader can walk the whole site without the sidebar.
    static string Pager(string rel, List<(string Name, string Icon, List<NavNode> Items)> groups, IReadOnlyDictionary<string, string> pages, Func<string, string> href)
    {
        var order = groups.SelectMany(g => Flatten(g.Items)).Select(n => n.Page!).Where(pages.ContainsKey).Distinct().ToList();
        var i = order.IndexOf(rel);
        if (i < 0) return "";
        string Card(int at, string cls, string label) => at < 0 || at >= order.Count ? "<span></span>"
            : $"<a class=\"{cls}\" href=\"{href(order[at])}\"><small>{label}</small><span>{Enc(pages[order[at]])}</span></a>";
        return $"<nav class=\"pager\" aria-label=\"Previous and next page\">{Card(i - 1, "prev", "← Previous")}{Card(i + 1, "next", "Next →")}</nav>";
    }
}

// One sidebar entry: a page (or a folder when `Page` is null) with an icon, an optional badge and a hint (the full
// path, shown as a tooltip and matched by the filter).
sealed record NavNode(string Label, string? Page = null, string Icon = "page", string? Badge = null, string? Hint = null,
    IReadOnlyList<NavNode>? Children = null);
