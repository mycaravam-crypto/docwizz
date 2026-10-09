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

    // `pages`: every generated Markdown page (relative, `/`-separated) and its title.
    public static string Page(string rel, string markdown, IReadOnlyDictionary<string, string> pages)
    {
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var body = Markdown.ToHtml(markdown.Replace(Generator.Marker, ""), Pipeline);
        body = Regex.Replace(body, @"href=""([^""#:?]+)\.md(#[^""]*)?""", m =>
        {
            var target = Path.GetRelativePath(".", Path.Combine(dir, m.Groups[1].Value + ".md")).Replace('\\', '/');
            return pages.ContainsKey(target) ? $"href=\"{m.Groups[1].Value}.html{m.Groups[2].Value}\"" : m.Value;
        });
        var up = string.Concat(Enumerable.Repeat("../", rel.Count(c => c == '/')));
        string Link(string page, string label) =>
            $"<a href=\"{up}{Path.ChangeExtension(page, ".html")}\"{(page == rel ? " aria-current=\"page\"" : "")}>{Enc(label)}</a>";
        var nav = string.Concat(Top.Select(n => Link(n.Page, n.Label)));
        var title = Title(rel, markdown);
        var side = Sidebar(rel, body, pages, Link);
        return $$"""
            <!doctype html>
            {{Generator.Marker}}
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
            <style>
            :root { --bg: #f8fafc; --panel: #fff; --fg: #0f172a; --muted: #64748b; --line: #e2e8f0; --link: #2563eb; --accent: #4f46e5;
              --code: #f1f5f9; --stripe: #f8fafc; --hover: #eef2ff; --shadow: 0 1px 2px rgb(15 23 42 / .06), 0 1px 3px rgb(15 23 42 / .08); }
            @media (prefers-color-scheme: dark) { :root { --bg: #0b1120; --panel: #111827; --fg: #e5e7eb; --muted: #94a3b8; --line: #1f2937;
              --link: #60a5fa; --accent: #818cf8; --code: #1e293b; --stripe: #0f172a; --hover: #1e1b4b; --shadow: none; } }
            * { box-sizing: border-box; }
            body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.6 ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; -webkit-font-smoothing: antialiased; }
            a { color: var(--link); text-decoration: none; } a:hover { text-decoration: underline; }
            header { position: sticky; top: 0; z-index: 10; display: flex; gap: .25rem; flex-wrap: wrap; align-items: center; padding: .5rem 1.25rem;
              background: color-mix(in srgb, var(--panel) 85%, transparent); backdrop-filter: blur(8px); border-bottom: 1px solid var(--line); }
            header .brand { font-weight: 700; color: var(--fg); margin-right: 1rem; letter-spacing: -.01em; } header .brand span { color: var(--accent); }
            header nav { display: flex; max-width: 100%; overflow-x: auto; } header nav a { flex: none; color: var(--muted); font-weight: 500; font-size: .9rem; padding: .35rem .7rem; border-radius: 6px; }
            header nav a:hover { color: var(--fg); background: var(--code); text-decoration: none; }
            header nav a[aria-current] { color: var(--accent); background: var(--hover); }
            .search { position: relative; margin-left: auto; }
            #q { width: 18rem; max-width: 70vw; padding: .45rem .75rem; font: inherit; font-size: .9rem; color: var(--fg); background: var(--panel);
              border: 1px solid var(--line); border-radius: 8px; outline: none; }
            #q:focus { border-color: var(--accent); box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent) 25%, transparent); }
            #hits { position: absolute; right: 0; top: calc(100% + .35rem); width: 22rem; max-width: 90vw; max-height: 60vh; overflow-y: auto; margin: 0; padding: .25rem;
              list-style: none; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 10px 25px rgb(15 23 42 / .15); }
            #hits:empty { display: none; } #hits a { display: flex; justify-content: space-between; gap: 1rem; padding: .35rem .6rem; border-radius: 5px; color: var(--fg); }
            #hits a:hover { background: var(--hover); text-decoration: none; }
            #hits small { color: var(--muted); font-size: .75rem; text-transform: uppercase; letter-spacing: .04em; }
            main { max-width: 76rem; margin: 1.5rem auto 3rem; padding: 2rem 2.5rem; background: var(--panel); border: 1px solid var(--line); border-radius: 12px;
              box-shadow: var(--shadow); overflow-wrap: anywhere; }
            @media (max-width: 640px) { main { margin: 0; padding: 1.25rem 1rem; border: 0; border-radius: 0; } header .brand { display: none; } }
            h1, h2, h3 { line-height: 1.25; letter-spacing: -.015em; scroll-margin-top: 4rem; }
            h1 { font-size: 1.9rem; margin: 0 0 .5rem; } h2 { font-size: 1.3rem; margin: 2.5rem 0 .75rem; padding-bottom: .4rem; border-bottom: 1px solid var(--line); }
            h3 { font-size: 1.05rem; margin: 1.75rem 0 .5rem; } h1 + p { color: var(--muted); margin-top: 0; }
            hr { border: 0; border-top: 1px solid var(--line); margin: 2rem 0; } em { color: var(--muted); }
            code { font: .85em/1.5 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; background: var(--code); padding: .1em .35em; border-radius: 4px; }
            pre { background: var(--code); border: 1px solid var(--line); border-radius: 8px; padding: 1rem; overflow-x: auto; } pre code { padding: 0; background: none; }
            pre.mermaid { background: none; border: 0; text-align: center; }
            table { border-collapse: separate; border-spacing: 0; display: block; max-width: 100%; width: max-content; overflow-x: auto; margin: 1rem 0;
              border: 1px solid var(--line); border-radius: 8px; font-size: .9rem; }
            th, td { padding: .5rem .85rem; text-align: left; vertical-align: top; border-bottom: 1px solid var(--line); }
            th { background: var(--code); font-weight: 600; font-size: .78rem; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); white-space: nowrap; }
            thead:not(:has(th:not(:empty))) { display: none; }
            tbody tr:nth-child(even) { background: var(--stripe); } tbody tr:hover { background: var(--hover); } tbody tr:last-child td { border-bottom: 0; }
            ul, ol { padding-left: 1.4rem; } li { margin: .2rem 0; }
            .generator { margin-top: 3rem; padding-top: .75rem; border-top: 1px solid var(--line); color: var(--muted); font-size: .8rem; }
            #hits .none { padding: .35rem .6rem; color: var(--muted); } #hits [aria-selected=true] a { background: var(--hover); }
            .skip { position: absolute; left: -999px; } .skip:focus { left: 1rem; top: .5rem; z-index: 20; padding: .4rem .75rem; background: var(--panel); border-radius: 6px; }
            .layout { display: grid; grid-template-columns: 15rem minmax(0, 1fr); gap: 1.5rem; align-items: start; max-width: 94rem; margin: 1.5rem auto 3rem; padding: 0 1.25rem; }
            .layout main { margin: 0; max-width: none; }
            .side { position: sticky; top: 4rem; max-height: calc(100vh - 5rem); overflow-y: auto; font-size: .875rem; }
            .side details { margin-bottom: .75rem; } .side summary { cursor: pointer; padding: .25rem .5rem; color: var(--muted); font-size: .75rem; font-weight: 600;
              text-transform: uppercase; letter-spacing: .04em; }
            .side ul { list-style: none; margin: .25rem 0 0; padding: 0; } .side li { margin: 0; }
            .side a { display: block; padding: .25rem .5rem; border-radius: 6px; color: var(--fg); overflow-wrap: anywhere; }
            .side a:hover { background: var(--code); text-decoration: none; } .side a[aria-current] { color: var(--accent); background: var(--hover); font-weight: 600; }
            /* Narrow screens: content first, the site map after it. */
            @media (max-width: 900px) { .layout { grid-template-columns: minmax(0, 1fr); } .side { order: 1; position: static; max-height: none; } }
            @media (max-width: 640px) { .layout { margin: 0; padding: 0; gap: 0; } .side { padding: .75rem 1rem; border-top: 1px solid var(--line); } }
            @media print {
              :root { --bg: #fff; --panel: #fff; --fg: #000; --muted: #444; --line: #ccc; --link: #000; --code: #f4f4f4; --stripe: #fff; --hover: #fff; --shadow: none; }
              header, .side, .skip { display: none; } .layout { display: block; max-width: none; margin: 0; padding: 0; }
              main { padding: 0; border: 0; box-shadow: none; } pre { white-space: pre-wrap; } h2, h3 { break-after: avoid; } tr, pre { break-inside: avoid; }
            }
            </style></head><body>
            <a class="skip" href="#content">Skip to content</a>
            <header><a class="brand" href="{{up}}index.html">Doc<span>Wizz</span></a><nav>{{nav}}</nav>
            <div class="search"><input id="q" type="search" placeholder="Search code and endpoints (/)" aria-label="Search"
              role="combobox" aria-expanded="false" aria-controls="hits" aria-autocomplete="list" autocomplete="off"><ul id="hits" role="listbox"></ul></div></header>
            <div class="layout">
            <nav class="side" aria-label="Site">{{side}}</nav>
            <main id="content">
            {{body}}
            <footer class="generator">Generated by docwizz {{Enc(AppVersion.Number)}}</footer>
            </main>
            </div>
            <script src="{{up}}search.js"></script>
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

    static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // Pages, Views and Modules, the group holding this page open; above them this page's `##` sections when there are 3+.
    static string Sidebar(string rel, string body, IReadOnlyDictionary<string, string> pages, Func<string, string, string> link)
    {
        string Group(string name, IReadOnlyList<(string Page, string Label)> items, bool open) => items.Count == 0 ? "" :
            $"<details{(open || items.Any(i => i.Page == rel) ? " open" : "")}><summary>{name}</summary><ul>"
            + string.Concat(items.Select(i => $"<li>{link(i.Page, i.Label)}</li>")) + "</ul></details>";
        List<(string, string)> Under(string folder) => pages.Where(p => p.Key.StartsWith(folder + "/"))
            .OrderBy(p => p.Value, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList();
        var topPages = Top.Where(t => pages.ContainsKey(t.Page))
            .Concat(pages.Where(p => !p.Key.Contains('/') && Top.All(t => t.Page != p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)))
            .ToList();
        // Markdig's auto identifiers: <h2 id="...">text</h2>; the text is already HTML, only tags are dropped.
        var sections = Regex.Matches(body, @"<h2 id=""([^""]+)"">(.*?)</h2>")
            .Select(m => $"<li><a href=\"#{m.Groups[1].Value}\">{Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")}</a></li>").ToList();
        var toc = sections.Count >= 3 ? $"<details open><summary>On this page</summary><ul>{string.Concat(sections)}</ul></details>" : "";
        return toc + Group("Pages", topPages, true) + Group("Views", Under("views"), false) + Group("Modules", Under("modules"), false);
    }
}
