using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;

// An exception that escapes a command is a bug or an environment problem (disk, permissions), never a finding: one
// line and exit 2, so CI can tell "docwizz broke" from "the gate failed" (1). DOCWIZZ_DEBUG=1 lets it through with the trace.
try { return await Dispatch(args); }
catch (ScopeException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (Exception e) when (Environment.GetEnvironmentVariable("DOCWIZZ_DEBUG") is not { Length: > 0 })
{
    Console.Error.WriteLine($"docwizz: error: {e.Message}");
    Console.Error.WriteLine("This is a problem running docwizz, not a finding about the repository. Set DOCWIZZ_DEBUG=1 for details.");
    return 2;
}

static async Task<int> Dispatch(string[] args)
{
    var request = Cli.Parse(args);
    var (cmd, opts, pos) = (request.Command, request.Options, request.Positional);
    if (request.Error is not null) return Usage(request.Error, cmd);
    if (cmd == "version" || opts.ContainsKey("version"))
    {
        Console.WriteLine(AppVersion.Line);
        return 0;
    }
    if (cmd == "help") return Help(pos.ElementAtOrDefault(1));
    if (opts.ContainsKey("help")) return Help(cmd);
    // `docwizz diff HEAD~1 HEAD`: no directory given, refs only.
    if (cmd == "diff" && pos.Count >= 2 && !Directory.Exists(pos[1])) pos.Insert(1, ".");
    // `docwizz product check <dir> <template.yaml>`: the subcommand comes before the directory (a folder named check: ./check).
    var productCheck = cmd == "product" && pos.ElementAtOrDefault(1) == "check";
    if (productCheck) pos.RemoveAt(1);
    // `docwizz requirements import|show|query <dir> ...`: the same, the subcommand first.
    string? requirementsAction = null;
    if (cmd == "requirements")
    {
        if (pos.ElementAtOrDefault(1) is not ("import" or "show" or "query")) return Usage("requirements needs import, show or query: docwizz requirements import <dir> <history.json> [<work-items>]", "requirements");
        requirementsAction = pos[1];
        pos.RemoveAt(1);
    }
    if (cmd == "tests")
    {
        if (pos.ElementAtOrDefault(1) != "import") return Usage("tests needs import", "tests");
        pos.RemoveAt(1);
    }
    if (cmd == "load")
    {
        if (pos.ElementAtOrDefault(1) != "import") return Usage("load needs import: docwizz load import <dir> <report.json>...", "load");
        pos.RemoveAt(1);
    }
    // Subcommand before repository dir: docwizz domain discover <dir> ...
    string? domainAction = null;
    if (cmd == "domain")
    {
        if (pos.ElementAtOrDefault(1) is not ("discover" or "areas"))
            return Usage("domain needs discover or areas: docwizz domain discover <dir> --seed <symbol|endpoint>", "domain");
        domainAction = pos[1];
        pos.RemoveAt(1);
    }
    var path = pos.ElementAtOrDefault(1) ?? ".";
    var json = opts.GetValueOrDefault("format") == "json";

    if (opts.ContainsKey("timings"))
    {
        Timings.Enabled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Timings.Report(Console.Error);
    }
    if (cmd is null) return Start();
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"not a directory: {path}");
        return 1;
    }
    if (cmd == "tests")
    {
        if (pos.ElementAtOrDefault(2) is not { } source)
            return Usage("tests requires import: docwizz tests import <dir> <snapshot.json>", "tests");
        var snapshot = AdoTestSnapshot.Load(Path.GetFullPath(source));
        var folder = Path.Combine(path, "docs", ".docwizz");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"ado-tests-{(snapshot.PlanIds.Length == 1 ? snapshot.PlanIds[0].ToString() : "all")}.json");
        File.WriteAllText(target, snapshot.Content.TrimEnd() + "\n");
        Console.WriteLine(AdoTestSnapshot.Summary(snapshot));
        Console.WriteLine($"Imported to {target}");
        return 0;
    }
    if (cmd == "init") return Init(path);
    if (cmd == "setup")
    {
        if (opts.ContainsKey("select-projects") && !SetupProjectSelection.Interactive)
        {
            Console.Error.WriteLine("setup --select-projects requires an interactive terminal (TTY); use --all-projects or edit scope: in docwizz.yaml for CI");
            return 1;
        }
        return await SetupCommand(path, opts.GetValueOrDefault("profile"), opts.ContainsKey("force"), opts.ContainsKey("ai"),
            opts.ContainsKey("html") || opts.ContainsKey("refresh-html-on-version-change"),
            opts.ContainsKey("refresh-html-on-version-change"), opts.ContainsKey("progress"),
            opts.ContainsKey("select-projects"), opts.ContainsKey("all-projects"), opts.ContainsKey("yes"), opts.ContainsKey("ai-ratings"));
    }
    Config config;
    try { config = Config.Load(path, opts.GetValueOrDefault("profile")); }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);   // names the file or profile and what is wrong with it
        return 1;
    }

    switch (cmd)
    {
        case "discover":
            var inventory = ProjectDiscovery.Scan(path, RepoFilesUnscoped(path, config));
            Console.WriteLine(json ? JsonSerializer.Serialize(inventory, JsonOptions()) : ProjectDiscovery.ConsoleReport(inventory));
            return 0;
        case "context":
            return ContextCommand(path, config, opts);
        case "domain" when domainAction == "areas":
            var areas = DomainAreas.Group(LoadModel(path, config).Item1);
            Console.Write(json ? DomainAreas.Json(areas) : DomainAreas.Text(areas));
            return 0;
        case "domain":
            return DomainCommand(path, config, opts, json, pos.ElementAtOrDefault(2));
        case "mcp":
            return await McpCommand(path, config, opts.ContainsKey("auto-rescan"));
        case "evidence-diff":
            return EvidenceDiffCommand(path, pos.ElementAtOrDefault(2), pos.ElementAtOrDefault(3), json);
        case "requirements":
            return requirementsAction switch
            {
                "import" => RequirementsImport(path, pos.ElementAtOrDefault(2), pos.ElementAtOrDefault(3), config, opts),
                "query" => RequirementsQuery(path, pos.ElementAtOrDefault(2), config),
                _ => RequirementsShow(path, config, opts, json),
            };
        case "load":
            return LoadImport(path, pos.Skip(2).ToList(), config);
        case "trace":
            return TraceCommand(path, pos.ElementAtOrDefault(2), json);
        case "product":
            return await ProductCommand(path, pos.ElementAtOrDefault(2), pos.ElementAtOrDefault(3), opts.GetValueOrDefault("context"), config, opts.ContainsKey("ai"), productCheck, json);
        case "sbom":
            return SbomCommand(path, pos.ElementAtOrDefault(2) ?? Path.Combine(path, "sbom.cdx.json"), config);
        case "scan":
            return Scan(path, pos.ElementAtOrDefault(2) ?? "model.json", config);
        case "analyze":
            return Analyze(path, config, enforce: false, json);
        case "check" when opts.GetValueOrDefault("since") is { } since:
            return DiffCommand(path, since, config, enforce: true, json: json);
        case "check":
            return Analyze(path, config, enforce: true, json);
        case "diff":
            return DiffCommand(path, pos.ElementAtOrDefault(2), config, enforce: false, head: pos.ElementAtOrDefault(3), json: json);
        case "architecture":
            return ArchitectureCommand(path, config, json);
        case "remediate":
            return Remediate(path, config, opts.GetValueOrDefault("package"), opts.GetValueOrDefault("to"), opts.ContainsKey("validate"), opts.GetValueOrDefault("since"), json);
        case "generate":
            var storageState = opts.GetValueOrDefault("storage-state");
            var stateFile = storageState is null ? null : Path.GetFullPath(Path.IsPathRooted(storageState)
                ? storageState : Path.Combine(path, storageState));
            if (stateFile is not null && !File.Exists(stateFile))
            {
                Console.Error.WriteLine("docwizz: --storage-state file not found");
                return 1;
            }
            var requirements = opts.ContainsKey("requirements") ? LoadRequirements(path) : null;
            if (opts.ContainsKey("requirements") && requirements is null) return 1;
            var report = await Generate(path, pos.ElementAtOrDefault(2) ?? Path.Combine(path, "docs"), config, opts.ContainsKey("ai"), opts.ContainsKey("html") || opts.ContainsKey("refresh-html-on-version-change"), refreshHtmlOnVersionChange: opts.ContainsKey("refresh-html-on-version-change"), progress: opts.ContainsKey("progress"), screenshotBaseUrl: opts.GetValueOrDefault("base-url"), screenshotStorageState: stateFile, screenshotDelayMs: int.Parse(opts.GetValueOrDefault("screenshot-delay") ?? "0"), requirements: requirements, aiRatings: opts.ContainsKey("ai-ratings"));
            // The first line stays the stable one-line result scripts match; the report follows it.
            Console.WriteLine(report.Line);
            Console.WriteLine();
            foreach (var l in report.Details) Console.WriteLine(l.Length == 0 ? "" : "  " + l);
            foreach (var l in GenerateEvidenceFeedback.Lines(path)) Console.WriteLine("  " + l);
            return 0;
        default:
            return Usage($"unknown command {cmd}");
    }
}

// Compare two explicit evidence snapshots without scanning source files.
static int EvidenceDiffCommand(string root, string? before, string? after, bool json)
{
    if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after))
        return Usage("evidence-diff needs two evidence catalog files", "evidence-diff");
    string Resolve(string file) => File.Exists(file) ? file : Path.Combine(root, file);
    try
    {
        var report = ProjectEvidenceImpact.Compare(
            ProjectEvidenceImport.Load(Resolve(before)), ProjectEvidenceImport.Load(Resolve(after)));
        if (json) Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions()));
        else
        {
            Console.WriteLine("Explicit evidence changes (not source-file freshness):");
            if (report.Evidence.Count == 0 && report.AddedLinks.Count == 0 && report.RemovedLinks.Count == 0)
                Console.WriteLine("No evidence changes.");
            foreach (var item in report.Evidence)
                Console.WriteLine($"{item.Id}: {item.Change}" +
                    (item.Fields.Length == 0 ? "" : $"; fields: {string.Join(", ", item.Fields)}") +
                    (item.RelatedIds.Length == 0 ? "" : $"; related: {string.Join(", ", item.RelatedIds)}") +
                    (item.AffectedTests.Length == 0 ? "" : $"; affected tests: {string.Join(", ", item.AffectedTests)}"));
            foreach (var link in report.AddedLinks) Console.WriteLine($"+ link {link.From} -> {link.To}");
            foreach (var link in report.RemovedLinks) Console.WriteLine($"- link {link.From} -> {link.To}");
        }
        return 0;
    }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }
    catch (IOException ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

// First iteration of domain discovery: offline graph traversal with opt-in historical requirement evidence.
// No model requests, Azure DevOps calls, Git changes or generated files.
static int DomainCommand(string root, Config config, Dictionary<string, string> opts, bool json, string? output)
{
    var seed = opts.GetValueOrDefault("seed");
    var workItem = opts.GetValueOrDefault("work-item");
    if ((seed is null) == (workItem is null))
        return Usage("domain discover needs exactly one of --seed <symbol|endpoint> or --work-item <id>", "domain");
    if (!int.TryParse(opts.GetValueOrDefault("depth") ?? $"{DomainCrawler.DefaultDepth}", out var depth)
        || depth is < 0 or > DomainCrawler.MaxDepth)
        return Usage($"--depth must be 0 to {DomainCrawler.MaxDepth}", "domain");
    if (!int.TryParse(opts.GetValueOrDefault("limit") ?? $"{DomainCrawler.DefaultLimit}", out var limit)
        || limit is < 1 or > DomainCrawler.MaxLimit)
        return Usage($"--limit must be 1 to {DomainCrawler.MaxLimit}", "domain");
    try
    {
        var file = Path.Combine(root, RequirementIndex.DefaultFile);
        var index = File.Exists(file) ? RequirementIndex.Load(file) : null;
        RequirementId? workItemId = workItem is null ? null : RequirementId.Parse(workItem, index?.Inputs.Repository);
        var (model, _, _) = LoadModel(root, config);
        var report = DomainCrawler.Discover(model, seed, workItemId, depth, limit, index,
            Git(root, "rev-parse HEAD")?.Trim(), root);
        if (index is not null && index.Inputs.Scope != ProjectScope.Fingerprint(config.Scope))
            report = report with { Warnings = [.. report.Warnings, "the imported requirement scope differs from the current project scope; reimport requirements"] };
        var md = opts.GetValueOrDefault("format") == "md";
        if (output is null)
        {
            Console.Write(json ? DomainCrawler.Json(report) : md ? DomainCrawler.Markdown(report) : DomainCrawler.Text(report));
            return 0;
        }
        // An explicit file: Markdown links each location relative to where the file is, and a file docwizz didn't write stays.
        var target = Path.GetFullPath(output);
        if (File.Exists(target) && !File.ReadAllText(target).Contains("Domain discovery", StringComparison.Ordinal)
            && !File.ReadAllText(target).Contains(DomainCrawler.Schema, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{output} exists and is not a docwizz domain report; refusing to overwrite");
            return 1;
        }
        var links = Path.GetRelativePath(Path.GetDirectoryName(target)!, Path.GetFullPath(root)).Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, json ? DomainCrawler.Json(report) : md ? DomainCrawler.Markdown(report, links) : DomainCrawler.Text(report));
        Console.WriteLine($"→ {output}");
        return 0;
    }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    catch (IOException e) { Console.Error.WriteLine(e.Message); return 1; }
}

// P2 traceability uses only explicit YAML links. No scan, inference or network calls.
static int TraceCommand(string root, string? name, bool json)
{
    if (string.IsNullOrWhiteSpace(name)) return Usage("trace needs an evidence YAML file", "trace");
    var file = File.Exists(name) ? name : Path.Combine(root, name);
    try
    {
        var report = TraceabilityReport.Build(ProjectEvidenceImport.Load(file), root);
        Console.Write(json ? TraceabilityReport.Json(report) : TraceabilityReport.Markdown(report));
        return 0;
    }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    catch (IOException e) { Console.Error.WriteLine($"{file}: {e.Message}"); return 1; }
}

// `requirements import`: the requirement index from exported history and work items (files only, no network). Only
// commits not mapped before are scanned (as of the commit); identical inputs write nothing.
static int RequirementsImport(string root, string? history, string? workItems, Config config, Dictionary<string, string> opts)
{
    if (string.IsNullOrWhiteSpace(history)) return Usage("requirements import needs a history file (gitwizz.history/v2, from gitwizz history export)", "requirements");
    if (opts.Keys.FirstOrDefault(o => o is "for" or "work-item" or "format") is { } o) return Usage($"--{o} applies to requirements show, not import", "requirements");
    string Resolve(string f) => File.Exists(f) || Directory.Exists(f) ? f : Path.Combine(root, f);
    var historyFile = Resolve(history);
    var target = Path.Combine(root, RequirementIndex.DefaultFile);
    try
    {
        if (!File.Exists(historyFile)) throw new ArgumentException($"{history}: no such file");
        RequirementIndex.Index index;
        // Mapping scans the code as of every new commit, so it is nearly all of the time.
        int initialReused = 0;
        using (var bar = CliProgress.ForConsole([new("read", 1, "Reading inputs"), new("map", 20, "Mapping commits"), new("write", 1, "Writing index")],
            opts.ContainsKey("progress")))
        {
            bar.Begin("read");
            var bytes = File.ReadAllBytes(historyFile);
            var items = RequirementIndex.ReadWorkItems(workItems is null ? null : Resolve(workItems));
            var scope = ProjectScope.Fingerprint(config.Scope);
            // The configured projects must exist now; at older commits they may not yet (ModelAt historical).
            if (config.Scope is not null) ProjectScope.Resolve(root, config.Scope, RepoFilesUnscoped(root, config));
            RequirementIndex.Index? previous = null;
            if (File.Exists(target))
            {
                try { previous = RequirementIndex.Load(target); }
                catch (ArgumentException) { }   // an older or broken index is rebuilt from scratch
            }
            if (previous?.Fingerprint == RequirementIndex.Fingerprint(bytes, items, scope))
            {
                bar.Dispose();
                Console.WriteLine($"unchanged: {RequirementIndex.DefaultFile} ({previous.Commits.Count} commits, {previous.WorkItems.Count} work items)");
                return 0;
            }
            // Resume only checkpoints produced from exactly the same history, work items and scan scope.
            var checkpointFile = target + ".checkpoint";
            if (File.Exists(checkpointFile))
            {
                try
                {
                    var saved = RequirementIndex.Load(checkpointFile);
                    if (saved.Fingerprint == RequirementIndex.Fingerprint(bytes, items, scope))
                    {
                        if (previous is not null && previous.Inputs.Scope == scope)
                        {
                            var seen = saved.Commits.Select(c => c.Sha).ToHashSet();
                            saved = saved with { Commits = [.. previous.Commits.Where(c => seen.Add(c.Sha)), .. saved.Commits] };
                        }
                        previous = saved;
                        Console.Error.WriteLine($"docwizz: resuming from {saved.Commits.Count} checkpointed/previous commits");
                    }
                }
                catch (Exception e) when (e is ArgumentException or IOException or System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($"docwizz: ignoring invalid requirements checkpoint: {e.Message}");
                }
            }
            bar.Advance("read", Plural(items.Count, "work item"));
            bar.Begin("map");
            var prefix = Git(root, "rev-parse --show-prefix")?.Trim() ?? "";
            string[] exts = [".cs", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".java", ".php"];
            // The bar includes reused commits and no-source commits from the outset, while
            // ETA uses only newly scanned commits (a resumed import must not skew the rate).
            var scanClock = Stopwatch.StartNew();
            int credited = 0;
            bool progressStarted = false;
            Action<int, int, string?> importProgress = (done, total, detail) =>
            {
                if (!progressStarted)
                {
                    credited = done;
                    var reusedMatch = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"^(\d+) reused");
                    if (reusedMatch.Success) initialReused = int.Parse(reusedMatch.Groups[1].Value);
                    scanClock.Restart();
                    progressStarted = true;
                }
                // Only newly attempted scans determine throughput. No-source and reused commits
                // are credited immediately; they must not inflate or dilute the ETA.
                var scannedNow = Math.Max(0, done - credited);
                var remainingMatch = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"(\d+) remaining");
                var scansRemaining = remainingMatch.Success ? int.Parse(remainingMatch.Groups[1].Value) : 0;
                var eta = scannedNow >= 5 && scansRemaining > 0
                    ? $" · ~{CliProgress.Duration(scanClock.Elapsed / scannedNow * scansRemaining)} left"
                    : scannedNow > 0 && scansRemaining > 0 ? " · estimating after 5 scans" : "";
                bar.Report(done, total, $"{done:N0}/{total:N0} commits{eta}{(detail is null ? "" : $" · {detail}")}");
            };
            index = RequirementIndex.Build(bytes, items, scope, prefix, (sha, paths) => ModelAt(root, sha, config, historical: true, changedFiles: paths),
                p => exts.Contains(Path.GetExtension(p)), previous, importProgress, partial =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(checkpointFile)!);
                    var tmp = checkpointFile + ".tmp";
                    File.WriteAllText(tmp, RequirementIndex.Serialize(partial));
                    File.Move(tmp, checkpointFile, overwrite: true);
                }, (_, changedPath) => ProjectScope.MightBelongToScope(config.Scope, changedPath));
            bar.Advance("map", Plural(index.Commits.Count(c => c.Mapping == "mapped"), "commit") + " mapped");
            bar.Begin("write");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target + ".tmp", RequirementIndex.Serialize(index));
            File.Move(target + ".tmp", target, overwrite: true);
            if (File.Exists(checkpointFile)) File.Delete(checkpointFile);
            bar.Advance("write");
        }
        var touched = index.Commits.SelectMany(c => c.Touches).Select(t => t.Symbol).Distinct().Count();
        Console.WriteLine($"→ {RequirementIndex.DefaultFile}: {index.Commits.Count} commits ({initialReused} reused, {index.Commits.Count(c => c.Mapping == "no-source")} no-source, {index.Commits.Count(c => c.Mapping == "mapped") - initialReused} newly mapped, {index.Commits.Count(c => c.Mapping == "missing")} missing), {touched} symbols touched, {index.PullRequests.Count} PRs, {index.WorkItems.Count} work items");
        foreach (var w in index.Warnings) Console.Error.WriteLine($"docwizz: ⚠ {w}");
        return 0;
    }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    catch (IOException e) { Console.Error.WriteLine(e.Message); return 1; }
}

// `load import`: validate powerwizz reports, keep them in the index and say which steps match an endpoint. Offline.
static int LoadImport(string root, List<string> reports, Config config)
{
    if (reports.Count == 0) return Usage("load import needs at least one powerwizz report (powerwizz.report/v1)", "load");
    var target = Path.Combine(root, LoadReports.DefaultFile);
    try
    {
        var read = reports.Select(r => LoadReports.Read(File.Exists(r) ? r : Path.Combine(root, r))).ToList();
        var index = LoadReports.Merge(File.Exists(target) ? LoadReports.Load(target) : null, read);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target + ".tmp", LoadReports.Serialize(index));
        File.Move(target + ".tmp", target, overwrite: true);
        var attached = LoadReports.Attach(LoadModel(root, config).Model, index, _ => null);
        Console.WriteLine($"→ {LoadReports.DefaultFile}: {index.Reports.Count} reports, {attached.Endpoints.Count} endpoints measured");
        foreach (var s in attached.Unmatched) Console.Error.WriteLine($"docwizz: no endpoint matches {s}");
        foreach (var s in attached.Ambiguous) Console.Error.WriteLine($"docwizz: ambiguous, not attached: {s}");
        return 0;
    }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    catch (IOException e) { Console.Error.WriteLine(e.Message); return 1; }
}

// `requirements show`: one symbol's requirements (or one work item's symbols) from the index and the current model.
// Read-only: no scan of history, no network.
static int RequirementsShow(string root, Config config, Dictionary<string, string> opts, bool json)
{
    var target = opts.GetValueOrDefault("for");
    var workItem = opts.GetValueOrDefault("work-item");
    if ((target is null) == (workItem is null)) return Usage("requirements show needs --for <symbol> or --work-item <id>", "requirements");
    var file = Path.Combine(root, RequirementIndex.DefaultFile);
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"no requirement index at {RequirementIndex.DefaultFile}; run docwizz requirements import {root} <history.json> [<work-items>] first");
        return 1;
    }
    RequirementIndex.Index index;
    try { index = RequirementIndex.Load(file); }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    if (index.Inputs.Scope != ProjectScope.Fingerprint(config.Scope))
        Console.Error.WriteLine("docwizz: the project scope changed since the index was imported; symbol ids may differ (run requirements import again)");
    var (model, _, _) = LoadModel(root, config);
    var head = Git(root, "rev-parse HEAD")?.Trim();
    if (workItem is not null)
    {
        RequirementId id;
        try { id = RequirementId.Parse(workItem, index.Inputs.Repository); }
        catch (ArgumentException e) { return Usage(e.Message, "requirements"); }
        var reverse = RequirementResolver.Symbols(index, id, model, head);
        if (json) Console.Write(RequirementResolver.Json(reverse));
        else
        {
            Console.WriteLine($"{id.Display}{(reverse.Item is { } w ? $" {w.Type}: {w.Title}" : " (content unavailable)")}");
            foreach (var s in reverse.Symbols)
                Console.WriteLine($"  {s.Id} [{s.Status}{(s.Current ? "" : ", not in the current code")}{(s.ChangedSince ? ", code changed since" : "")}]{(s.Location is { } l ? $" — {l}" : "")}");
            if (reverse.Symbols.Count == 0) Console.WriteLine("  no symbol in the index is linked to it");
            foreach (var warning in reverse.Warnings) Console.WriteLine($"⚠ {warning}");
        }
        return 0;
    }
    string symbolId;
    Node? node;
    if (AgentContext.Resolve(model, target!) is { Target.Primary: { } n }) (symbolId, node) = (n.Id, n);
    else if (index.Commits.SelectMany(c => c.Touches).FirstOrDefault(t => t.Symbol == target) is { } historical) (symbolId, node) = (historical.Symbol, null);
    else
    {
        var r = AgentContext.Resolve(model, target!);
        Console.Error.WriteLine(r.Target is { } t ? $"{target} is a {t.Kind}; requirements are per symbol (a method, a type)" : r.Error);
        if (r.Candidates.Count > 0) Console.Error.WriteLine("candidates:" + string.Concat(r.Candidates.Select(c => $"\n  {c}")));
        return 1;
    }
    var nodes = model.Nodes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
    var context = RequirementResolver.For(index, symbolId, node, head, nodes.Contains);
    Console.Write(json ? RequirementResolver.Json(context) : RequirementResolver.Text(context));
    return 0;
}

// `requirements query`: gitwizz's resolver protocol (RequirementQuery). Only the JSON answer goes to stdout; a query
// that can't be read exits 1 with the reason on stderr. A missing index is an answer (unavailable), not an error.
static int RequirementsQuery(string root, string? queryFile, Config config)
{
    if (string.IsNullOrWhiteSpace(queryFile)) return Usage("requirements query needs a query file (gitwizz.requirement-query/v1): docwizz requirements query <dir> <query.json>", "requirements");
    string text;
    try { text = File.ReadAllText(queryFile); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"the query file can't be read: {e.Message}"); return 1; }
    var (symbols, error) = RequirementQuery.Read(text);
    if (error is not null) { Console.Error.WriteLine(error); return 1; }
    var file = Path.Combine(root, RequirementIndex.DefaultFile);
    RequirementIndex.Index? index = null;
    if (File.Exists(file))
    {
        try { index = RequirementIndex.Load(file); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    }
    var nodes = index is null ? [] : LoadModel(root, config).Model.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
    var answer = RequirementQuery.Answer(index, symbols, id => nodes.GetValueOrDefault(id), Git(root, "rev-parse HEAD")?.Trim(),
        index is not null && index.Inputs.Scope != ProjectScope.Fingerprint(config.Scope));
    Console.Write(answer.ToJsonString() + "\n");
    return 0;
}

// A context target's requirement lines: per symbol only (a file or folder has no single history to show).
static List<AgentContext.Line> RequirementLinesFor(RequirementIndex.Index index, AgentContext.Target t, string? head, Config config, CodeModel model)
{
    if (t.Primary is not { } n) return [new("requirements are per symbol: use --for <symbol>", "history")];
    var context = RequirementResolver.For(index, n.Id, n, head, id => model.Nodes.Any(x => x.Id == id));
    return AgentContext.RequirementLines(context, BusinessContext.Select(context, config.Ai.Requirements));
}

// The requirement index for --requirements, or null after saying why there is none (the caller exits 1): an opt-in that
// silently did nothing would look like "no requirements".
static RequirementIndex.Index? LoadRequirements(string root)
{
    var file = Path.Combine(root, RequirementIndex.DefaultFile);
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"--requirements: no requirement index at {RequirementIndex.DefaultFile}; run docwizz requirements import {root} <history.json> [<work-items>] first");
        return null;
    }
    try { return RequirementIndex.Load(file); }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return null; }
}

// The imported index for the requirement pages, or null: none imported, or unreadable (said so; the pages then say none).
static RequirementIndex.Index? ImportedRequirements(string root)
{
    var file = Path.Combine(root, RequirementIndex.DefaultFile);
    if (!File.Exists(file)) return null;
    try { return RequirementIndex.Load(file); }
    catch (ArgumentException e) { Console.Error.WriteLine($"docwizz: requirement pages skipped: {e.Message}"); return null; }
}

// A context package for a coding agent, from the model the docs were generated from (docs/.docwizz/model.json), or from a
// fresh scan when there is none. No network, no build, no AI: only what is in the model and the AI cache.
static int ContextCommand(string root, Config config, Dictionary<string, string> opts)
{
    if (opts.GetValueOrDefault("for") is not { Length: > 0 } target)
        return Usage("context needs --for <symbol|file|folder|VERB /route>", "context");
    if (!int.TryParse(opts.GetValueOrDefault("hops") ?? $"{AgentContext.DefaultHops}", out var hops) || hops is < 1 or > AgentContext.MaxHops)
        return Usage($"--hops must be a number from 1 to {AgentContext.MaxHops}", "context");
    if (!int.TryParse(opts.GetValueOrDefault("budget") ?? $"{AgentContext.DefaultBudget}", out var budget) || budget < 100)
        return Usage("--budget must be a number of tokens, at least 100", "context");

    var (model, source, since) = LoadModel(root, config);
    var changed = source.StartsWith("docs/") ? BuildFiles(root, config).Count(f => File.GetLastWriteTimeUtc(f) > since) : 0;

    var resolved = AgentContext.Resolve(model, target);
    if (resolved.Target is not { } t)
    {
        Console.Error.WriteLine(resolved.Error);
        if (resolved.Candidates.Count > 0)
            Console.Error.WriteLine((resolved.Error!.Contains("ambiguous") ? "candidates:" : "closest:") + string.Concat(resolved.Candidates.Select(c => $"\n  {c}")));
        return 1;
    }
    var includeAi = opts.ContainsKey("include-ai");
    var findings = Analyzer.Analyze(model, config);
    var arch = Architecture.Check(model, config);
    var drafts = AiProse.Cached(model, Path.Combine(root, "docs", ".docwizz", "ai-cache.json"));
    var graph = new ContextBuilder(root, model);
    List<AgentContext.Line>? requirements = null;
    if (opts.ContainsKey("requirements"))
    {
        if (LoadRequirements(root) is not { } index) return 1;
        requirements = RequirementLinesFor(index, t, Git(root, "rev-parse HEAD")?.Trim(), config, model);
    }
    var blocks = AgentContext.Blocks(graph, config, t, hops, includeAi, findings, arch, drafts, requirements);
    var aiLeftOut = includeAi ? 0 : t.Scope.Count(n => drafts.ContainsKey(n.Id));
    var package = new AgentContext.Package(Path.GetFileName(Path.GetFullPath(root).TrimEnd('/', '\\')), model.Commit,
        new(source, Git(root, "rev-parse --short HEAD")?.Trim(), changed), t, budget, includeAi, aiLeftOut, blocks, [], requirements is not null);
    if (package.Stale) Console.Error.WriteLine("docwizz: the model is stale; run docwizz generate . to refresh it (the package says why)");
    Console.Write(AgentContext.Render(package, opts.GetValueOrDefault("format") == "json"));
    return 0;
}

// The model the docs were generated from (docs/.docwizz/model.json), else a fresh scan: the model, where it came from,
// and the time it reflects (when it was written, or now), for staleness checks.
static (CodeModel Model, string Source, DateTime Since) LoadModel(string root, Config config)
{
    var fingerprint = Path.Combine(root, "docs", ".docwizz", "model.json");
    if (!File.Exists(fingerprint)) return (BuildModel(root, config).Model, "a fresh scan (no docs/.docwizz/model.json)", DateTime.UtcNow);
    var written = File.GetLastWriteTimeUtc(fingerprint);
    var saved = JsonSerializer.Deserialize<CodeModel>(File.ReadAllText(fingerprint), JsonOptions())!;
    if (saved.ScopeFingerprint != ProjectScope.Fingerprint(config.Scope))
        return (BuildModel(root, config).Model, "a fresh scan (the project scope changed since generate)", DateTime.UtcNow);
    return (saved, $"docs/.docwizz/model.json (written {written:yyyy-MM-dd HH:mm} UTC)", written);
}

// `docwizz mcp`: the read-only MCP server on stdio. stdout carries only protocol messages.
static async Task<int> McpCommand(string root, Config config, bool autoRescan)
{
    Console.Error.WriteLine($"docwizz mcp: serving {Path.GetFullPath(root)} on stdio (read-only; Ctrl+D or closing stdin stops it)");
    var refs = new McpRefModelCache();
    var scope = ProjectScope.Fingerprint(config.Scope);
    CodeModel? RefModel(string gitRef) => refs.Get(gitRef, scope,
        r => GitRun(root, $"rev-parse --verify {r}^{{commit}}") is { Exit: 0 } ok ? ok.Out.Trim() : null,
        sha => ModelAt(root, sha, config))
        ?? ModelAt(root, gitRef, config); // Preserve git failure diagnostics for unresolved refs.
    string? Head() => Git(root, "rev-parse --short HEAD")?.Trim();
    int ChangedSince(DateTime time) => BuildFiles(root, config).Count(f => File.GetLastWriteTimeUtc(f) > time);
    // Fingerprints the same file list BuildModel reads (RepoFiles), not just source extensions.
    var working = new McpWorkingModelCache(Head, () => McpWorkingModelCache.Fingerprint(root, RepoFiles(root, config)),
        () => BuildModel(root, config).Model);
    var host = new McpServer.Host(root, config, autoRescan,
        Load: () => LoadModel(root, config),
        Scan: () => working.Get(force: true),
        Head: Head,
        ChangedSince: ChangedSince,
        Impact: gitRef => RefModel(gitRef) is { } before
            ? DiffJson(Diff.Compare(before, working.Get(), config), gitRef, null) : null,
        Check: since =>
        {
            var model = working.Get();
            if (since is null)
            {
                var findings = Analyzer.Analyze(model, config);
                var (failures, _) = CheckFailures(root, model, config, findings, Architecture.Check(model, config));
                return new { pass = failures.Count == 0, failures };
            }
            if (RefModel(since) is not { } before) return null;
            var result = Diff.Compare(before, model, config);
            var (ok, summary) = Gate(result, config);
            return DiffJson(result, since, new { pass = ok, summary });
        });
    return await new McpServer(host).Run(Console.In, Console.Out);
}

// The source files the scanners would read, for staleness checks.
static List<string> BuildFiles(string root, Config config)
{
    string[] exts = [".cs", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".java", ".php"];
    return [.. RepoFiles(root, config).Where(f => exts.Contains(Path.GetExtension(f)))];
}

// Product draft from evidence; with --ai, a local model words each section from it (validated, cached). Manual files are
// never overwritten.
static async Task<int> ProductCommand(string root, string? templateName, string? outName, string? contextName, Config config, bool ai = false, bool check = false, bool json = false)
{
    if (string.IsNullOrWhiteSpace(templateName))
        return Usage("product needs a template YAML path", "product");
    var templatePath = File.Exists(templateName) ? templateName : Path.Combine(root, templateName);
    ProductTemplate template;
    try { template = ProductTemplate.Load(templatePath); }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    catch (IOException e) { Console.Error.WriteLine($"{templatePath}: {e.Message}"); return 1; }
    ProjectContext? project = null;
    if (contextName is not null)
    {
        var contextPath = File.Exists(contextName) ? contextName : Path.Combine(root, contextName);
        try { project = ProjectContext.Load(contextPath); project.Validate(template); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch (IOException e) { Console.Error.WriteLine($"{contextPath}: {e.Message}"); return 1; }
    }
    var output = outName is null ? Path.Combine(root, "docs", "products", template.ProductId + ".md")
        : outName;
    var full = Path.GetFullPath(output);
    if (check)
    {
        if (ai) return Usage("product check makes no AI calls; drop --ai", "product");
        var findings = ProductCheck.Run(root, template, BuildModel(root, config).Model, project, full);
        var errors = findings.Count(f => f.Severity == "error");
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { product = template.ProductId, pass = errors == 0, findings,
            note = "references and freshness only; content, decisions and approval need human review" }, JsonOptions()));
        else ProductCheck.Report(template, findings, Console.Out);
        return errors == 0 ? 0 : 1;
    }
    if (json) return Usage("--format applies to product check, not to writing a draft", "product");
    const string marker = "<!-- generated by docwizz product -->";
    if (File.Exists(full) && !File.ReadLines(full).Take(2).Contains(marker))
    {
        Console.Error.WriteLine($"{full} exists and is not docwizz-generated; refusing to overwrite");
        return 1;
    }
    var model = BuildModel(root, config).Model;
    Dictionary<string, List<ProductSynthesis.Sentence>>? synthesis = null;
    if (ai)
    {
        var catalog = ProductDraft.Catalog(template, model, project);
        using var provider = AiProviders.Create(config.Ai, "product synthesis");
        if (provider is not null)
            Console.Error.WriteLine($"docwizz: drafting {catalog.Count(c => c.Evidence.Count > 0)} sections with {provider.Model} at {provider.Endpoint} (evidence only; sections without evidence stay OFFEN)");
        synthesis = await ProductSynthesis.Synthesize(template, catalog, provider, AiProviders.Tag(config.Ai),
            Path.Combine(root, "docs", ".docwizz", "product-ai-cache.json"));
        if (synthesis.Count == 0) synthesis = null;   // nothing worded: the deterministic draft, without ids or an AI note
    }
    var text = (marker + "\n" + ProductDraft.Render(template, model, project, synthesis)).Replace("\r\n", "\n");
    // The evidence and gap report sits next to the draft (<id>.evidence.json) and is protected the same way.
    var report = Path.ChangeExtension(full, ".evidence.json");
    var evidence = ProductDraft.Report(template, model, project, synthesis);
    if (File.Exists(report) && !File.ReadAllText(report).Contains("\"generator\": \"docwizz product\""))
    {
        Console.Error.WriteLine($"{report} exists and is not docwizz-generated; refusing to overwrite");
        return 1;
    }
    var written = false;
    foreach (var (file, content) in new[] { (full, text), (report, evidence) })
    {
        if (File.Exists(file) && File.ReadAllText(file) == content) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        Console.WriteLine($"→ {Path.GetRelativePath(root, file)}");
        written = true;
    }
    if (!written) Console.WriteLine($"unchanged: {Path.GetRelativePath(root, full)}");
    return 0;
}

static int SbomCommand(string root, string output, Config config)
{
    var files = RepoFiles(root, config);
    var json = Sbom.Export(root, files);
    var parent = Path.GetDirectoryName(Path.GetFullPath(output));
    if (parent is not null) Directory.CreateDirectory(parent);
    File.WriteAllText(output, json);
    Console.WriteLine($"→ {output}");
    return 0;
}

// Starter config: the defaults, spelled out, to edit in place.
static int Init(string dir)
{
    var file = Path.Combine(dir, "docwizz.yaml");
    if (File.Exists(file))
    {
        Console.Error.WriteLine($"{file} exists; not overwriting");
        return 1;
    }
    File.WriteAllText(file, Config.Default);
    Console.WriteLine($"→ {file}");
    return 0;
}

// `docwizz` alone: the next useful action, not the full reference.
static int Start()
{
    Console.Error.WriteLine("""
        No command specified.

        For a new repository:
          docwizz setup .

        Common workflows:
          docwizz generate .       # write or refresh the docs
          docwizz check .          # quality gate (CI)
          docwizz architecture .   # layers, violations, cycles

        Run 'docwizz help' for all commands, 'docwizz help <command>' for one.
        """);
    return 1;
}

static int Help(string? command)
{
    if (command is not null && Cli.Commands.TryGetValue(command, out var c))
    {
        Console.WriteLine($"""
            docwizz {c.Syntax}

              {c.Purpose}
              for: {c.Use}

            example:
              {c.Example}
            """);
        if (c.Options.Length > 0) Console.WriteLine($"\noptions:\n  {c.Options}\n");
        Console.WriteLine("All commands, options and how they combine: CLI.md");
        return 0;
    }
    if (command is not null && command != "help") return Usage($"unknown command {command}");
    Console.WriteLine(Reference());
    return 0;
}

static int Usage(string? error, string? command = null)
{
    if (error is not null) Console.Error.WriteLine(error);
    Console.Error.WriteLine(command is not null && Cli.Commands.ContainsKey(command)
        ? $"Run 'docwizz help {command}' for its options."
        : "Run 'docwizz help' for all commands and options.");
    return 1;
}

static string Reference() => $"""
    usage: docwizz <command> [dir] [options]

    start here:
      docwizz setup [dir]               detect the stack, write docwizz.yaml, analyze, generate docs, check
        [--force]                       overwrite an existing docwizz.yaml
        [--select-projects]             choose projects interactively (TTY required)
        [--all-projects]                clear a saved project scope, preserving other config
        [--yes]                         no prompt for scripts and CI
        [--html] [--progress] [--refresh-html-on-version-change] [--ai] [--ai-ratings]  as for generate

    everyday:
      docwizz discover [dir]            read-only project and solution inventory [--format json]
      docwizz generate <dir> [out]      write Markdown docs (default <dir>/docs)
        [--html]                        also write an HTML page next to every Markdown page
        [--screenshots --base-url <url>]  capture static routes from a running frontend (Playwright)
        [--storage-state <file>]        reuse a saved Playwright login session (generate screenshots only)
        [--screenshot-delay <ms>]       extra wait before each capture, for slow pages (screenshots only)
        [--refresh-html-on-version-change]  rewrite HTML when the docwizz version changes
        [--progress]                    print progress to stderr (including redirected output)
        [--ai]                          draft missing summaries with a self-hosted model (Ollama or OpenAI-compatible)
        [--ai-ratings]                  rate written docs with a self-hosted model (advisory; independent of --ai)
        [--requirements]                drafts also get each symbol's linked work items (requirements import first)
      docwizz analyze <dir>             documentation report
      docwizz check <dir>               report + exit 1 if thresholds fail (CI)
      docwizz check <dir> --since <ref> exit 1 only on gaps/violations introduced since <ref>
      docwizz architecture <dir>        layers, dependencies, violations; exit 1 above check thresholds
      docwizz diff <dir> [ref]          what changed vs <ref> (default: docs/.docwizz/model.json)
      docwizz diff [dir] <base> <head>  what changed between two git refs
      docwizz context <dir> --for <target>  token-budgeted facts about one symbol, file, folder or endpoint, for agents
        [--hops <1-4>] [--budget <tokens>] [--format md|json] [--include-ai] [--requirements]
      docwizz domain discover <dir> --seed <symbol|endpoint>  bounded graph walk, optional historical requirements
      docwizz load import <dir> <report.json>...  powerwizz load-test results, shown per endpoint in api.md
      docwizz domain areas <dir>        candidate business areas: methods grouped by folder and delegation, inferred
        [--work-item <id>] [--depth <0-8>] [--limit <1-500>] [--format json]
        --work-item replaces --seed; the imported requirements index is required for a work-item seed
      docwizz mcp <dir>                 read-only MCP server on stdio for coding agents (find_symbol, context, impact, ...)
        [--auto-rescan]                 rescan when the model is stale instead of only saying so
      docwizz sbom [dir] [out]          export direct manifest dependencies as CycloneDX JSON
      docwizz remediate <dir>           package update suggestions: command or patch, impact, confidence
        [--package <name> --to <ver>]   propose this update (default: remediation.targets and version drift)
        [--validate]                    apply each in a temporary copy and run restore, build, tests
        [--since <ref>]                 say whether each touches only what changed since <ref>

    manual setup and debugging:
      docwizz init [dir]                write a starter docwizz.yaml with every default
      docwizz scan <dir> [model.json]   write the raw code model
      docwizz evidence-diff <dir> <before.yaml> <after.yaml>  compare explicit evidence changes
        [--format json]                 machine-readable change report
      docwizz tests import <dir> <snapshot.json>  validate and store an offline Azure DevOps test snapshot
      docwizz trace <dir> <evidence.yaml>  explicit requirement-architecture-test gaps
        [--format json]                 machine-readable instead of Markdown
      docwizz requirements import <dir> <history.json> [<work-items>]  index exported commit/PR/work-item history
        [--progress]                    progress bar on stderr also when redirected (commits mapped, time left)
      docwizz requirements show <dir> --for <symbol>  work items a symbol was changed for (confirmed/candidate)
      docwizz requirements query <dir> <query.json>   answer gitwizz's requirement query (gitwizz.requirement-input/v1)
        [--work-item <id>] [--format json]  one work item's symbols (123 or github#123); machine-readable
      docwizz product <dir> <template.yaml> [out.md]  product draft from code and project evidence
        [--context <file.yaml>]         load sourced project facts
        [--ai]                          a self-hosted model words sections, citing evidence ids
      docwizz product check <dir> <template.yaml> [draft.md]  references and freshness of a draft; exit 1 on errors

    options:
      --profile <name|file.yaml>        setup/generate/check/analyze/architecture/diff: documentation profile,
                                        one of {string.Join(", ", Profiles.Names)}, or your own file
      --format console|json             discover/analyze/check/architecture/diff/context/domain/product/trace/evidence-diff/requirements/remediate output
      --timings                         time per stage and peak memory, on stderr (benchmarks)
      --help, -h                        help for a command: docwizz <command> --help
      --version, -v                     version, commit and runtime (also: docwizz version)

    Every command, option and how they combine: CLI.md
    """;

// Every file docwizz may read. Prefer git's view (honours .gitignore, skips nested worktrees); fall back to a directory walk.
// Dot-directories are skipped except those named in `dotDirs` (CI descriptors live in .github/.circleci).
static List<string> RepoFilesUnscoped(string root, Config config, string[]? dotDirs = null)
{
    string[] skip = ["bin", "obj", "node_modules", "dist"];
    // docwizz's own output (a directory below the root holding .docwizz/): its search.js would otherwise be scanned
    // as a module on the next run, so regenerating unchanged code would change the docs.
    var output = new Dictionary<string, bool>();
    bool IsOutput(string dir) => output.TryGetValue(dir, out var o) ? o : (output[dir] = Directory.Exists(Path.Combine(root, dir, ".docwizz")));
    bool InOutput(string rel)
    {
        for (var dir = Path.GetDirectoryName(rel); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            if (IsOutput(dir)) return true;
        return false;
    }
    return (Git(root, "ls-files --cached --others --exclude-standard")?
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => Path.Combine(root, f)).Where(File.Exists)
        ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).SkipLast(1)
            .Any(d => (d.StartsWith('.') && dotDirs?.Contains(d) != true) || skip.Contains(d)))
        .Where(f => !InOutput(Path.GetRelativePath(root, f)))
        .Where(f => !config.Exclude.Any(g =>
            FileSystemName.MatchesSimpleExpression(g, Path.GetRelativePath(root, f).Replace('\\', '/'))))
        .Distinct()
        // Sorted: a directory walk (a `git archive`d ref) and `git ls-files` must yield the same order, or which
        // declaration of a multi-file symbol wins below would differ between the two sides of a diff.
        .OrderBy(f => Path.GetRelativePath(root, f).Replace('\\', '/'), StringComparer.Ordinal)
        .ToList();
}

// All consumers of source/deployment/manifest files share the same scope. Discovery alone uses RepoFilesUnscoped.
static List<string> RepoFiles(string root, Config config, string[]? dotDirs = null)
{
    var all = RepoFilesUnscoped(root, config, dotDirs);
    return ProjectScope.Resolve(root, config.Scope, all).Files;
}

// progress, when given, hears about every step from the first file listing on: a large repository spends most of a
// generate here, and a bar that only moves once the scan is over looks hung.
// historical: root is a tree as of an older commit (ProjectScope.Resolve).
static (CodeModel Model, List<string> Files) BuildModel(string root, Config config, CliProgress? progress = null, bool historical = false)
{
    string[] exts = [".cs", ".vue", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".sql", ".java", ".php"];
    progress?.Report(0, 1, "listing files");
    var raw = Timings.Measure("files", () => RepoFilesUnscoped(root, config));
    var plan = ProjectScope.Resolve(root, config.Scope, raw, historical);
    var candidates = plan.Files;
    var scanned = candidates.Where(f => exts.Contains(Path.GetExtension(f)) && !f.EndsWith(".d.ts") && !f.EndsWith(".min.js")
        && !f.Replace('\\', '/').Contains("/wwwroot/lib/")).ToList(); // ponytail: libman/bower vendor dir only; other vendored JS needs exclude:

    var csFiles = scanned.Where(f => f.EndsWith(".cs")).ToList();
    var feFiles = scanned.Where(f => Path.GetExtension(f) is ".vue" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs").ToList();
    var sqlFiles = scanned.Where(f => f.EndsWith(".sql")).ToList();
    var javaFiles = scanned.Where(f => f.EndsWith(".java")).ToList();
    var phpFiles = scanned.Where(f => f.EndsWith(".php")).ToList();
    // Work units for the bar: a file each, plus the start of a Node process for the scanners that need one.
    // C# files cost about twice the others, and loading the reference assemblies is a fixed cost of its own.
    var csUnits = csFiles.Count == 0 ? 0 : 2 * csFiles.Count + 20;
    var total = 2 + csUnits + feFiles.Count + (feFiles.Count > 0 ? 20 : 0) + sqlFiles.Count + javaFiles.Count + (javaFiles.Count > 0 ? 10 : 0) + phpFiles.Count;
    var done = 1;
    string Files(int n, string what) => $"{what} · {n:N0} file{(n == 1 ? "" : "s")}";
    progress?.Report(done, total, $"found {scanned.Count:N0} source files");

    // The scanner counts parsing as the first half, the semantic pass as the second; reference loading sits between.
    var (nodes, edges) = Timings.Measure("scan:csharp", () => CSharpScanner.Scan(root, csFiles, config.CommentDocs, (i, n) =>
        progress?.Report(done + (i < csFiles.Count ? i : i + 20), total, i < csFiles.Count ? $"C# parsing {i + 1:N0}/{csFiles.Count:N0}"
            : i == csFiles.Count ? "C# loading references" : $"C# symbols {i - csFiles.Count + 1:N0}/{csFiles.Count:N0}")));
    done += csUnits;
    if (feFiles.Count > 0) progress?.Report(done, total, Files(feFiles.Count, "Vue/TS/JS"));
    var (feNodes, feEdges) = Timings.Measure("scan:frontend", () => Frontend.Scan(root, feFiles));
    done += feFiles.Count + (feFiles.Count > 0 ? 20 : 0);
    if (sqlFiles.Count > 0) progress?.Report(done, total, Files(sqlFiles.Count, "SQL"));
    var (sqlNodes, sqlEdges) = Timings.Measure("scan:sql", () => Sql.Scan(root, sqlFiles));
    done += sqlFiles.Count;
    if (javaFiles.Count > 0) progress?.Report(done, total, Files(javaFiles.Count, "Java"));
    var (javaNodes, javaEdges) = Timings.Measure("scan:java", () => JavaScanner.Scan(root, javaFiles));
    done += javaFiles.Count + (javaFiles.Count > 0 ? 10 : 0);
    if (phpFiles.Count > 0) progress?.Report(done, total, Files(phpFiles.Count, "PHP"));
    var (phpNodes, phpEdges) = Timings.Measure("scan:php", () => PhpScanner.Scan(root, phpFiles));
    done += phpFiles.Count;
    progress?.Report(done, total, "projects and configuration");
    var (projNodes, projEdges) = Timings.Measure("scan:projects", () => Projects.Scan(root, candidates.Where(f => f.EndsWith(".csproj") || f.EndsWith(".sln") || f.EndsWith(".slnx")
        || Path.GetFileName(f) is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts")));
    if (config.Scope is not null) ProjectScope.CompleteReferences(plan, projNodes, projEdges);
    // Keys defined in configuration files win over the bare key nodes code reads create.
    var settings = Timings.Measure("scan:configuration", () => Configuration.Scan(root, candidates.Where(Configuration.IsConfigFile)));
    var defined = settings.Select(n => n.Id).ToHashSet();
    nodes.RemoveAll(n => n.Kind == "config" && defined.Contains(n.Id));
    progress?.Report(total - 1, total, "linking the code graph");
    var link = Timings.Stage("link");
    var testFiles = scanned.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
        .Where(f => config.Tests.Any(g => FileSystemName.MatchesSimpleExpression(g, f))
            || Projects.Of(projNodes, f)?.Tags?.Contains("test") == true).ToHashSet(); // test projects: by metadata, not only by path
    // Same id from test and production code (a test router's `route:/login`): production wins.
    nodes = CodeModel.MergeHashes(nodes, n => testFiles.Contains(n.File)).OrderBy(n => testFiles.Contains(n.File)).DistinctBy(n => n.Id).ToList();
    var ids = nodes.Select(n => n.Id).ToHashSet();
    nodes.AddRange(CodeModel.MergeHashes(feNodes.Concat(javaNodes).Concat(phpNodes).Concat(sqlNodes), n => testFiles.Contains(n.File)).OrderBy(n => testFiles.Contains(n.File)).Concat(projNodes).Concat(settings).Where(n => ids.Add(n.Id)));
    edges = Frontend.LinkHttp(nodes, Sql.Link(nodes, [.. edges, .. feEdges, .. javaEdges, .. phpEdges, .. sqlEdges, .. projEdges]));
    Externals.Link(nodes, edges);
    Configuration.Link(nodes, edges);

    // Test code leaves only `tests` edges (test symbol → code it uses) behind.
    var testIds = nodes.Where(n => testFiles.Contains(n.File)).Select(n => n.Id).ToHashSet();
    nodes.RemoveAll(n => testIds.Contains(n.Id));
    edges = edges.Where(e => !testIds.Contains(e.To) && (!testIds.Contains(e.From) || e.Kind is "calls" or "creates" or "imports" or "renders" or "injects" or "http"))
        .Select(e => testIds.Contains(e.From) ? new Edge(e.From, e.To, "tests") : e).Distinct().ToList();

    nodes = nodes.Select(n => n.Kind is "external" or "config" ? n : n with { Language = Language(n.File) }).ToList();
    link.Dispose();

    var files = scanned.Where(f => !testFiles.Contains(Path.GetRelativePath(root, f).Replace('\\', '/'))).ToList();
    return (new CodeModel(Git(root, "rev-parse --short HEAD")?.Trim(), nodes, edges,
        ProjectScope.Fingerprint(config.Scope), plan.Inventory), files);
}

static string? Language(string file) => Path.GetExtension(file) switch
{
    ".cs" => "csharp", ".vue" => "vue", ".ts" or ".tsx" => "typescript", ".js" or ".jsx" or ".mjs" => "javascript", ".sql" => "sql", ".java" => "java", ".php" => "php", ".csproj" => "msbuild",
    _ => Path.GetFileName(file) switch { "package.json" => "npm", "pom.xml" => "maven", "build.gradle" or "build.gradle.kts" => "gradle", _ => null },
};

static int Scan(string root, string outFile, Config config)
{
    var (model, files) = BuildModel(root, config);
    WriteModel(model, outFile);

    Console.WriteLine($"Files  {files.Count}");
    foreach (var g in files.GroupBy(Path.GetExtension).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-5} {g.Count()}");
    Console.WriteLine($"Nodes  {model.Nodes.Count}");
    foreach (var g in model.Nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-12} {g.Count()}");
    Console.WriteLine($"Edges  {model.Edges.Count}");
    Console.WriteLine($"→ {outFile}");
    return 0;
}

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

static void WriteModel(CodeModel model, string file) => File.WriteAllText(file, JsonSerializer.Serialize(model, JsonOptions()));

// The documentation model as data: per item what is required, what exists and where it comes from.
static object DocumentationJson(CodeModel model, Config config, List<DocumentationItem> items)
{
    var nodes = model.Nodes.ToDictionary(n => n.Id);
    // Evidence: the source location of every symbol an item's sections were derived or drafted from.
    List<string> Evidence(DocumentationItem i) => i.Sources.Concat(i.Sections.Values.SelectMany(s => s.From ?? []))
        .Distinct().Select(nodes.GetValueOrDefault).OfType<Node>().Select(CodeModel.Location).ToList();
    return new
    {
        commit = model.Commit,
        profile = config.Profile,
        note = $"coverage against the '{config.Profile}' profile; not a statement of standards compliance",
        coverage = Math.Round(Analyzer.Coverage(items), 1),
        quality = Math.Round(Analyzer.Quality(items), 1),
        items = items.Select(i => new
        {
            id = i.Node.Id, kind = i.Node.Kind, file = i.Node.File, line = i.Node.Line,
            level = i.Level, status = i.Status, pattern = i.Pattern, required = i.Required,
            sections = i.Sections, missing = i.Missing, reasons = i.Reasons, sources = i.Sources, evidence = Evidence(i), tested = i.Tested,
            flags = i.Flags is { Count: > 0 } ? i.Flags : null,
        }),
    };
}

// Writes the docs; returns the one-line result (setup shows it as the stage detail) and the full report `generate`
// prints below it.
static async Task<GenerateReport> Generate(string root, string outDir, Config config, bool ai, bool html = false, CodeModel? model = null, bool refreshHtmlOnVersionChange = false, bool progress = false, string? screenshotBaseUrl = null, string? screenshotStorageState = null, int screenshotDelayMs = 0, RequirementIndex.Index? requirements = null, bool aiRatings = false)
{
    // Weights: each stage's usual share of the wall time (bench/baseline.json); the scan dominates, AI calls when on.
    List<CliProgress.Stage> stages =
    [
        new("scan", model is null ? 70 : 0, "Scanning code"),
        new("analyze", 3, "Analyzing docs"),
        new("architecture", 2, "Architecture"),
        new("AI summaries", ai ? 40 : 1, "AI summaries"),
        new("AI assessments", aiRatings ? 20 : 1, "AI assessments"),
    ];
    if (screenshotBaseUrl is not null) stages.Add(new("screenshots", 15, "Screenshots"));
    stages.AddRange([new("write pages", 12, "Writing pages"), new("metadata", 2, "Metadata")]);
    using var bar = CliProgress.ForConsole(stages, progress);

    bar.Begin("scan", "starting");
    int? sourceFiles = null;
    if (model is null)
    {
        var built = BuildModel(root, config, bar);
        (model, sourceFiles) = (built.Model, built.Files.Count);
    }
    var symbols = model.Nodes.Count(n => n.Kind is not ("route" or "project" or "package" or "external" or "config"));
    bar.Advance("scan", (sourceFiles is { } sf ? $"{sf:N0} files, " : "") + $"{symbols:N0} symbols");

    bar.Begin("analyze");
    var findings = Timings.Measure("analyze", () => Analyzer.Analyze(model, config));
    var critical = findings.Count(Analyzer.IsCritical);
    bar.Advance("analyze", $"coverage {Analyzer.Coverage(findings):0}%, {critical} critical");

    bar.Begin("architecture");
    var arch = Timings.Measure("architecture", () => Architecture.Check(model, config));
    bar.Advance("architecture", $"{Plural(arch.Violations.Count, "violation")}, {Plural(arch.Cycles.Count, "cycle")}");

    // Cached drafts are always used; new ones are only requested with --ai.
    bar.Begin("AI summaries", ai ? "drafting" : "from cache");
    // --requirements: each symbol's bounded requirement selection goes into its prompt (and its cache key).
    var head = requirements is null ? null : Git(root, "rev-parse HEAD")?.Trim();
    var current = model.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
    Func<Node, BusinessContext.Selection?>? business = requirements is null ? null
        : n => BusinessContext.Select(RequirementResolver.For(requirements, n.Id, n, head, current.Contains), config.Ai.Requirements);
    var drafts = await AiProse.Summaries(root, model, findings, Path.Combine(outDir, ".docwizz", "ai-cache.json"), ai, config.Ai,
        AiProgress(bar, "drafts"), business);
    // A draft fills what is missing, never what is written or derived: the summary, and sections the item doesn't have.
    foreach (var f in findings)
    {
        if (!drafts.TryGetValue(f.Node.Id, out var draft)) continue;
        if (draft.Text.Length > 0) f.Sections["summary"] = new(Origin.Ai, draft.Text, draft.Sources, draft.Sections?.GetValueOrDefault("summary"));
        foreach (var (name, sentences) in draft.Sections ?? [])
            if (name != "summary" && (AiProse.SectionNames.Contains(name) || (requirements is not null && AiProse.BusinessSectionNames.Contains(name))))
                f.Sections.TryAdd(name == "errors" ? "exception" : name,
                    new(Origin.Ai, string.Join(" ", sentences.Select(x => x.Text)), [.. sentences.SelectMany(x => x.From).Distinct()], sentences));
    }
    var summaries = drafts.Where(d => !d.Key.StartsWith("module:") && d.Value.Text.Length > 0).ToDictionary(d => d.Key, d => d.Value.Text);
    var overviews = drafts.Where(d => d.Key.StartsWith("module:")).ToDictionary(d => d.Key["module:".Length..], d => d.Value);
    bar.Advance("AI summaries", drafts.Count == 0 ? (ai ? "none" : "off (--ai)") : $"{Plural(summaries.Count, "summary", "summaries")}, {Plural(overviews.Count, "overview")}");

    // Advisory ratings of written docs: shown in quality.md, never part of doc quality % or check.
    bar.Begin("AI assessments", aiRatings ? "rating written docs" : "from cache");
    var assessments = await AiProse.Assessments(root, model, findings, Path.Combine(outDir, ".docwizz", "ai-assessments.json"), aiRatings, config.Ai,
        AiProgress(bar, "rated"));
    bar.Advance("AI assessments", assessments.Count == 0 ? (aiRatings ? "none" : "off (--ai-ratings)") : Plural(assessments.Count, "rating"));

    var screenshots = new Dictionary<string, string>();
    if (screenshotBaseUrl is not null)
    {
        bar.Begin("screenshots", screenshotBaseUrl);
        screenshots = await ScreenshotCapture.Run(model, outDir, screenshotBaseUrl, screenshotStorageState, screenshotDelayMs);
        bar.Advance("screenshots", Plural(screenshots.Count, "route"));
    }
    else screenshots = ScreenshotCapture.Existing(model, outDir);

    // Requirement pages come from an imported index whenever there is one; --requirements only adds it to AI prompts.
    var pageRequirements = requirements ?? ImportedRequirements(root);
    bar.Begin("write pages", html ? "Markdown and HTML" : "Markdown");
    var rebuildHtml = html && refreshHtmlOnVersionChange && HtmlVersion.NeedsRefresh(outDir, AppVersion.Number);
    var (pages, changed) = Timings.Measure("generate", () => new Generator(root, outDir, model, findings, arch, config, summaries, overviews, assessments)
    {
        WriteHtml = html,
        RebuildHtml = rebuildHtml,
        Screenshots = screenshots,
        Load = LoadAttached(root, model),
        Requirements = pageRequirements,
        Head = pageRequirements is null ? null : head ?? Git(root, "rev-parse HEAD")?.Trim(),
        Files = [.. RepoFiles(root, config, [".github", ".circleci"]).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))],
    }.Run());
    bar.Advance("write pages", $"{Plural(pages.Count, "page")}, {changed.Count} changed");

    bar.Begin("metadata");
    if (html) HtmlVersion.Record(outDir, AppVersion.Number);
    // Fingerprint for `docwizz diff`: the model these docs were generated from.
    Directory.CreateDirectory(Path.Combine(outDir, ".docwizz"));
    WriteModel(model, Path.Combine(outDir, ".docwizz", "model.json"));
    File.WriteAllText(Path.Combine(outDir, ".docwizz", "documentation.json"),
        JsonSerializer.Serialize(DocumentationJson(model, config, findings), JsonOptions()));
    bar.Advance("metadata");

    var line = $"{pages.Count} pages → {outDir} (commit {model.Commit ?? "unknown"}): {changed.Count} changed" +
        (changed.Count is > 0 and <= 10 ? $" ({string.Join(", ", changed.Order())})" : "");

    // The report: what was found, how good the docs are, where to look next, and where the time went.
    static string Meter(double pct) => new string('█', (int)Math.Round(pct / 5)) + new string('░', 20 - (int)Math.Round(pct / 5));
    var kinds = model.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
    var endpoints = model.Nodes.Count(n => n.Tags is ["endpoint", ..]);
    var coverage = Analyzer.Coverage(findings);
    var quality = Analyzer.Quality(findings);
    var written = Analyzer.WithWrittenDocs(findings).Count;
    var warnings = findings.Count(f => f.Level == Level.Medium && f.Status != Status.Documented);
    var undocumented = findings.Count(f => f.Status == Status.Undocumented);
    var documented = findings.Count(f => f.Status == Status.Documented);
    var partial = findings.Count - documented - undocumented;
    var security = arch.Violations.Count(v => v.Rule.StartsWith("SEC-"));
    var page = (string name) => Path.Combine(outDir, html ? Path.ChangeExtension(name, ".html") : name);
    var details = new List<string>
    {
        $"Scanned       {(sourceFiles is { } n ? $"{Plural(n, "source file")} → " : "")}{Plural(symbols, "symbol")}" +
            (endpoints > 0 ? $", {Plural(endpoints, "endpoint")}" : "") +
            (kinds.GetValueOrDefault("project") is > 0 and var projects ? $", {Plural(projects, "project")}" : "") +
            (kinds.GetValueOrDefault("route") is > 0 and var routes ? $", {Plural(routes, "route")}" : ""),
        $"Coverage      {Meter(coverage)} {coverage,3:0}%  {documented:N0} documented, {partial:N0} partial, {undocumented:N0} missing (profile {config.Profile})",
        $"Doc quality   {Meter(quality)} {quality,3:0}%  of {Plural(written, "item")} with written docs",
        $"Gaps          {critical} critical, {warnings} warning{(warnings == 1 ? "" : "s")}" + (critical + warnings > 0 ? $" → {page("quality.md")}" : ""),
        $"Architecture  {Plural(arch.Violations.Count - security, "violation")}, {Plural(arch.Cycles.Count, "cycle")}" +
            (config.Security.Enabled ? $", {Plural(security, "security finding")}" : "") +
            (arch.Violations.Count + arch.Cycles.Count > 0 ? $" → {page("architecture.md")}" : ""),
    };
    if (ai || aiRatings || drafts.Count + assessments.Count > 0)
        details.Add($"AI            {Plural(summaries.Count, "summary", "summaries")}, {Plural(overviews.Count, "module overview")}, {Plural(assessments.Count, "rating")}" +
            (ai || aiRatings ? "" : " (cached; --ai drafts more, --ai-ratings rates written docs)"));
    if (screenshotBaseUrl is not null) details.Add($"Screenshots   {Plural(screenshots.Count, "route")} from {screenshotBaseUrl}");
    else if (screenshots.Count > 0) details.Add($"Screenshots   {Plural(screenshots.Count, "route")} kept from the last capture (--screenshots to refresh)");
    details.Add($"Pages         {pages.Count:N0} {(html ? "Markdown + HTML" : "Markdown")}: {changed.Count:N0} changed, {pages.Count - changed.Count:N0} unchanged");
    var slowest = bar.Durations.Where(d => d.Time.TotalSeconds >= 0.05).OrderByDescending(d => d.Time).Take(3)
        .Select(d => $"{d.Stage} {CliProgress.Duration(d.Time)}").ToList();
    details.Add($"Time          {CliProgress.Duration(bar.Elapsed)}" + (slowest.Count > 0 ? $"  ({string.Join(" · ", slowest)})" : ""));
    details.Add("");
    details.Add($"Open          {page("index.md")}");
    if (!ai && findings.Count(f => f.Missing.Contains("summary")) is > 0 and var missing)
        details.Add($"Next          docwizz generate {root} --ai   drafts {Plural(missing, "missing summary", "missing summaries")}");
    details.Add($"{(details[^1].StartsWith("Next") ? "             " : "Next         ")} docwizz check {root}   gates coverage in CI");
    return new(line, details);
}

static string Plural(int n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";

// An AI stage's bar detail: "12/35 drafts · ~6m 02s left · MaterialController": what is left estimated from the average
// time per finished request so far (requests run in parallel, so this is throughput, not one request's time), then the
// item asked for most recently. The estimate comes first so a narrow terminal cuts the name, not the time.
static Action<int, int, string?> AiProgress(CliProgress bar, string unit)
{
    var clock = Stopwatch.StartNew();
    return (done, total, current) =>
    {
        if (done == 0 && current is null) clock.Restart(); // the first request starts now, not when the cache was read
        var left = done > 0 && done < total ? $" · ~{CliProgress.Duration(clock.Elapsed / done * (total - done))} left" : "";
        bar.Report(done, total, $"{done:N0}/{total:N0} {unit}{left}{(current is null ? "" : $" · {current}")}");
    };
}

static int DiffCommand(string root, string? gitRef, Config config, bool enforce, string? head = null, bool json = false)
{
    CodeModel? before;
    string baseline;
    if (gitRef is not null)
    {
        before = ModelAt(root, gitRef, config);
        baseline = gitRef;
    }
    else
    {
        var fingerprint = Path.Combine(root, "docs", ".docwizz", "model.json");
        if (!File.Exists(fingerprint))
        {
            Console.Error.WriteLine($"no {fingerprint}; run docwizz generate first or pass a git ref");
            return 1;
        }
        before = JsonSerializer.Deserialize<CodeModel>(File.ReadAllText(fingerprint), JsonOptions());
        if (before?.ScopeFingerprint != ProjectScope.Fingerprint(config.Scope))
        {
            Console.Error.WriteLine("scope changed since the last generate; regenerate docs before comparing against the saved model");
            return 1;
        }
        baseline = $"docs generated at {before?.Commit ?? "unknown"}";
    }
    if (before is null) return 1;

    var after = head is null ? BuildModel(root, config).Model : ModelAt(root, head, config);
    if (after is null) return 1;
    var result = Timings.Measure("diff", () => Diff.Compare(before, after, config));
    var against = head is null ? baseline : $"{baseline}, at {head}";
    if (!json) Diff.Report(result, against, Console.Out);
    if (!enforce)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(DiffJson(result, against, null), JsonOptions()));
        return 0;
    }

    var (ok, summary) = Gate(result, config);
    if (json) Console.WriteLine(JsonSerializer.Serialize(DiffJson(result, against, new { pass = ok, summary }), JsonOptions()));
    else
    {
        Console.WriteLine();
        Console.WriteLine(summary);
    }
    return ok ? 0 : 1;
}

// `check --since`: whether the change introduces what the thresholds forbid, and the one-line verdict.
static (bool Ok, string Summary) Gate(DiffResult result, Config config)
{
    var critical = result.NewGaps.Count(Analyzer.IsCritical);
    var violations = Architecture.Failing(result.NewViolations, config.Check).Count;
    var complex = TooComplex(result.Added.Concat(result.Changed), config.Check);
    // Docs that now contradict the code fail; inferred flags and possibly stale docs are reported only.
    var contradictions = result.NewFlags.Count(x => x.Flag.Origin == Origin.Fact);
    // New symbols at or above check.require_tests with no linked test code (structural, not coverage).
    var untested = config.Check.RequireTests is { } rt && Enum.Parse<Level>(rt, true) is var min
        ? result.Tests.Where(t => t.Added && t.Tests.Count == 0 && t.Level >= min).ToList() : [];
    var ok = critical == 0 && violations == 0 && complex.Count == 0 && contradictions == 0 && untested.Count == 0;
    var summary = ok ? "check: PASS" : $"check: FAIL — introduced {critical} critical gaps, {violations} violations" +
        (contradictions > 0 ? $", {contradictions} docs contradicting the code" : "") +
        (complex.Count > 0 ? $", {complex.Count} symbols over complexity {config.Check.MaxComplexity}" : "") +
        (untested.Count > 0 ? $", {untested.Count} new {config.Check.RequireTests!.ToLowerInvariant()}-level symbols without linked tests ({string.Join(", ", untested.Select(t => Naming.Display(t.Symbol)))})" : "");
    return (ok, summary);
}

// `diff`/`check --since` as data: symbols by id and location, and test traceability with its caveat spelled out.
static object DiffJson(DiffResult d, string baseline, object? check)
{
    static object Symbol(Node n) => new { id = n.Id, kind = n.Kind, location = CodeModel.Location(n) };
    return new
    {
        baseline,
        added = d.Added.Select(Symbol), changed = d.Changed.Select(Symbol), removed = d.Removed.Select(Symbol),
        pages = d.Pages, dependencies = new { added = d.DepsAdded, removed = d.DepsRemoved }, decisions = d.Decisions,
        stale = d.Stale.Select(s => new { id = s.After.Id, location = CodeModel.Location(s.After), changes = s.Changes }),
        introduced = new
        {
            gaps = d.NewGaps.Select(f => new { id = f.Node.Id, location = CodeModel.Location(f.Node), level = f.Level, critical = Analyzer.IsCritical(f), missing = f.Missing }),
            violations = d.NewViolations,
            flags = d.NewFlags.Select(x => new { id = x.Item.Node.Id, x.Flag.Rule, basis = x.Flag.Origin, x.Flag.Detail }),
        },
        packages = d.Packages.Select(p => new { project = p.Project, package = p.Package, ecosystem = p.Ecosystem, before = p.Before, after = p.After, kind = p.Kind,
            symbols = p.Impact.Uses.Select(u => u.Id), flows = p.Impact.Flows, tests = p.Impact.Tests, scope = p.Scope, note = Remediations.ImpactNote }),
        tests = new
        {
            note = TestLinks.Note,
            linked = d.Tests.Where(t => t.Tests.Count > 0).Select(t => new { id = t.Symbol.Id, location = CodeModel.Location(t.Symbol), change = t.Added ? "added" : "changed",
                level = t.Level, tests = t.Tests.Select(l => new { test = l.Test, link = l.Via is null ? "direct" : "indirect", via = l.Via }) }),
            unlinked = d.Tests.Where(t => t.Tests.Count == 0).Select(t => new { id = t.Symbol.Id, location = CodeModel.Location(t.Symbol), change = t.Added ? "added" : "changed", level = t.Level }),
        },
        check,
    };
}

// Scans the tree as it was at <ref>, extracted from git into a temp dir.
// historical: a selected project that doesn't exist yet at gitRef is left out instead of failing (requirements import
// maps every exported commit, most of them older than some of today's projects).
// Cache raw immutable blobs only; symbol resolution still runs for the historical context.
static byte[]? GitBlob(string top, string blobId)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("git", ["cat-file", "blob", blobId])
        {
            WorkingDirectory = top, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        using var stream = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(stream);
        process.WaitForExit();
        _ = errors.Result;
        return process.ExitCode == 0 ? stream.ToArray() : null;
    }
    catch (IOException) { return null; }
    catch (System.ComponentModel.Win32Exception) { return null; }
}

static CodeModel? ModelAt(string root, string gitRef, Config config, bool historical = false, List<string>? changedFiles = null)
{
    var profile = Environment.GetEnvironmentVariable("DOCWIZZ_REQUIREMENTS_PROFILE") == "1" && changedFiles is not null;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var last = TimeSpan.Zero;
    void Mark(string stage)
    {
        if (!profile) return;
        var elapsed = clock.Elapsed;
        Console.Error.WriteLine($"docwizz requirements profile: {gitRef[..Math.Min(7, gitRef.Length)]} {stage} +{(elapsed - last).TotalSeconds:0.000}s total={elapsed.TotalSeconds:0.000}s");
        last = elapsed;
    }
    var prefixRun = GitRun(root, "rev-parse --show-prefix");
    var shaRun = prefixRun.Exit == 0 ? GitRun(root, $"rev-parse --short {gitRef}") : prefixRun;
    if (prefixRun.Exit != 0 || shaRun.Exit != 0)
    {
        // git's own reason: a missing repository and an unknown ref read alike otherwise, and a transient failure
        // (#133) leaves nothing to go on.
        Console.Error.WriteLine($"not a git repo or unknown ref: {gitRef} ({(prefixRun.Exit != 0
            ? GitFailure("rev-parse --show-prefix", prefixRun) : GitFailure($"rev-parse --short {gitRef}", shaRun))})");
        return null;
    }
    var (prefix, sha) = (prefixRun.Out.Trim(), shaRun.Out.Trim());
    Mark("git ref resolution");
    var tmp = Directory.CreateTempSubdirectory("docwizz-");
    try
    {
        var top = Git(root, "rev-parse --show-toplevel")!.Trim();
        var dir = Directory.CreateDirectory(Path.Combine(tmp.FullName, "tree")).FullName;
        // Read only changed blobs from Git's object database. A blob is immutable and may be
        // reused across commits; do not cache a whole semantic model by blob alone.
        var selective = changedFiles is { Count: > 0 } && config.Scope is null;
        if (profile) Console.Error.WriteLine($"docwizz requirements profile: {sha} mode={(selective ? "selective" : config.Scope is not null ? "scoped-full" : "full")} changedFiles={changedFiles?.Count ?? 0}");
        if (selective)
        {
            var success = true;
            foreach (var path in changedFiles!)
            {
                if (Path.IsPathRooted(path) || path.Split('/', '\\').Any(p => p is ".." or "." or ""))
                {
                    success = false;
                    break;
                }
                var gitPath = prefix.Replace('\\', '/') + path.Replace('\\', '/');
                var blobId = Git(top, $"rev-parse {gitRef}:{gitPath}")?.Trim();
                if (blobId is null || !System.Text.RegularExpressions.Regex.IsMatch(blobId, "^[0-9a-f]{40,64}$"))
                {
                    success = false;
                    break;
                }
                var targetFile = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                // A bounded in-process cache avoids redundant Git reads without persisting
                // sensitive historical content or leaking scanner semantics across versions.
                if (!HistoricalBlobCache.Data.TryGetValue(blobId, out var bytes))
                {
                    var blob = GitBlob(top, blobId);
                    if (blob is null) { success = false; break; }
                    bytes = blob;
                    if (HistoricalBlobCache.Data.Count >= 256) HistoricalBlobCache.Data.Clear();
                    HistoricalBlobCache.Data[blobId] = bytes;
                }
                File.WriteAllBytes(targetFile, bytes);
            }
            if (success)
            {
                Mark("read changed Git blobs");
                var selectiveModel = BuildModel(dir, config, historical: true).Model with { Commit = sha };
                Mark("scan model");
                return selectiveModel;
            }
            Mark("selective fallback");
            Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        // Preserve the existing full-scan fallback for missing blobs or scoped projects.
        var tar = Path.Combine(tmp.FullName, "tree.tar");
        var tree = prefix.Length == 0 ? gitRef : $"{gitRef}:{prefix}";
        if (Git(top, $"archive --format=tar -o \"{tar}\" {tree}") is null) return null;
        Mark("git archive");
        TarFile.ExtractToDirectory(tar, dir, overwriteFiles: true);
        Mark("extract archive");
        var model = BuildModel(dir, config, historical: historical).Model with { Commit = sha };
        Mark("scan model");
        return model;
    }
    finally
    {
        tmp.Delete(recursive: true);
    }
}

static int Analyze(string root, Config config, bool enforce, bool json)
{
    var model = BuildModel(root, config).Model;
    var findings = Timings.Measure("analyze", () => Analyzer.Analyze(model, config));
    var arch = Timings.Measure("architecture", () => Architecture.Check(model, config));
    var (failures, missingSections) = CheckFailures(root, model, config, findings, arch);

    if (json)
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            documentation = DocumentationJson(model, config, findings),
            architecture = arch,
            missingArchitectureSections = config.ArchitectureSections.Count > 0 ? missingSections : null,
            check = enforce ? new { pass = failures.Count == 0, failures } : null,
        }, JsonOptions()));
    else
    {
        Console.WriteLine($"Profile: {config.Profile}");
        Analyzer.Report(findings, Console.Out);
        Architecture.Report(arch, Console.Out);
        if (config.ArchitectureSections.Count > 0)
            Console.WriteLine($"  human-authored sections (docs/architecture/): " + string.Join(", ",
                config.ArchitectureSections.Select(s => missingSections.Contains(s) ? $"{s} ✗" : $"{s} ✓")));
        if (enforce)
        {
            Console.WriteLine();
            Console.WriteLine(failures.Count == 0 ? "check: PASS" : "check: FAIL — " + string.Join("; ", failures));
        }
    }
    return enforce && failures.Count > 0 ? 1 : 0;
}

// The `check` thresholds against one analysis: what fails, and the profile's architecture sections that are missing.
static (List<string> Failures, List<string> MissingSections) CheckFailures(string root, CodeModel model, Config config,
    List<DocumentationItem> findings, ArchitectureResult arch)
{
    var violations = Architecture.Failing(arch.Violations, config.Check);
    var complex = TooComplex(model.Nodes, config.Check);
    var coverage = Analyzer.Coverage(findings);
    var critical = findings.Count(Analyzer.IsCritical);
    var failures = new List<string>();
    if (coverage < config.Check.MinCoverage) failures.Add($"coverage {coverage:0}% < {config.Check.MinCoverage}%");
    if (config.Check.MinQuality is { } minQuality && Analyzer.Quality(findings) is var quality && quality < minQuality)
        failures.Add($"doc quality {quality:0}% < {minQuality}%");
    if (critical > config.Check.MaxCritical) failures.Add($"{critical} critical > {config.Check.MaxCritical}");
    if (violations.Count > config.Check.MaxViolations) failures.Add($"{violations.Count} violations > {config.Check.MaxViolations}");
    if (arch.Cycles.Count > config.Check.MaxCycles) failures.Add($"{arch.Cycles.Count} cycles > {config.Check.MaxCycles}");
    if (complex.Count > 0) failures.Add($"complexity > {config.Check.MaxComplexity}: " +
        string.Join(", ", complex.Take(5).Select(n => $"{Naming.Display(n)} ({n.Complexity})")) + (complex.Count > 5 ? ", …" : ""));
    // Human-authored architecture sections the profile requires (docs/architecture/<name>.md).
    var missingSections = config.ArchitectureSections
        .Where(s => !File.Exists(Path.Combine(root, "docs", "architecture", $"{s}.md"))).ToList();
    if (missingSections.Count > 0) failures.Add($"missing architecture sections: {string.Join(", ", missingSections)}");
    return (failures, missingSections);
}

// First run: discovery → optional TTY selection (scope in memory and/or persisted) → source scan →
// inferred config → analysis → generation → check. The scope is ALWAYS resolved before BuildModel.
// Inferred layer/test config is written after scan so existing setup output remains stable.
// Exit 1 only when a stage fails: a failing check is the baseline to improve on, reported with what to do next.
static async Task<int> SetupCommand(string root, string? profile, bool force, bool ai, bool html, bool refreshHtmlOnVersionChange = false, bool progress = false,
    bool selectProjects = false, bool allProjects = false, bool yes = false, bool aiRatings = false)
{
    var file = Path.Combine(root, "docwizz.yaml");
    var outDir = Path.Combine(root, "docs");
    var existing = File.Exists(file) && !force;
    Config config = null!;
    CodeModel model = null!;
    Setup.Detection detection = null!;
    ProjectDiscovery.Inventory? inventory = null;
    List<DocumentationItem>? findings = null;
    ArchitectureResult? arch = null;
    string configStatus = "";
    bool? passed = null;
    string? selectedConfigStatus = null;
    var selectionStage = selectProjects || allProjects || (SetupProjectSelection.Interactive && !yes && !existing);

    // Named as the stages below, weighted by their usual share of the time; setup logs each finished stage itself.
    List<CliProgress.Stage> barStages = [new("discover", 2, "Discovering")];
    if (selectionStage) barStages.Add(new("select", 1, "Selecting"));
    barStages.AddRange([new("scan", 50, "Scanning code"), new("config", 1, "Configuration"), new("analyze", 2, "Analyzing docs"),
        new("architecture", 2, "Architecture"), new("generate", 30, "Generating docs"), new("check", 2, "Checking")]);
    using var bar = CliProgress.ForConsole(barStages, progress, checklist: false);
    Console.WriteLine($"docwizz setup {Path.GetFullPath(root)}");
    var stages = new List<Setup.Stage>
    {
        new("discover", () =>
        {
            // Always before BuildModel. An existing explicit scope is loaded and validated before language scanning.
            // New configurations remain unfiltered by default; project selection is not inferred.
            config = existing ? Config.Load(root, profile) : Config.Parse(Config.Default, root, profile);
            inventory = ProjectDiscovery.Scan(root, RepoFilesUnscoped(root, config));
            return Setup.DiscoverySummary(inventory);
        }, Required: true),
    };
    if (selectionStage)
        stages.Add(new("select", () =>
        {
            if (inventory is null) throw new ScopeException("setup: discovery did not return a project inventory");
            if (allProjects)
            {
                selectedConfigStatus = existing ? SetupProjectSelection.Save(root, null, profile) : null;
                if (existing) config = Config.Load(root, profile);
                else config.Scope = null;
                return "all projects (explicit scope removed)";
            }
            if (!selectProjects && (existing || !Setup.NeedsProjectSelection(inventory)))
                return "not needed (existing configuration preserved or one unambiguous project)";

            var initial = SetupProjectSelection.Initial(inventory, config.Scope);
            var available = inventory.Projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
            var missing = config.Scope?.Projects.Include.Where(p => !available.Contains(p)).ToList() ?? [];
            if (missing.Count > 0)
                Console.WriteLine("Previously selected projects no longer found: " + string.Join(", ", missing) +
                    " — review and confirm a new selection to update scope:");
            // Cache the read-only candidates and validate each proposed choice before accepting it.
            // Invalid shared-directory selections can be corrected without restarting setup.
            var candidates = RepoFilesUnscoped(root, config);
            SetupProjectSelection.Choice choice;
            using (bar.Suspend())   // the prompt owns the terminal
                choice = SetupProjectSelection.Prompt(inventory, initial, Console.In, Console.Out,
                    selected => { ProjectScope.Resolve(root, SetupProjectSelection.Scope(selected, config.Scope), candidates); });
            if (choice.Cancelled) throw new ScopeException("setup: project selection cancelled; no configuration changed");
            var proposed = SetupProjectSelection.Scope(choice.Projects, config.Scope);
            if (existing)
            {
                selectedConfigStatus = SetupProjectSelection.Save(root, proposed, profile);
                config = Config.Load(root, profile);
            }
            else config.Scope = proposed;
            return $"selected {choice.Projects.Count} projects (references: {proposed.Projects.References})" +
                (existing ? $"; {selectedConfigStatus} docwizz.yaml" : "; scope will be written with generated config");
        }, Required: true));
    stages.AddRange(
    [
        new("scan", () =>
        {
            var (m, files) = BuildModel(root, config, bar);
            model = m;
            var all = RepoFiles(root, config, [".github", ".circleci"]).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
            detection = Setup.Detect(root, model, all, config);
            return $"{files.Count} source files, {model.Nodes.Count} nodes; {Setup.Stack(detection)}";
        }, Required: true),
        new("config", () =>
        {
            configStatus = existing ? selectedConfigStatus ?? "kept" : Setup.WriteConfig(root,
                SetupProjectSelection.WithScope(Setup.Yaml(detection, AppVersion.Number, inventory), config.Scope), force);
            // The generated file only narrows layers and test globs to those files matched; selection ran before scan.
            if (!existing) config = Config.Load(root, profile);
            return $"{configStatus} {file}" + (existing
                ? (configStatus == "kept" ? " (--force to regenerate)" : " (only scope: modified)")
                : $" ({detection.Layers.Count} layers, {detection.TestGlobs.Count} test globs inferred)");
        }, Required: true),
        new("analyze", () =>
        {
            findings = Analyzer.Analyze(model, config);
            return $"coverage {Analyzer.Coverage(findings):0}%, {findings.Count(Analyzer.IsCritical)} critical gaps, profile {config.Profile}";
        }),
        new("architecture", () =>
        {
            arch = Architecture.Check(model, config);
            var security = arch.Violations.Count(v => v.Rule.StartsWith("SEC-"));
            return $"{arch.Violations.Count - security} violations ({Architecture.Failing(arch.Violations, config.Check).Count - security} failing), {arch.Cycles.Count} cycles" +
                (config.Security.Enabled ? $", {security} security findings" : ", security rules off");
        }),
        new("generate", () => Generate(root, outDir, config, ai, html, model, refreshHtmlOnVersionChange: refreshHtmlOnVersionChange, aiRatings: aiRatings).GetAwaiter().GetResult().Line),
        new("check", () =>
        {
            findings ??= Analyzer.Analyze(model, config);
            arch ??= Architecture.Check(model, config);
            var (failures, _) = CheckFailures(root, model, config, findings, arch);
            passed = failures.Count == 0;
            return passed.Value ? "PASS" : "FAIL — " + string.Join("; ", failures);
        }),
    ]);
    var results = Setup.Run(stages, Console.Out, s => bar.Advance(s));

    var failed = results.Where(r => !r.Ok).ToList();
    var rel = (string p) => "./" + Path.GetRelativePath(".", p).Replace('\\', '/');
    var outcome = Setup.Outcome(results, passed, arch);
    Console.WriteLine();
    var skipped = failed.Count(r => r.Status == "skipped");
    Console.WriteLine(outcome switch
    {
        Setup.Status.Success => "Setup complete.",
        Setup.Status.SuccessWithFindings => "Setup complete, with findings: they are about the repository; docwizz itself ran without errors.",
        _ => $"Setup incomplete: {string.Join(", ", failed.Where(r => r.Status == "failed").Select(r => r.Name))} failed" +
            (skipped > 0 ? $", {skipped} stage{(skipped == 1 ? "" : "s")} skipped." : "."),
    });
    Console.WriteLine();
    Console.WriteLine($"Status:         {Setup.Label(outcome)}");
    if (inventory is not null) Console.WriteLine($"Discovery:      {Setup.DiscoverySummary(inventory)}");
    if (config?.Scope is not null) Console.WriteLine($"Scope:          {string.Join(", ", config.Scope.Projects.Include)} (references: {config.Scope.Projects.References})");
    if (detection is not null)
    {
        Console.WriteLine($"Stack:          {Setup.Stack(detection)}");
        Console.WriteLine($"Projects:       {(detection.ProjectFiles.Count == 0 ? "none" : string.Join(", ", detection.ProjectFiles.Select(p => $"{p.Value} {p.Key}")))}");
        Console.WriteLine($"Tests:          {(detection.TestGlobs.Count == 0 && detection.TestProjects == 0 ? "none found" : string.Join(", ", detection.TestGlobs.Select(t => $"{t.Key} ({t.Value} files)").Append(detection.TestProjects > 0 ? $"{detection.TestProjects} test projects" : null).OfType<string>()))}");
        Console.WriteLine($"Deployment:     {(detection.Deployment.Count == 0 ? "none found" : string.Join(", ", detection.Deployment.Take(5)) + (detection.Deployment.Count > 5 ? $", … ({detection.Deployment.Count})" : ""))}");
        Console.WriteLine($"Layers:         {(detection.Layers.Count == 0 ? "none detected" : string.Join(", ", detection.Layers.Select(l => $"{l.Key} ({l.Value.Files})")))}");
    }
    if (configStatus.Length > 0) Console.WriteLine($"Configuration:  {rel(file)} ({configStatus})");
    if (results.Single(r => r.Name == "generate").Ok) Console.WriteLine($"Documentation:  {rel(outDir)}");
    if (findings is not null && config is not null) Console.WriteLine($"Coverage:       {Analyzer.Coverage(findings):0}% (profile {config.Profile})");
    if (arch is not null) Console.WriteLine($"Architecture:   {arch.Violations.Count(v => !v.Rule.StartsWith("SEC-"))} findings, {arch.Cycles.Count} cycles");
    if (arch is not null && config is not null) Console.WriteLine($"Security:       {(config.Security.Enabled ? $"{arch.Violations.Count(v => v.Rule.StartsWith("SEC-"))} findings" : "off")}");
    if (passed is not null) Console.WriteLine($"Check:          {results.Single(r => r.Name == "check").Detail}");
    foreach (var r in failed.Where(r => r.Status == "failed")) Console.WriteLine($"Error:          {r.Name}: {r.Detail}");

    if (detection is not null && config is not null && configStatus.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Follow-up:");
        foreach (var t in Setup.FollowUp(detection, configStatus, config, passed != false)) Console.WriteLine($"  - {t}");
        if (inventory is not null && Setup.NeedsProjectSelection(inventory))
            Console.WriteLine("  - multiple solutions or standalone projects detected: review with docwizz discover " +
                root + " --format json; run docwizz setup " + root +
                " --select-projects in a terminal or configure scope.projects.include in docwizz.yaml");
    }
    Console.WriteLine();
    Console.WriteLine("Next useful actions:");
    foreach (var line in Setup.NextActions(root, outcome, existing)) Console.WriteLine(line);
    return failed.Count == 0 ? 0 : 1;
}

// Package remediation: static by default; --validate runs the repository's build and tests in a temporary copy.
static int Remediate(string root, Config config, string? package, string? to, bool validate, string? since, bool json)
{
    var model = BuildModel(root, config).Model;
    List<Remediation> list;
    try { list = Timings.Measure("remediate", () => Remediations.Plan(root, model, config, package is null ? null : (package, to!))); }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    if (since is not null)
    {
        if (ModelAt(root, since, config) is not { } before) return 1;
        var diff = Diff.Compare(before, model, config);
        list = [.. list.Select(r => r with { Scope = Remediations.Scope(r.Impact, diff.Added.Concat(diff.Changed), model, config) })];
    }
    if (validate)
    {
        // Explicit, and announced: validation executes the repository's build (and so its code), never in the working tree.
        Console.Error.WriteLine("docwizz: --validate runs the configured restore/build/test commands in a temporary copy of " + root);
        list = [.. list.Select(r => r.Actionable ? r with { Validation = Remediations.Validate(root, model, r, config.Remediation, Console.Error) } : r)];
    }
    if (json) Console.WriteLine(JsonSerializer.Serialize(Remediations.Json(list), JsonOptions()));
    else Remediations.Report(list, root, Console.Out);
    // Fails only when an executed check failed; suggestions alone never fail.
    return list.Any(r => r.Validation is { Pass: false }) ? 1 : 0;
}

static int ArchitectureCommand(string root, Config config, bool json)
{
    var model = BuildModel(root, config).Model;
    var arch = Timings.Measure("architecture", () => Architecture.Check(model, config));
    if (json) Console.WriteLine(JsonSerializer.Serialize(arch, JsonOptions()));
    else Architecture.Report(arch, Console.Out);
    return Architecture.Failing(arch.Violations, config.Check).Count > config.Check.MaxViolations || arch.Cycles.Count > config.Check.MaxCycles ? 1 : 0;
}

static List<Node> TooComplex(IEnumerable<Node> nodes, CheckConfig check) =>
    check.MaxComplexity is { } max ? nodes.Where(n => n.Complexity > max).OrderByDescending(n => n.Complexity).ToList() : [];

// Imported load reports attached to endpoints; a run before the endpoint file's last commit is stale. Null without an index.
static LoadReports.Attached? LoadAttached(string root, CodeModel model)
{
    var file = Path.Combine(root, LoadReports.DefaultFile);
    if (!File.Exists(file)) return null;
    try
    {
        return LoadReports.Attach(model, LoadReports.Load(file),
            n => DateTimeOffset.TryParse(Git(root, $"log -1 --format=%cI -- \"{n.File}\"")?.Trim(), out var at) ? at : null);
    }
    catch (Exception e) when (e is ArgumentException or JsonException)
    {
        Console.Error.WriteLine($"docwizz: ⚠ {e.Message}; api.md has no load section");
        return null;
    }
}

static string? Git(string dir, string args) => GitRun(dir, args) is { Exit: 0 } r ? r.Out : null;

// A git command with its exit code and what it said on stderr, for errors that need to say why git failed (#133: a
// bare "unknown ref" left an intermittent CI failure undiagnosable). Exit -1: git couldn't be started. Both streams are
// drained concurrently, so a chatty stderr can't fill its pipe and hang the command.
static (int Exit, string Out, string Err) GitRun(string dir, string args)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err.Result);
    }
    catch (Exception e)
    {
        return (-1, "", $"git could not be started: {e.Message}");
    }
}

// Why a git command failed, in one line: its exit code and the first line it wrote to stderr.
static string GitFailure(string args, (int Exit, string Out, string Err) r) =>
    $"git {args}: " + (r.Exit == -1 ? r.Err : $"exit {r.Exit}" +
        (r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line ? $": {line}" : ""));

// `generate`: the stable one-line result, and the report printed below it.
record GenerateReport(string Line, List<string> Details);
