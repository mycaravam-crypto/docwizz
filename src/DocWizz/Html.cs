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
        var title = Regex.Match(markdown, @"^# (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        return $$"""
            <!doctype html>
            {{Generator.Marker}}
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
            <style>
            :root { --bg: #fff; --fg: #1f2328; --muted: #59636e; --line: #d1d9e0; --link: #0969da; --code: #f6f8fa; }
            @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #9198a1; --line: #3d444d; --link: #4493f8; --code: #151b23; } }
            body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 system-ui, sans-serif; }
            header { display: flex; gap: 1rem; flex-wrap: wrap; align-items: center; padding: .6rem 1rem; border-bottom: 1px solid var(--line); }
            header a { font-weight: 600; } main { max-width: 72rem; margin: 0 auto; padding: 1rem; overflow-wrap: anywhere; }
            a { color: var(--link); } code, pre { background: var(--code); border-radius: 4px; } code { padding: 0 .25em; } pre { padding: .75rem; overflow-x: auto; }
            table { border-collapse: collapse; display: block; overflow-x: auto; margin: 1rem 0; } th, td { border: 1px solid var(--line); padding: .3rem .6rem; text-align: left; vertical-align: top; }
            #q { margin-left: auto; padding: .3rem .5rem; min-width: 14rem; } #hits { list-style: none; margin: 0; padding: 0 1rem; } #hits li { padding: .15rem 0; }
            #hits small { color: var(--muted); }
            </style></head><body>
            <header><a href="{{up}}index.html">Index</a><a href="{{up}}architecture.html">Architecture</a><a href="{{up}}api.html">API</a>
            <a href="{{up}}frontend.html">Frontend</a><a href="{{up}}quality.html">Quality</a>
            <input id="q" type="search" placeholder="Search components, endpoints, keys…" aria-label="Search"></header>
            <ul id="hits"></ul>
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
                li.append(a, k); return li;
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
