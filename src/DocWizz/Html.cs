using System.Text.RegularExpressions;
using Markdig;

// HTML twins of the generated Markdown, written next to it: the same tree, so relative links to sources keep working.
// Links between generated pages go to their HTML twin; Mermaid blocks render with mermaid.js (from a CDN, the code stays
// readable offline); search.js holds the search index as a script, which works from file:// where fetch() does not.
static class Html
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub) // same anchors as the Markdown links
        .UseAdvancedExtensions().Build();

    public static string Page(string rel, string markdown, ISet<string> pages)
    {
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var body = Markdown.ToHtml(markdown.Replace(Generator.Marker, ""), Pipeline);
        body = Regex.Replace(body, @"href=""([^""#:?]+)\.md(#[^""]*)?""", m =>
        {
            var target = Path.GetRelativePath(".", Path.Combine(dir, m.Groups[1].Value + ".md")).Replace('\\', '/');
            return pages.Contains(target) ? $"href=\"{m.Groups[1].Value}.html{m.Groups[2].Value}\"" : m.Value;
        });
        var up = string.Concat(Enumerable.Repeat("../", rel.Count(c => c == '/')));
        var nav = string.Concat(new[] { ("index", "Overview"), ("architecture", "Architecture"), ("api", "API"), ("frontend", "Frontend"), ("quality", "Quality") }
            .Select(n => $"<a href=\"{up}{n.Item1}.html\"{(rel == n.Item1 + ".md" ? " aria-current=\"page\"" : "")}>{n.Item2}</a>"));
        var title = Regex.Match(markdown, @"^# (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
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
            header nav a { color: var(--muted); font-weight: 500; font-size: .9rem; padding: .35rem .7rem; border-radius: 6px; }
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
            </style></head><body>
            <header><a class="brand" href="{{up}}index.html">Doc<span>Wizz</span></a><nav>{{nav}}</nav>
            <div class="search"><input id="q" type="search" placeholder="Search components, endpoints, keys…" aria-label="Search"><ul id="hits"></ul></div></header>
            <main>
            {{body}}
            </main>
            <script src="{{up}}search.js"></script>
            <script>
            const q = document.getElementById('q'), hits = document.getElementById('hits');
            q.addEventListener('input', () => {
              const t = q.value.trim().toLowerCase();
              hits.replaceChildren(...(t.length < 2 ? [] : (window.docwizzSearch || []).filter(e => e.name.toLowerCase().includes(t)).slice(0, 20).map(e => {
                const li = document.createElement('li'), a = document.createElement('a'), k = document.createElement('small');
                a.href = '{{up}}' + e.page.replace(/\.md(#|$)/, '.html$1'); a.textContent = e.name; k.textContent = ' ' + e.kind;
                a.append(k); li.append(a); return li;
              })));
            });
            </script>
            <script type="module">
            if (document.querySelector('.mermaid')) {
              const { default: mermaid } = await import('https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs');
              mermaid.initialize({ startOnLoad: false, theme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' });
              await mermaid.run();
            }
            </script>
            </body></html>
            """;
    }
}
