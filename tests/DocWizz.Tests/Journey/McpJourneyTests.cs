using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocWizz.Tests.Journey;

// `docwizz mcp` as an MCP client sees it: a process speaking JSON-RPC over stdin/stdout, against a copy of the fixture
// made into a git repository (fixed dates, so its commit is the same on every run) with one change on top. Each tool's
// answer is compared with a golden file (golden/mcp/<tool>.json; DOCWIZZ_UPDATE_GOLDEN=1 rewrites them). The server must
// leave the working tree exactly as it found it.
public class McpJourneyTests
{
    static readonly string Root = FindRoot();
    static readonly string Golden = Path.Combine(Root, "tests", "DocWizz.Tests", "Journey", "golden", "mcp");

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "fixture")) && File.Exists(Path.Combine(dir.FullName, "CLI.md"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found above the test binary");
    }

    sealed class Repo : IDisposable
    {
        public string Dir { get; }

        public Repo()
        {
            Dir = Path.Combine(Directory.CreateTempSubdirectory("docwizz-mcp-").FullName, "fixture");
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "fixture"), "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Path.Combine(Root, "fixture"), f);
                if (rel.Split(Path.DirectorySeparatorChar).Any(d => d is "node_modules" or "docs" or "bin" or "obj")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Dir, rel))!);
                File.Copy(f, Path.Combine(Dir, rel));
            }
            Git("init -q -b main");
            Git("add -A");
            Git("-c user.name=docwizz -c user.email=docwizz@example.com commit -q -m fixture");
            // One change on top of the commit, for impact and check --since.
            var service = Path.Combine(Dir, "backend", "Application", "MaterialService.cs");
            File.WriteAllText(service, File.ReadAllText(service).TrimEnd().TrimEnd('}') + "\n    public int Count(int max) => max > 0 ? max : 0;\n}\n");
        }

        void Git(string args)
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var v in new[] { "GIT_AUTHOR_DATE", "GIT_COMMITTER_DATE" }) psi.Environment[v] = "2026-01-01T00:00:00Z";
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {args}: {p.StandardError.ReadToEnd()}");
        }

        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Dir)!, recursive: true);
    }

    // A running server: send a request, read its response (responses come back in order, one per line).
    sealed class Server : IDisposable
    {
        readonly Process process;
        int next;

        public Server(string dir, params string[] options)
        {
            var psi = new ProcessStartInfo(Docwizz.Dotnet, [typeof(Cli).Assembly.Location, "mcp", dir, .. options])
            {
                WorkingDirectory = dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.Environment.Remove("DOCWIZZ_DEBUG");
            process = Process.Start(psi)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
        }

        public JsonObject Raw(string line)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
            var reply = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            return JsonNode.Parse(reply ?? throw new InvalidOperationException("server closed stdout"))!.AsObject();
        }

        public JsonObject Request(string method, JsonObject? args = null) =>
            Raw(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++next, ["method"] = method, ["params"] = args ?? [] }.ToJsonString());

        public void Notify(string method) => process.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString());

        // A tool's structured answer, and whether it is an error.
        public (JsonObject Body, bool Error) Call(string tool, JsonObject? arguments = null)
        {
            var result = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] })["result"]!.AsObject();
            Assert.Equal(result["structuredContent"]!.ToJsonString(), JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.ToJsonString());
            return (result["structuredContent"]!.AsObject(), result["isError"]!.GetValue<bool>());
        }

        public int Stop()
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(TimeSpan.FromMinutes(1))) process.Kill(entireProcessTree: true);
            return process.ExitCode;
        }

        public void Dispose() { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
    }

    static Dictionary<string, string> Snapshot(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Where(f => !Path.GetRelativePath(dir, f).StartsWith(".git"))
        .ToDictionary(f => Path.GetRelativePath(dir, f), f => $"{File.GetLastWriteTimeUtc(f):O} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))}");

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static void MatchesGolden(string tool, JsonObject body)
    {
        // The commit depends on git's object format, not on docwizz: compare everything else.
        var text = Regex.Replace(body.ToJsonString(Indented), @"""(commit|head)"": ""[0-9a-f]{7,40}""", @"""$1"": ""<commit>""") + "\n";
        var golden = Path.Combine(Golden, $"{tool}.json");
        if (Environment.GetEnvironmentVariable("DOCWIZZ_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Golden);
            File.WriteAllText(golden, text);
        }
        Assert.Equal(File.ReadAllText(golden), text);
    }

    [Fact]
    public void Every_tool_answers_from_the_model_matches_its_golden_file_and_writes_nothing()
    {
        Docwizz.RequireFrontendScanner();
        using var repo = new Repo();
        var before = Snapshot(repo.Dir);
        using var server = new Server(repo.Dir);

        var init = server.Request("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" } });
        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("docwizz", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        server.Notify("notifications/initialized");

        var tools = server.Request("tools/list")["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["find_symbol", "context", "callers", "callees", "trace_endpoint", "module_summary", "impact", "gaps", "violations", "check", "rescan"], tools);
        Assert.DoesNotContain("generate", tools);
        Assert.DoesNotContain("setup", tools);

        var calls = new (string Tool, JsonObject Args)[]
        {
            ("find_symbol", new() { ["query"] = "MaterialService", ["limit"] = 5 }),
            ("context", new() { ["target"] = "POST /api/materials", ["budget"] = 1500 }),
            ("callers", new() { ["id"] = "Fixture.Application.IMaterialService" }),
            ("callees", new() { ["id"] = "Fixture.Application.MaterialService.CreateAsync" }),
            ("trace_endpoint", new() { ["verb"] = "GET", ["route"] = "/api/stock/{sku}" }),
            ("module_summary", new() { ["path"] = "backend/Application" }),
            ("impact", new()),
            ("gaps", new() { ["path"] = "backend/Api", ["limit"] = 3 }),
            ("violations", new() { ["path"] = "backend" }),
            ("check", new() { ["since"] = "HEAD" }),
        };
        foreach (var (tool, args) in calls)
        {
            var (body, error) = server.Call(tool, args);
            Assert.False(error, $"{tool}: {body}");
            Assert.False(body["model"]!["stale"]!.GetValue<bool>(), $"{tool}: {body["model"]}");
            MatchesGolden(tool, body);
        }
        // Answers stay within their budget, and say what was left out.
        var (small, _) = server.Call("impact", new() { ["budget"] = 200 });
        Assert.True(small.ToJsonString().Length <= 200 * 4 + 200, $"{small.ToJsonString().Length} characters");
        Assert.NotNull(small["truncated"]);
        Assert.Equal(0, server.Stop());
        Assert.Equal(before, Snapshot(repo.Dir));   // read-only: nothing written, nothing touched
    }

    [Fact]
    public void A_stale_model_is_reported_until_rescan_and_bad_requests_are_errors_not_crashes()
    {
        Docwizz.RequireFrontendScanner();
        using var repo = new Repo();
        using var server = new Server(repo.Dir);
        server.Request("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }); // unknown version: the server's own
        Assert.Equal(McpServer.ProtocolVersion, server.Request("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" })["result"]!["protocolVersion"]!.GetValue<string>());

        File.SetLastWriteTimeUtc(Path.Combine(repo.Dir, "backend", "Domain", "Material.cs"), DateTime.UtcNow.AddMinutes(5));
        var (stale, _) = server.Call("find_symbol", new() { ["query"] = "Material" });
        Assert.True(stale["model"]!["stale"]!.GetValue<bool>());
        Assert.Contains("1 source file changed since the model was loaded", stale["model"]!["staleBecause"]!.ToJsonString());
        var (rescan, rescanError) = server.Call("rescan");
        Assert.False(rescanError);
        Assert.True(rescan["nodes"]!.GetValue<int>() > 0);

        var (unknown, isError) = server.Call("context", new() { ["target"] = "Fixture.Nope" });
        Assert.True(isError);
        Assert.Contains("no symbol, file, folder or endpoint matches Fixture.Nope", unknown["error"]!.GetValue<string>());
        Assert.True(server.Call("gaps", new() { ["limit"] = "many" }).Error);
        Assert.Equal(-32601, server.Request("resources/list")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, server.Request("tools/call", new JsonObject { ["name"] = "generate" })["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, server.Raw("{not json")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(0, server.Stop());
    }

    [Fact]
    public void Auto_rescan_answers_from_fresh_code()
    {
        Docwizz.RequireFrontendScanner();
        using var repo = new Repo();
        using var server = new Server(repo.Dir, "--auto-rescan");
        var file = Path.Combine(repo.Dir, "backend", "Domain", "Pallet.cs");
        File.WriteAllText(file, "namespace Fixture.Domain;\n\npublic class Pallet { }\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var (body, _) = server.Call("find_symbol", new() { ["query"] = "Pallet" });
        Assert.False(body["model"]!["stale"]!.GetValue<bool>());
        Assert.Equal("a rescan", body["model"]!["source"]!.GetValue<string>());
        Assert.Equal("cs:Fixture.Domain.Pallet", body["matches"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(0, server.Stop());
    }
}
