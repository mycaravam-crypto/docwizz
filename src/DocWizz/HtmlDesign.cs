// Self-contained report theme: no fonts, stylesheets, or runtime dependencies needed for file:// pages.
// Keep design tokens and behavior separate from the Markdown/HTML renderer.
static class HtmlDesign
{
    public const string Css = """
:root {
  color-scheme: light;
  --canvas: #f3f5f8; --surface: #ffffff; --surface-soft: #f8fafc;
  --ink: #172334; --text: #344457; --muted: #5c6b7e;
  --line: #dce2eb; --accent: #235bbd; --accent-hover: #174a9c;
  --accent-soft: #e9f0fd; --code: #eef2f6; --stripe: #f8fafc;
  --shadow: 0 2px 5px rgb(18 33 55 / .04), 0 14px 44px rgb(18 33 55 / .045);
  --radius: 12px; --header-height: 68px;
}
:root[data-theme="dark"] {
  color-scheme: dark; --canvas: #101620; --surface: #192331; --surface-soft: #202d3d;
  --ink: #f1f5fb; --text: #d2ddeb; --muted: #a3b3c6; --line: #35465a;
  --accent: #8eb7ff; --accent-hover: #c2d6ff; --accent-soft: #253b59;
  --code: #253344; --stripe: #202d3d;
  --shadow: 0 12px 42px rgb(0 0 0 / .16);
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark; --canvas: #101620; --surface: #192331; --surface-soft: #202d3d;
    --ink: #f1f5fb; --text: #d2ddeb; --muted: #a3b3c6; --line: #35465a;
    --accent: #8eb7ff; --accent-hover: #c2d6ff; --accent-soft: #253b59;
    --code: #253344; --stripe: #202d3d;
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
:focus-visible { outline: 3px solid var(--accent); outline-offset: 3px; }
[hidden] { display: none !important; }
.skip { position: fixed; left: 12px; top: -100px; z-index: 60; padding: 10px 16px; background: var(--surface); color: var(--ink); border-radius: 8px; }
.skip:focus { top: 12px; }
.reading-progress { position: fixed; z-index: 50; left: 0; top: 0; width: 0; height: 3px; background: var(--accent); pointer-events: none; }
header { position: sticky; top: 0; z-index: 30; display: flex; align-items: center; gap: 18px; min-height: var(--header-height); padding: 9px clamp(16px, 2.5vw, 36px); background: var(--surface); border-bottom: 1px solid var(--line); }
header .brand { display: inline-flex; align-items: center; gap: 11px; flex: none; color: var(--ink); font-size: 18px; font-weight: 750; letter-spacing: -.04em; text-decoration: none; }
.brand-mark { display: grid; place-items: center; width: 34px; height: 34px; color: white; background: #285bb0; border-radius: 10px; font-size: 13px; font-weight: 800; letter-spacing: -.06em; }
header .brand span:not(.brand-mark) { color: var(--accent); }
header nav { display: flex; align-items: center; gap: 3px; min-width: 0; }
header nav a { display: block; flex: none; padding: 8px 11px; border-radius: 8px; color: var(--muted); font-size: 13px; font-weight: 600; text-decoration: none; }
header nav a:hover, header nav a[aria-current] { color: var(--accent); background: var(--accent-soft); }
.header-actions { display: flex; align-items: center; gap: 9px; margin-left: auto; }
.icon-button { flex: none; display: inline-flex; align-items: center; justify-content: center; width: 38px; height: 38px; color: var(--text); background: var(--surface); border: 1px solid var(--line); border-radius: 9px; }
.icon-button:hover { background: var(--surface-soft); color: var(--ink); }
#nav-toggle, #nav-close { display: none; }
.search { position: relative; width: clamp(180px, 18vw, 300px); }
#q, #side-filter { width: 100%; padding: 8px 12px 8px 34px; color: var(--ink); background: var(--surface-soft); border: 1px solid var(--line); border-radius: 9px; outline: none; }
#q { font-size: 13px; }
#q:focus, #side-filter:focus { border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
.search:before, .filter-wrap:before { content: "⌕"; position: absolute; top: 3px; left: 10px; color: var(--muted); font-size: 21px; pointer-events: none; }
.search kbd { position: absolute; right: 10px; top: 8px; color: var(--muted); font: 11px/1.5 ui-monospace, monospace; border: 1px solid var(--line); border-radius: 4px; padding: 1px 5px; pointer-events: none; }
#q:not(:placeholder-shown) + kbd { display: none; }
#hits { position: absolute; z-index: 40; right: 0; top: calc(100% + 7px); width: min(450px, 90vw); max-height: 65vh; overflow-y: auto; margin: 0; padding: 6px; list-style: none; background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius); box-shadow: 0 18px 55px rgb(0 0 0 / .17); }
#hits:empty { display: none; }
#hits a { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding: 8px 11px; border-radius: 7px; color: var(--ink); text-decoration: none; }
#hits a:hover, #hits [aria-selected="true"] a { background: var(--accent-soft); }
#hits small { flex: none; color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
#hits .none { padding: 8px 11px; color: var(--muted); }
.layout { display: grid; grid-template-columns: minmax(200px, 250px) minmax(0, 1fr) minmax(160px, 216px); gap: clamp(18px, 2vw, 32px); max-width: 1700px; margin: 0 auto; padding: 24px clamp(16px, 2vw, 36px) 64px; align-items: start; }
.side, .toc { position: sticky; top: calc(var(--header-height) + 24px); max-height: calc(100vh - var(--header-height) - 50px); overflow-y: auto; scrollbar-width: thin; }
.side { padding-right: 8px; }
.side-top { display: flex; align-items: center; justify-content: space-between; margin: 0 0 15px; color: var(--ink); font-size: 12px; font-weight: 750; text-transform: uppercase; letter-spacing: .10em; }
.filter-wrap { position: relative; margin-bottom: 16px; }
#side-filter { font-size: 13px; }
.side details { margin: 0 0 10px; }
.side summary { cursor: pointer; padding: 10px 12px; color: var(--muted); font-size: 11px; line-height: 1.4; font-weight: 800; text-transform: uppercase; letter-spacing: .10em; border-radius: 7px; }
.side summary:hover { color: var(--ink); background: var(--surface-soft); }
.side ul, .toc ul { margin: 0; padding: 0; list-style: none; }
.side li { margin: 1px 0; }
.side a { display: block; padding: 7px 12px; color: var(--text); font-size: 13px; line-height: 1.45; text-decoration: none; border-left: 3px solid transparent; border-radius: 0 8px 8px 0; overflow-wrap: anywhere; }
.side a:hover { background: var(--surface-soft); color: var(--ink); }
.side a[aria-current] { background: var(--accent-soft); border-left-color: var(--accent); color: var(--accent); font-weight: 700; }
.side-empty { padding: 8px 12px; color: var(--muted); font-size: 13px; }
main { min-width: 0; background: var(--surface); border: 1px solid var(--line); border-radius: 14px; box-shadow: var(--shadow); padding: clamp(22px, 3.5vw, 54px); overflow-wrap: anywhere; }
.breadcrumbs { display: flex; align-items: center; gap: 8px; margin-bottom: 22px; color: var(--muted); font-size: 12px; }
.breadcrumbs a { color: var(--muted); text-decoration: none; }
.breadcrumbs a:hover { color: var(--accent); }
.breadcrumbs span:last-child { color: var(--text); font-weight: 600; }
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
main table { display: block; max-width: 100%; overflow-x: auto; border-spacing: 0; border-collapse: separate; margin: 18px 0 26px; font-size: 13px; border: 1px solid var(--line); border-radius: 10px; }
main th, main td { padding: 10px 14px; vertical-align: top; text-align: left; border-bottom: 1px solid var(--line); }
main th { position: sticky; top: 0; color: var(--muted); background: var(--surface-soft); font-size: 11px; letter-spacing: .045em; text-transform: uppercase; white-space: nowrap; }
main thead:not(:has(th:not(:empty))) { display: none; }
main tbody tr:nth-child(even) { background: var(--stripe); }
main tbody tr:hover { background: var(--accent-soft); }
main tbody tr:last-child td { border-bottom: 0; }
.generator { margin-top: 48px; padding-top: 18px; border-top: 1px solid var(--line); color: var(--muted); font-size: 12px; }
.toc-inner { border-left: 1px solid var(--line); padding: 8px 0 8px 18px; }
.toc-title { margin-bottom: 13px; color: var(--muted); font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: .10em; }
.toc a { display: block; margin: 5px 0; padding: 5px 0 5px 12px; color: var(--muted); font-size: 12px; line-height: 1.45; border-left: 2px solid transparent; text-decoration: none; overflow-wrap: anywhere; }
.toc a:hover, .toc a[aria-current="location"] { margin-left: -1px; color: var(--accent); border-left: 3px solid var(--accent); }
@media (max-width: 1350px) {
  .layout { grid-template-columns: 240px minmax(0, 1fr); max-width: 1200px; }
  .toc { display: none; }
}
@media (max-width: 900px) {
  header { gap: 10px; }
  header nav { display: none; }
  #nav-toggle { display: inline-flex; }
  .layout { display: block; padding: 16px 16px 48px; }
  .side { display: none; position: fixed; z-index: 45; top: var(--header-height); bottom: 0; left: 0; width: min(85vw, 340px); max-height: none; padding: 20px; background: var(--surface); border-right: 1px solid var(--line); box-shadow: 10px 0 24px rgb(0 0 0 / .1); }
  body[data-nav-open="true"] .side { display: block; }
  #nav-close { display: inline-flex; }
  main { width: 100%; }
}
@media (max-width: 580px) {
  header { padding: 10px 13px; }
  .brand-name { display: none; }
  .header-actions { gap: 6px; }
  .search { width: min(47vw, 230px); }
  .search kbd { display: none; }
  .layout { padding: 0 0 36px; }
  main { border: 0; border-radius: 0; box-shadow: none; padding: 28px 18px; }
  main table { margin-left: -3px; margin-right: -3px; }
}
@media (prefers-reduced-motion: reduce) {
  html { scroll-behavior: auto; }
}
@media print {
  :root { color-scheme: light; --canvas: #fff; --surface: #fff; --ink: #000; --text: #222; --muted: #444; --line: #bbb; --code: #eee; --stripe: #fff; --accent: #000; --shadow: none; }
  header, .side, .toc, .skip, .reading-progress { display: none !important; }
  .layout { display: block; max-width: none; margin: 0; padding: 0; }
  main { padding: 0; border: 0; box-shadow: none; }
  main pre { white-space: pre-wrap; }
  h2, h3 { break-after: avoid; }
  pre, tr { break-inside: avoid; }
}
""";

    public const string Script = """
(() => {
  const root = document.documentElement;
  const button = document.getElementById('theme-toggle');
  const navButton = document.getElementById('nav-toggle');
  const closeButton = document.getElementById('nav-close');
  const side = document.querySelector('.side');
  const filter = document.getElementById('side-filter');
  const progress = document.getElementById('reading-progress');
  const themeKey = 'docwizz.report.theme';
  let saved = null;
  try { saved = localStorage.getItem(themeKey); } catch (_) { /* file:// can restrict storage */ }
  if (saved === 'light' || saved === 'dark') root.dataset.theme = saved;
  function updateThemeLabel() {
    const dark = root.dataset.theme === 'dark' ||
      (!root.dataset.theme && window.matchMedia('(prefers-color-scheme: dark)').matches);
    button.textContent = dark ? '☀' : '☾';
    button.setAttribute('aria-label', dark ? 'Switch to light mode' : 'Switch to dark mode');
    button.title = button.getAttribute('aria-label');
  }
  button.addEventListener('click', () => {
    const current = getComputedStyle(root).colorScheme.includes('dark') ? 'dark' : 'light';
    root.dataset.theme = current === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem(themeKey, root.dataset.theme); } catch (_) { /* optional */ }
    updateThemeLabel();
  });
  if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', updateThemeLabel);
  }
  updateThemeLabel();
  function toggleNav(open) {
    document.body.dataset.navOpen = String(open);
    navButton.setAttribute('aria-expanded', String(open));
    if (open) { closeButton.focus(); } else if (side.contains(document.activeElement)) { navButton.focus(); }
  }
  navButton.addEventListener('click', () => toggleNav(document.body.dataset.navOpen !== 'true'));
  closeButton.addEventListener('click', () => toggleNav(false));
  side.addEventListener('click', e => { if (e.target.closest('a')) toggleNav(false); });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && document.body.dataset.navOpen === 'true') toggleNav(false);
  });
  function filterLinks() {
    const value = filter.value.trim().toLowerCase();
    let shown = 0;
    side.querySelectorAll('details').forEach(group => {
      let matches = 0;
      group.querySelectorAll('li').forEach(li => {
        const match = li.textContent.toLowerCase().includes(value);
        li.hidden = !match;
        if (match) { matches++; shown++; }
      });
      group.hidden = value.length > 0 && matches === 0;
      if (value && matches > 0) group.open = true;
    });
    const empty = document.getElementById('side-empty');
    empty.hidden = !value || shown !== 0;
  }
  filter.addEventListener('input', filterLinks);
  const sections = Array.from(document.querySelectorAll('main h2[id]'));
  const tocLinks = Array.from(document.querySelectorAll('.toc a[href^="#"]'));
  const onScroll = () => {
    const max = document.documentElement.scrollHeight - window.innerHeight;
    progress.style.width = (max <= 0 ? 100 : Math.min(100, Math.max(0, window.scrollY / max * 100))) + '%';
    let current = '';
    for (const heading of sections) {
      if (heading.getBoundingClientRect().top <= 150) current = heading.id;
      else break;
    }
    tocLinks.forEach(a => {
      if (current && a.getAttribute('href') === '#' + current) a.setAttribute('aria-current', 'location');
      else a.removeAttribute('aria-current');
    });
  };
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onScroll);
  onScroll();
})();
""";
}
