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
    // projects, adds the solution → project → module breadcrumb and sidebar group.
    public static string Page(string rel, string markdown, IReadOnlyDictionary<string, string> pages, ProjectHierarchy? hierarchy = null)
    {
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var body = Markdown.ToHtml(markdown.Replace(Generator.Marker, ""), Pipeline);
        body = Regex.Replace(body, @"href=""([^""#:?]+)\.md(#[^""]*)?""", m =>
        {
            var target = Path.GetRelativePath(".", Path.Combine(dir, m.Groups[1].Value + ".md")).Replace('\\', '/');
            return pages.ContainsKey(target) ? $"href=\"{m.Groups[1].Value}.html{m.Groups[2].Value}\"" : m.Value;
        });
        // Wide tables scroll without narrowing the content.
        body = Regex.Replace(body, @"<table\b[^>]*>", "<div class=\"table-wrap\">$0").Replace("</table>", "</table></div>");
        var tableIndex = 0;
        body = Regex.Replace(body, @"<div class=""table-wrap"">(?=<table)", _ =>
        {
            var id = $"table-filter-{++tableIndex}";
            return $"<div class=\"table-tools\"><label for=\"{id}\">Filter table</label><input id=\"{id}\" type=\"search\" data-table-filter placeholder=\"Search rows…\" autocomplete=\"off\"><span class=\"table-count\" aria-live=\"polite\"></span></div><div class=\"table-wrap\">";
        });
        var up = string.Concat(Enumerable.Repeat("../", rel.Count(c => c == '/')));
        string Link(string page, string label) =>
            $"<a href=\"{up}{Path.ChangeExtension(page, ".html")}\"{(page == rel ? " aria-current=\"page\"" : "")}>{Enc(label)}</a>";
        // A view or the description marks Architecture in the header as the section it belongs to.
        var section = rel.StartsWith("views/") || rel == "architecture-description.md" ? "architecture.md"
            : rel == "tests.md" ? "quality.md" : rel;
        var nav = string.Concat(Top.Select(n => n.Page != rel && n.Page == section
            ? $"<a href=\"{up}{Path.ChangeExtension(n.Page, ".html")}\" aria-current=\"true\">{Enc(n.Label)}</a>" : Link(n.Page, n.Label)));
        var title = Title(rel, markdown);
        var side = Sidebar(pages, Link, hierarchy);
        var toc = TableOfContents(body);
        var category = rel.StartsWith("modules/") ? "Module" : rel.StartsWith("views/") ? "Architecture view"
            : rel.StartsWith("routes/") ? "Route" : rel.StartsWith("requirements/") ? "Requirement" : rel.StartsWith("projects/") ? "Project" : rel.StartsWith("solutions/") ? "Solution" : "Documentation";
        // Docs / the solution and project (or Architecture) above this page / this page.
        List<string> trail = rel.StartsWith("views/") ? [Link("architecture.md", "Architecture")]
            : rel == "tests.md" ? [Link("quality.md", "Quality")]
            : hierarchy?.Breadcrumbs(rel, Link) ?? [];
        var breadcrumbs = string.Join("<span aria-hidden=\"true\">/</span>", [$"<a href=\"{up}index.html\">Docs</a>", .. trail, $"<span aria-current=\"page\">{Enc(title)}</span>"]);
        return $$"""
            <!doctype html>
            {{Generator.Marker}}
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
            <style>
            {{HtmlDesign.Css}}
            </style></head><body>
            <a class="skip" href="#content">Skip to content</a>
            <div class="reading-progress" id="reading-progress" aria-hidden="true"></div>
            <header><button class="icon-button" id="nav-toggle" type="button" aria-label="Open navigation" aria-controls="site-nav" aria-expanded="false">☰</button><a class="brand" href="{{up}}index.html"><span class="brand-mark" aria-hidden="true">DW</span><span class="brand-name">Doc<span>Wizz</span></span></a><nav aria-label="Primary">{{nav}}</nav>
            <div class="header-actions"><div class="search"><input id="q" type="search" placeholder="Search docs..." aria-label="Search"
              role="combobox" aria-expanded="false" aria-controls="hits" aria-autocomplete="list" autocomplete="off"><kbd aria-hidden="true">/</kbd><ul id="hits" role="listbox"></ul></div><button class="icon-button" id="theme-toggle" type="button" aria-label="Switch theme" title="Switch theme">☾</button></div></header>
            <div class="layout">
            <nav class="side" id="site-nav" aria-label="Site"><div class="side-top">Explore <button class="icon-button" id="nav-close" type="button" aria-label="Close navigation">×</button></div><div class="filter-wrap"><input id="side-filter" type="search" placeholder="Filter pages" aria-label="Filter navigation pages" autocomplete="off"></div><p class="side-empty" id="side-empty" hidden>No pages match.</p>{{side}}</nav>
            <main id="content">
            <nav class="breadcrumbs" aria-label="Breadcrumb">{{breadcrumbs}}</nav>
            <span class="report-label">{{Enc(category)}}</span>
            {{body}}
            <footer class="generator">Generated by docwizz {{Enc(AppVersion.Number)}}</footer>
            </main>
            <aside class="toc" aria-label="On this page">{{toc}}</aside>
            </div>
            <script>
            document.querySelectorAll('[data-table-filter]').forEach(input => {
              const wrap = input.parentElement.nextElementSibling;
              const rows = [...(wrap?.querySelector('tbody')?.rows || [])];
              const count = input.parentElement.querySelector('.table-count');
              const update = () => {
                const query = input.value.trim().toLocaleLowerCase();
                let visible = 0;
                rows.forEach(row => {
                  const match = row.textContent.toLocaleLowerCase().includes(query);
                  row.hidden = !match;
                  if (match) visible++;
                });
                count.textContent = visible + ' / ' + rows.length + ' rows';
              };
              input.addEventListener('input', update);
              update();
            });
            </script>
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
            </script>
            <script type="module">
            if (document.querySelector('.mermaid')) {
              const { default: mermaid } = await import('{{MermaidUrl}}');
              mermaid.initialize({ startOnLoad: false, theme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' });
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

    // A collapsible sidebar group, open only when it holds the current page.
    internal static string Folder(string summary, string items) => items.Length == 0 ? "" :
        $"<li><details{((summary + items).Contains("aria-current=\"page\"") ? " open" : "")}><summary>{summary}</summary><ul>{items}</ul></details></li>";

    // The header's sections in its order, Architecture holding its description and views, then the code: solutions →
    // projects → modules from `hierarchy`, else the flat module list.
    static string Sidebar(IReadOnlyDictionary<string, string> pages, Func<string, string, string> link, ProjectHierarchy? hierarchy)
    {
        string Items(IEnumerable<(string Page, string Label)> items) => string.Concat(items.Select(i => $"<li>{link(i.Page, i.Label)}</li>"));
        // Views keep the order the generator wrote them in (context first), not their titles' order.
        var architecture = new[] { ("architecture.md", "Overview"), ("architecture-description.md", "Description") }
            .Where(p => pages.ContainsKey(p.Item1)).Concat(pages.Where(p => p.Key.StartsWith("views/")).Select(p => (p.Key, p.Value)));
        var quality = new[] { ("quality.md", "Overview"), ("tests.md", "Tests & requirements") }
            .Where(p => pages.ContainsKey(p.Item1));
        var top = Top.Select(t => t.Page == "architecture.md" ? Folder("Architecture", Items(architecture))
            : t.Page == "quality.md" ? Folder("Quality", Items(quality))
            : pages.ContainsKey(t.Page) ? Items([t]) : "");
        var other = pages.Where(p => !p.Key.Contains('/') && p.Key != "architecture-description.md" && p.Key != "tests.md" && Top.All(t => t.Page != p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value));
        var modules = pages.Where(p => p.Key.StartsWith("modules/")).OrderBy(p => p.Value, StringComparer.Ordinal).Select(p => (p.Key, p.Value));
        var code = hierarchy?.Sidebar(link) is { Length: > 0 } tree ? tree : Items(modules);
        return "<ul>" + string.Concat(top) + Items(other) + Folder("Code", code) + "</ul>";
    }
}
