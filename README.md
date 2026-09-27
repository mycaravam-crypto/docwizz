# docwizz
Small tool, creates documentation. Finds the code that *needs* docs and doesn't have them.

```bash
docwizz scan <dir> [model.json]   # write the code model (nodes + edges)
docwizz analyze <dir>             # documentation report
docwizz check <dir>               # report, exit 1 if thresholds fail (CI)
docwizz generate <dir> [out]      # Markdown + Mermaid docs (default <dir>/docs)
docwizz diff <dir> [ref]          # changed symbols + affected doc pages (default baseline: docs/.docwizz/model.json)
docwizz check <dir> --since <ref> # CI: fail only on critical gaps / violations introduced since <ref>
./test.sh                         # smoke test against fixture/
```

Vue/TS support needs Node and a one-time `npm ci` in [scanner-vue/](scanner-vue/) (C# works without it).
HTTP calls (`fetch`, `axios`) are linked to the matching backend endpoint.

Config: `docwizz.yaml` in the scanned dir (or cwd). Without one, the built-in default in
[src/DocWizz/Analyzer.cs](src/DocWizz/Analyzer.cs) (`Config.Default`) applies. Copy it as a starting point.
Sections are XML doc tag names (`summary`, `param`, `returns`, `exception`, or any custom tag).
