// Self-contained report theme: no fonts, stylesheets, or runtime dependencies needed for file:// pages.
// Keep design tokens and behavior separate from the Markdown/HTML renderer.
static class HtmlDesign
{
    public const string Css = """
:root {
  color-scheme: light;
  --canvas: #f4f6f9; --surface: #ffffff; --surface-soft: #f6f8fb; --hover: #eef2f7;
  --ink: #172334; --text: #344457; --muted: #5c6b7e; --faint: #8593a5;
  --line: #dfe5ed; --guide: #e4e9f0; --accent: #235bbd; --accent-hover: #174a9c;
  --accent-soft: #e8effc; --code: #eef2f6; --stripe: #f8fafc;
  --solution: #7046c8; --project: #235bbd; --module: #0d7f73; --view: #b25f12;
  --badge-app: #e3f4ea; --badge-app-ink: #1c6b3c; --badge-test: #fbf0dc; --badge-test-ink: #8a5a08;
  --shadow: 0 2px 5px rgb(18 33 55 / .04), 0 14px 44px rgb(18 33 55 / .045);
  --radius: 12px; --header-height: 64px; --side-width: 300px;
}
:root[data-theme="dark"] {
  color-scheme: dark; --canvas: #0f141c; --surface: #161f2b; --surface-soft: #1c2735; --hover: #223042;
  --ink: #f1f5fb; --text: #d2ddeb; --muted: #a3b3c6; --faint: #7a8ba0; --line: #2c3a4c; --guide: #2a3747;
  --accent: #8eb7ff; --accent-hover: #c2d6ff; --accent-soft: #22385a;
  --code: #253344; --stripe: #1c2735;
  --solution: #b79cff; --project: #8eb7ff; --module: #5fd0c0; --view: #f0a860;
  --badge-app: #1d3b2a; --badge-app-ink: #8fe0ae; --badge-test: #3d3019; --badge-test-ink: #f1c879;
  --shadow: 0 12px 42px rgb(0 0 0 / .16);
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark; --canvas: #0f141c; --surface: #161f2b; --surface-soft: #1c2735; --hover: #223042;
    --ink: #f1f5fb; --text: #d2ddeb; --muted: #a3b3c6; --faint: #7a8ba0; --line: #2c3a4c; --guide: #2a3747;
    --accent: #8eb7ff; --accent-hover: #c2d6ff; --accent-soft: #22385a;
    --code: #253344; --stripe: #1c2735;
    --solution: #b79cff; --project: #8eb7ff; --module: #5fd0c0; --view: #f0a860;
    --badge-app: #1d3b2a; --badge-app-ink: #8fe0ae; --badge-test: #3d3019; --badge-test-ink: #f1c879;
    --shadow: 0 12px 42px rgb(0 0 0 / .16);
  }
}
*, *::before, *::after { box-sizing: border-box; }
html { scroll-behavior: smooth; scroll-padding-top: 100px; }
body { margin: 0; color: var(--text); background: var(--canvas); font: 400 15px/1.7 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; -webkit-font-smoothing: antialiased; }
button, input { font: inherit; }
button { cursor: pointer; }
a { color: var(--accent); text-underline-offset: .15em; }
a:hover { color: var(--accent-hover); }
:focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }
[hidden] { display: none !important; }
.ico { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.skip { position: fixed; left: 12px; top: -100px; z-index: 60; padding: 10px 16px; background: var(--surface); color: var(--ink); border-radius: 8px; }
.skip:focus { top: 12px; }
.reading-progress { position: fixed; z-index: 50; left: 0; top: 0; width: 0; height: 3px; background: var(--accent); pointer-events: none; }
header { position: sticky; top: 0; z-index: 30; display: flex; align-items: center; gap: 18px; height: var(--header-height); padding: 0 clamp(16px, 2vw, 28px); background: color-mix(in srgb, var(--surface) 92%, transparent); backdrop-filter: blur(8px); border-bottom: 1px solid var(--line); }
header .brand { display: inline-flex; align-items: center; gap: 10px; flex: none; color: var(--ink); font-size: 18px; font-weight: 750; letter-spacing: -.04em; text-decoration: none; }
.brand-mark { display: grid; place-items: center; width: 32px; height: 32px; color: white; background: #285bb0; border-radius: 9px; font-size: 13px; font-weight: 800; letter-spacing: -.06em; }
header .brand span:not(.brand-mark) { color: var(--accent); }
.top-nav { display: flex; align-items: center; gap: 2px; min-width: 0; overflow-x: auto; scrollbar-width: none; }
.top-nav a { display: block; flex: none; padding: 7px 11px; border-radius: 8px; color: var(--muted); font-size: 13.5px; font-weight: 600; text-decoration: none; }
.top-nav a:hover { color: var(--ink); background: var(--hover); }
.top-nav a[aria-current] { color: var(--accent); background: var(--accent-soft); }
.header-actions { display: flex; align-items: center; gap: 8px; margin-left: auto; }
.icon-button { flex: none; display: inline-flex; align-items: center; justify-content: center; width: 36px; height: 36px; padding: 0; color: var(--text); background: var(--surface); border: 1px solid var(--line); border-radius: 9px; }
.icon-button .ico { width: 18px; height: 18px; }
.icon-button:hover { background: var(--hover); color: var(--ink); }
#nav-toggle, #nav-close { display: none; }
.search { position: relative; width: clamp(200px, 22vw, 340px); }
.search > .ico, .filter-wrap > .ico { position: absolute; top: 50%; left: 11px; transform: translateY(-50%); color: var(--faint); pointer-events: none; }
#q, #side-filter { width: 100%; padding: 7px 12px 7px 34px; color: var(--ink); background: var(--surface-soft); border: 1px solid var(--line); border-radius: 9px; outline: none; font-size: 13.5px; }
#q:focus, #side-filter:focus { background: var(--surface); border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
.search kbd { position: absolute; right: 9px; top: 50%; transform: translateY(-50%); color: var(--muted); font: 11px/1.5 ui-monospace, monospace; background: var(--surface); border: 1px solid var(--line); border-radius: 5px; padding: 0 6px; pointer-events: none; }
#q:not(:placeholder-shown) + kbd, #q:focus + kbd { display: none; }
#hits { position: absolute; z-index: 40; right: 0; top: calc(100% + 7px); width: min(460px, 92vw); max-height: 65vh; overflow-y: auto; margin: 0; padding: 6px; list-style: none; background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius); box-shadow: 0 18px 55px rgb(0 0 0 / .17); }
#hits:empty { display: none; }
#hits a { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding: 8px 11px; border-radius: 7px; color: var(--ink); text-decoration: none; }
#hits a:hover, #hits [aria-selected="true"] a { background: var(--accent-soft); }
#hits small { flex: none; color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
#hits .none { padding: 8px 11px; color: var(--muted); }
.layout { display: grid; grid-template-columns: var(--side-width) minmax(0, 1fr) 230px; align-items: start; min-height: calc(100vh - var(--header-height)); }
/* Sidebar: a panel docked to the left edge with its own scroll area, so the tree never scrolls away with the page. */
.side { position: sticky; top: var(--header-height); display: flex; flex-direction: column; height: calc(100vh - var(--header-height)); background: var(--surface); border-right: 1px solid var(--line); }
.side-top { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 16px 16px 10px; color: var(--ink); font-size: 12px; font-weight: 750; text-transform: uppercase; letter-spacing: .08em; }
.side-actions { display: flex; align-items: center; gap: 2px; }
.text-button { padding: 3px 7px; color: var(--muted); background: none; border: 0; border-radius: 6px; font-size: 11.5px; font-weight: 600; letter-spacing: 0; text-transform: none; }
.text-button:hover { color: var(--accent); background: var(--hover); }
.filter-wrap { position: relative; margin: 0 16px 8px; }
.side-empty { margin: 8px 16px; color: var(--muted); font-size: 13px; }
.side-scroll { flex: 1; min-height: 0; overflow-y: auto; padding: 4px 10px 28px; scrollbar-width: thin; scrollbar-color: var(--line) transparent; overscroll-behavior: contain; }
.nav-group { margin: 0 0 6px; }
.nav-group > summary { display: flex; align-items: center; gap: 8px; padding: 8px 8px; color: var(--muted); font-size: 11px; line-height: 1.4; font-weight: 750; text-transform: uppercase; letter-spacing: .09em; list-style: none; cursor: pointer; border-radius: 7px; user-select: none; }
.nav-group > summary::-webkit-details-marker { display: none; }
.nav-group > summary::after { content: ""; width: 6px; height: 6px; margin: 0 4px 0 6px; border: solid currentColor; border-width: 0 1.5px 1.5px 0; transform: rotate(-45deg); transition: transform .15s; }
.nav-group[open] > summary::after { transform: rotate(45deg); }
.nav-group > summary:hover { color: var(--ink); background: var(--hover); }
.nav-group > summary .ico { width: 14px; height: 14px; }
.nav-group > summary .count { margin-left: auto; }
.side ul, .toc ul { margin: 0; padding: 0; list-style: none; }
.tree { padding-bottom: 6px; }
.tree ul { margin: 1px 0 2px 16px; padding-left: 6px; border-left: 1px solid var(--guide); }
.node > ul { display: none; }
.node.open > ul, .side.filtering .node.filter-open > ul { display: block; }
.row { position: relative; display: flex; align-items: center; min-height: 32px; border-radius: 7px; }
.row:hover { background: var(--hover); }
.row.current { background: var(--accent-soft); }
.row.current::before { content: ""; position: absolute; left: -7px; top: 6px; bottom: 6px; width: 3px; background: var(--accent); border-radius: 2px; }
.tree > .node > .row.current::before { left: -4px; }
.twisty, .twisty-space { flex: none; width: 22px; height: 26px; }
.twisty { display: inline-flex; align-items: center; justify-content: center; padding: 0; color: var(--faint); background: none; border: 0; border-radius: 5px; }
.twisty:hover { color: var(--ink); }
.twisty .ico { width: 14px; height: 14px; transition: transform .15s; }
.twisty[aria-expanded="true"] .ico { transform: rotate(90deg); }
.link { display: flex; flex: 1; align-items: center; gap: 8px; min-width: 0; padding: 5px 8px 5px 2px; color: var(--text); background: none; border: 0; font-size: 13.5px; line-height: 1.4; text-align: left; text-decoration: none; }
.link:hover { color: var(--ink); }
.link[aria-current] { color: var(--accent); font-weight: 650; }
.link .ico { color: var(--faint); }
.link .kind-solution { color: var(--solution); }
.link .kind-project { color: var(--project); }
.link .kind-module { color: var(--module); }
.link .kind-view { color: var(--view); }
.link[aria-current] .ico { color: var(--accent); }
.label { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.folder-toggle { color: var(--muted); font-weight: 600; }
.side.filtering .node.filter-hit > .row .label { color: var(--ink); font-weight: 650; }
.badge { flex: none; padding: 0 6px; color: var(--muted); background: var(--hover); border-radius: 5px; font-size: 10px; font-weight: 700; line-height: 17px; letter-spacing: .04em; text-transform: uppercase; }
.badge-app { color: var(--badge-app-ink); background: var(--badge-app); }
.badge-test { color: var(--badge-test-ink); background: var(--badge-test); }
.count { flex: none; min-width: 20px; padding: 0 6px; color: var(--faint); background: var(--surface-soft); border: 1px solid var(--line); border-radius: 10px; font-size: 10.5px; font-weight: 650; line-height: 16px; letter-spacing: 0; text-align: center; }
.backdrop { position: fixed; inset: 0; z-index: 44; background: rgb(10 16 25 / .45); }
main { max-width: min(1060px, calc(100% - 2 * clamp(16px, 2.5vw, 36px))); min-width: 0; margin: 28px auto 64px; background: var(--surface); border: 1px solid var(--line); border-radius: 14px; box-shadow: var(--shadow); padding: clamp(22px, 3.5vw, 52px); overflow-wrap: anywhere; }
.breadcrumbs { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; margin-bottom: 22px; color: var(--muted); font-size: 12.5px; }
.breadcrumbs a { color: var(--muted); text-decoration: none; }
.breadcrumbs a:hover { color: var(--accent); text-decoration: underline; }
.breadcrumbs .sep { color: var(--faint); font-size: 14px; }
.breadcrumbs > span:last-child:not(.sep) { color: var(--ink); font-weight: 600; }
.report-label { display: inline-block; margin-bottom: 8px; color: var(--accent); font-size: 11px; font-weight: 800; letter-spacing: .12em; text-transform: uppercase; }
main > h1 { margin: 0 0 10px; max-width: 28ch; color: var(--ink); font-size: clamp(28px, 3vw, 39px); font-weight: 760; line-height: 1.16; letter-spacing: -.045em; overflow-wrap: anywhere; }
main > h1 + p { color: var(--muted); font-size: 14px; margin: 0 0 28px; }
main p, main li { max-width: 82ch; }
main p { margin: 12px 0; }
main h2, main h3, main h4 { color: var(--ink); letter-spacing: -.025em; scroll-margin-top: 105px; }
main h2 { margin: 44px 0 15px; padding-bottom: 11px; border-bottom: 1px solid var(--line); font-size: 22px; line-height: 1.3; }
main h3 { margin: 29px 0 11px; font-size: 17px; line-height: 1.35; }
main h4 { margin: 20px 0 8px; font-size: 15px; }
main ul, main ol { padding-left: 24px; }
main li { margin: 5px 0; }
main hr { border: 0; border-top: 1px solid var(--line); margin: 30px 0; }
main blockquote { margin: 20px 0; padding: 12px 20px; border-left: 4px solid var(--accent); background: var(--surface-soft); border-radius: 0 8px 8px 0; }
main blockquote p { margin: 0; }
main code { font: 13px/1.55 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; padding: 2px 5px; background: var(--code); border-radius: 4px; }
main pre { position: relative; margin: 18px 0; padding: 18px; overflow-x: auto; color: var(--ink); background: var(--surface-soft); border: 1px solid var(--line); border-radius: 10px; line-height: 1.55; }
main pre code { padding: 0; background: none; }
main pre.mermaid { background: transparent; border-color: var(--line); text-align: center; }
main img { display: block; max-width: 100%; height: auto; border: 1px solid var(--line); border-radius: 8px; }
/* Wide tables scroll inside .table-wrap; cells wrap at word boundaries, not inside identifiers (main's overflow-wrap). */
.table-wrap { max-width: 100%; overflow-x: auto; margin: 18px 0 26px; border: 1px solid var(--line); border-radius: 10px; }
main table { width: 100%; border-spacing: 0; border-collapse: separate; font-size: 13px; }
main th, main td { padding: 10px 14px; vertical-align: top; text-align: left; border-bottom: 1px solid var(--line); overflow-wrap: normal; }
main th { position: sticky; top: 0; color: var(--muted); background: var(--surface-soft); font-size: 11px; letter-spacing: .045em; text-transform: uppercase; white-space: nowrap; }
main thead:not(:has(th:not(:empty))) { display: none; }
main tbody tr:nth-child(even) { background: var(--stripe); }
main tbody tr:hover { background: var(--accent-soft); }
main tbody tr:last-child td { border-bottom: 0; }
.generator { margin-top: 48px; padding-top: 18px; border-top: 1px solid var(--line); color: var(--muted); font-size: 12px; }
.pager { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; margin-top: 52px; }
.pager a { display: flex; flex-direction: column; gap: 2px; padding: 14px 18px; color: var(--ink); border: 1px solid var(--line); border-radius: 10px; text-decoration: none; min-width: 0; }
.pager a:hover { border-color: var(--accent); background: var(--accent-soft); }
.pager a span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 650; color: var(--accent); }
.pager small { color: var(--muted); font-size: 12px; }
.pager .next { grid-column: 2; text-align: right; }
.toc { position: sticky; top: calc(var(--header-height) + 28px); max-height: calc(100vh - var(--header-height) - 56px); overflow-y: auto; padding: 0 20px 0 0; scrollbar-width: thin; }
.toc-inner { border-left: 1px solid var(--line); padding: 4px 0 4px 16px; }
.toc-title { margin-bottom: 10px; color: var(--ink); font-size: 11px; font-weight: 750; text-transform: uppercase; letter-spacing: .09em; }
.toc a { display: block; margin: 2px 0 2px -17px; padding: 4px 0 4px 15px; color: var(--muted); font-size: 12.5px; line-height: 1.45; border-left: 2px solid transparent; text-decoration: none; overflow-wrap: anywhere; }
.toc a:hover { color: var(--ink); }
.toc a[aria-current="location"] { color: var(--accent); border-left-color: var(--accent); font-weight: 600; }
.to-top { position: fixed; right: 22px; bottom: 22px; z-index: 20; display: inline-flex; align-items: center; justify-content: center; width: 42px; height: 42px; color: var(--accent); background: var(--surface); border: 1px solid var(--line); border-radius: 50%; box-shadow: var(--shadow); }
.to-top:hover { background: var(--accent-soft); }
.to-top .ico { width: 18px; height: 18px; }
@media (max-width: 1400px) {
  .layout { grid-template-columns: var(--side-width) minmax(0, 1fr); }
  .toc { display: none; }
}
@media (max-width: 960px) {
  header { gap: 10px; }
  .top-nav { display: none; }
  #nav-toggle { display: inline-flex; }
  .layout { display: block; }
  .side { display: none; position: fixed; z-index: 45; top: 0; bottom: 0; left: 0; height: 100%; width: min(88vw, 340px); box-shadow: 10px 0 30px rgb(0 0 0 / .18); }
  body[data-nav-open="true"] .side { display: flex; }
  body[data-nav-open="true"] { overflow: hidden; }
  #nav-close { display: inline-flex; }
  #nav-expand, #nav-collapse { display: none; }
  main { max-width: calc(100% - 32px); margin: 16px auto 48px; }
}
@media (max-width: 580px) {
  header { padding: 0 12px; }
  .brand-name { display: none; }
  .header-actions { gap: 6px; }
  .search { width: min(56vw, 240px); }
  .search kbd { display: none; }
  main { max-width: none; margin: 0 0 36px; border: 0; border-radius: 0; box-shadow: none; padding: 26px 16px; }
  .table-wrap { margin-left: -3px; margin-right: -3px; }
  .pager { grid-template-columns: 1fr; }
  .pager .next { grid-column: 1; }
}
@media (prefers-reduced-motion: reduce) {
  html { scroll-behavior: auto; }
  .twisty .ico, .nav-group > summary::after { transition: none; }
}
@media print {
  :root { color-scheme: light; --canvas: #fff; --surface: #fff; --ink: #000; --text: #222; --muted: #444; --line: #bbb; --code: #eee; --stripe: #fff; --accent: #000; --shadow: none; }
  header, .side, .toc, .skip, .reading-progress, .pager, .to-top, .backdrop { display: none !important; }
  .layout { display: block; }
  main { max-width: none; margin: 0; padding: 0; border: 0; box-shadow: none; }
  main pre { white-space: pre-wrap; }
  .table-wrap { overflow: visible; }
  h2, h3 { break-after: avoid; }
  pre, tr { break-inside: avoid; }
}
""";

    // Inline icon sprite (stroke icons, colored by CSS through currentColor), referenced as <use href="#i-name"/>.
    public const string Icons = """
<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" style="position:absolute" aria-hidden="true" focusable="false">
<symbol id="i-home" viewBox="0 0 24 24"><path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M10 21v-6h4v6"/></symbol>
<symbol id="i-book" viewBox="0 0 24 24"><path d="M4 19V5a2 2 0 0 1 2-2h14v14H6a2 2 0 0 0-2 2 2 2 0 0 0 2 2h14"/><path d="M8 7h8M8 11h5"/></symbol>
<symbol id="i-layers" viewBox="0 0 24 24"><path d="m12 3 9 5-9 5-9-5 9-5z"/><path d="m3 13 9 5 9-5"/></symbol>
<symbol id="i-api" viewBox="0 0 24 24"><path d="m8 8-4 4 4 4M16 8l4 4-4 4M14 5l-4 14"/></symbol>
<symbol id="i-monitor" viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></symbol>
<symbol id="i-check" viewBox="0 0 24 24"><path d="M12 3 4 6v6c0 4.5 3.4 8 8 9 4.6-1 8-4.5 8-9V6l-8-3z"/><path d="m9 12 2 2 4-4"/></symbol>
<symbol id="i-eye" viewBox="0 0 24 24"><path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></symbol>
<symbol id="i-view" viewBox="0 0 24 24"><path d="M9 4 3 6v14l6-2 6 2 6-2V4l-6 2-6-2zM9 4v14M15 6v14"/></symbol>
<symbol id="i-box" viewBox="0 0 24 24"><path d="M21 8 12 3 3 8v8l9 5 9-5V8z"/><path d="m3 8 9 5 9-5M12 13v8"/></symbol>
<symbol id="i-solution" viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/></symbol>
<symbol id="i-project" viewBox="0 0 24 24"><path d="M21 8 12 3 3 8v8l9 5 9-5V8z"/><path d="m3 8 9 5 9-5M12 13v8"/></symbol>
<symbol id="i-module" viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z"/></symbol>
<symbol id="i-folder" viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z"/></symbol>
<symbol id="i-page" viewBox="0 0 24 24"><path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9l-6-6z"/><path d="M14 3v6h6"/></symbol>
<symbol id="i-chevron" viewBox="0 0 24 24"><path d="m9 6 6 6-6 6"/></symbol>
<symbol id="i-search" viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></symbol>
<symbol id="i-filter" viewBox="0 0 24 24"><path d="M4 5h16l-6 8v6l-4-2v-4L4 5z"/></symbol>
<symbol id="i-menu" viewBox="0 0 24 24"><path d="M4 6h16M4 12h16M4 18h16"/></symbol>
<symbol id="i-close" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></symbol>
<symbol id="i-moon" viewBox="0 0 24 24"><path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/></symbol>
<symbol id="i-sun" viewBox="0 0 24 24"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></symbol>
<symbol id="i-up" viewBox="0 0 24 24"><path d="M12 19V5M5 12l7-7 7 7"/></symbol>
</svg>
""";

    public const string Script = """
(() => {
  const root = document.documentElement;
  const byId = id => document.getElementById(id);
  const themeButton = byId('theme-toggle'), navButton = byId('nav-toggle'), closeButton = byId('nav-close');
  const side = document.querySelector('.side'), scroller = byId('side-scroll'), filter = byId('side-filter');
  const backdrop = byId('nav-backdrop'), progress = byId('reading-progress'), toTop = byId('to-top');
  // file:// pages may refuse storage; every feature that uses it degrades to the generated defaults.
  const storage = kind => { try { return window[kind]; } catch (_) { return null; } };
  const local = storage('localStorage'), session = storage('sessionStorage');
  const read = (store, key, fallback) => { try { const v = store && store.getItem(key); return v == null ? fallback : JSON.parse(v); } catch (_) { return fallback; } };
  const write = (store, key, value) => { try { if (store) store.setItem(key, JSON.stringify(value)); } catch (_) { /* optional */ } };

  // Theme: an explicit choice wins over the system setting and is remembered across pages.
  const themeKey = 'docwizz.report.theme';
  const isDark = () => getComputedStyle(root).colorScheme.includes('dark');
  function updateThemeLabel() {
    const dark = isDark();
    themeButton.querySelector('use').setAttribute('href', dark ? '#i-sun' : '#i-moon');
    themeButton.setAttribute('aria-label', dark ? 'Switch to light mode' : 'Switch to dark mode');
    themeButton.title = themeButton.getAttribute('aria-label');
  }
  themeButton.addEventListener('click', () => {
    root.dataset.theme = isDark() ? 'light' : 'dark';
    try { if (local) local.setItem(themeKey, root.dataset.theme); } catch (_) { /* optional */ }
    updateThemeLabel();
    document.dispatchEvent(new Event('docwizz:theme'));
  });
  window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', () => {
    updateThemeLabel();
    if (!root.dataset.theme) document.dispatchEvent(new Event('docwizz:theme'));
  });
  updateThemeLabel();

  // Mobile drawer.
  function toggleNav(open) {
    document.body.dataset.navOpen = String(open);
    backdrop.hidden = !open;
    navButton.setAttribute('aria-expanded', String(open));
    if (open) { closeButton.focus(); } else if (side.contains(document.activeElement)) { navButton.focus(); }
  }
  navButton.addEventListener('click', () => toggleNav(document.body.dataset.navOpen !== 'true'));
  closeButton.addEventListener('click', () => toggleNav(false));
  backdrop.addEventListener('click', () => toggleNav(false));
  side.addEventListener('click', e => { if (e.target.closest('a')) toggleNav(false); });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && document.body.dataset.navOpen === 'true') toggleNav(false);
  });

  // Tree: what the reader opened or closed stays that way on the next page; the path to this page is always open.
  const openKey = 'docwizz.nav.open', scrollKey = 'docwizz.nav.scroll';
  const remembered = read(session, openKey, {});
  const nodes = Array.from(side.querySelectorAll('li.node')).filter(li => li.querySelector(':scope > ul'));
  const groups = Array.from(side.querySelectorAll('details.nav-group'));
  const holdsCurrent = el => el.querySelector('[aria-current="page"]') !== null;
  function setOpen(li, open) {
    li.classList.toggle('open', open);
    li.querySelector(':scope > .row > .twisty')?.setAttribute('aria-expanded', String(open));
  }
  function remember(key, open) { remembered[key] = open; write(session, openKey, remembered); }
  nodes.forEach(li => { if (li.dataset.key in remembered && !li.classList.contains('open')) setOpen(li, remembered[li.dataset.key]); });
  groups.forEach(g => { if (g.dataset.key in remembered && !holdsCurrent(g)) g.open = remembered[g.dataset.key]; });
  side.addEventListener('click', e => {
    const toggle = e.target.closest('.twisty, .folder-toggle');
    if (toggle) {
      const li = toggle.closest('li.node');
      const open = !li.classList.contains('open');
      setOpen(li, open);
      remember(li.dataset.key, open);
      return;
    }
    const summary = e.target.closest('.nav-group > summary');
    if (summary && !side.classList.contains('filtering')) remember(summary.parentElement.dataset.key, !summary.parentElement.open);
  });
  function setAll(open) {
    nodes.forEach(li => { const keep = !open && holdsCurrent(li); setOpen(li, open || keep); remember(li.dataset.key, open || keep); });
    groups.forEach(g => { g.open = true; remember(g.dataset.key, true); });
  }
  byId('nav-expand').addEventListener('click', () => setAll(true));
  byId('nav-collapse').addEventListener('click', () => setAll(false));

  // Keep the sidebar where the reader left it; bring this page's entry into view when it is off-screen.
  scroller.scrollTop = read(session, scrollKey, 0);
  const current = scroller.querySelector('[aria-current="page"]');
  if (current) {
    const box = scroller.getBoundingClientRect(), at = current.getBoundingClientRect();
    if (at.top < box.top || at.bottom > box.bottom) current.scrollIntoView({ block: 'center' });
  }
  window.addEventListener('pagehide', () => write(session, scrollKey, scroller.scrollTop));

  // Filter: a match shows its ancestors (opened) and its own subtree.
  function filterTree() {
    const value = filter.value.trim().toLowerCase();
    side.classList.toggle('filtering', value.length > 0);
    let shown = 0;
    const walk = (li, inMatch) => {
      const own = value.length > 0 && li.dataset.match.includes(value);
      let below = false;
      li.querySelectorAll(':scope > ul > li.node').forEach(child => { if (walk(child, inMatch || own)) below = true; });
      li.hidden = value.length > 0 && !own && !below && !inMatch;
      li.classList.toggle('filter-open', below);
      li.classList.toggle('filter-hit', own);
      if (own) shown++;
      return own || below;
    };
    groups.forEach(g => {
      let any = false;
      g.querySelectorAll(':scope > ul > li.node').forEach(li => { if (walk(li, false)) any = true; });
      g.hidden = value.length > 0 && !any;
      if (value) {
        if (!('wasOpen' in g.dataset)) g.dataset.wasOpen = String(g.open);
        g.open = true;
      } else if ('wasOpen' in g.dataset) {
        g.open = g.dataset.wasOpen === 'true';
        delete g.dataset.wasOpen;
      }
    });
    byId('side-empty').hidden = !value || shown !== 0;
  }
  filter.addEventListener('input', filterTree);
  filter.addEventListener('keydown', e => {
    if (e.key === 'Escape' && filter.value) { e.stopPropagation(); filter.value = ''; filterTree(); }
    else if (e.key === 'Enter') { side.querySelector('.filter-hit:not([hidden]) > .row a')?.click(); }
  });

  // Reading progress, the section being read in "On this page", and the back-to-top button.
  const sections = Array.from(document.querySelectorAll('main h2[id]'));
  const tocLinks = Array.from(document.querySelectorAll('.toc a[href^="#"]'));
  toTop.addEventListener('click', () => window.scrollTo({ top: 0 }));
  const onScroll = () => {
    const max = document.documentElement.scrollHeight - window.innerHeight;
    progress.style.width = (max <= 0 ? 100 : Math.min(100, Math.max(0, window.scrollY / max * 100))) + '%';
    toTop.hidden = window.scrollY < 600;
    let currentSection = '';
    for (const heading of sections) {
      if (heading.getBoundingClientRect().top <= 150) currentSection = heading.id;
      else break;
    }
    tocLinks.forEach(a => {
      if (currentSection && a.getAttribute('href') === '#' + currentSection) a.setAttribute('aria-current', 'location');
      else a.removeAttribute('aria-current');
    });
  };
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onScroll);
  onScroll();
})();
""";
}
