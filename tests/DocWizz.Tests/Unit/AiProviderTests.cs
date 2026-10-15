using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DocWizz.Tests.Unit;

// The --ai provider seam: the endpoint guard every provider connects through, and the Ollama provider against a fake
// server. test.sh covers the same rules end to end (drafts, a public host, a cloud model).
public class AiProviderTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:192.168.1.10", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_loopback_and_private_addresses_are_local(string address, bool local) =>
        Assert.Equal(local, LocalEndpoint.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData(null, "http://localhost:11434/")]
    [InlineData("gpu-box", "http://gpu-box:11434/")]
    [InlineData("10.0.0.5:8080", "http://10.0.0.5:8080/")]
    [InlineData("0.0.0.0", "http://localhost:11434/")]
    [InlineData("https://llm.internal/ollama", "https://llm.internal:11434/ollama/")]
    public void Ollama_host_is_read_the_way_ollama_reads_it(string? host, string uri) =>
        Assert.Equal(uri, OllamaProvider.Uri(host).ToString());

    [Theory]
    [InlineData("gpt-oss:120b-cloud", true)]
    [InlineData("qwen3-coder:480b-cloud", true)]
    [InlineData("deepseek:cloud", true)]
    [InlineData("qwen2.5-coder:7b", false)]
    [InlineData("cloudy-model:7b", false)]
    public void Ollama_cloud_models_are_recognised(string model, bool cloud) => Assert.Equal(cloud, OllamaProvider.IsCloud(model));

    // An HTTP server on loopback: answers each request with the next of `replies` (the last one repeats; status 0 = never
    // answer), and records each request's first line, headers and body.
    internal sealed class FakeServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public List<string> Requests { get; } = [];
        public List<string> Heads { get; } = [];
        public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

        public FakeServer(int status, string body) : this((status, body)) { }

        public FakeServer(params (int Status, string Body)[] replies)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var c = await listener.AcceptTcpClientAsync();
                        // For a silent server, keep the connection open without parsing a request.
                        // This isolates the timeout test from unrelated request-parser failures.
                        if (replies.Length == 1 && replies[0].Status == 0)
                        {
                            await Task.Delay(Timeout.Infinite);
                            continue;
                        }
                        var stream = c.GetStream();
                        var reader = new StreamReader(stream, Encoding.UTF8);
                        var length = 0;
                        var head = new StringBuilder();
                        for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                        {
                            head.AppendLine(line);
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                        }
                        var buffer = new char[length];
                        var read = 0;
                        while (read < length) read += await reader.ReadAsync(buffer, read, length - read);
                        int n;
                        lock (Requests) { Requests.Add(new string(buffer)); Heads.Add(head.ToString()); n = Requests.Count; }
                        var (status, body) = replies[Math.Min(n, replies.Length) - 1];
                        if (status == 0) { await Task.Delay(Timeout.Infinite); continue; }
                        var bytes = Encoding.UTF8.GetBytes(body);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(bytes);
                    }
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException) { }
            });
        }

        public void Dispose() => listener.Stop();
    }

    [Fact]
    public async Task Ollama_sends_a_json_chat_at_temperature_0_and_returns_the_answer_without_thinking()
    {
        using var server = new FakeServer(200, """{"message":{"content":"<think>hmm</think>{\"summary\":[]}"}}""");
        using var ai = new OllamaProvider("local:7b", server.Uri);
        Assert.Equal(("ollama", "local:7b"), (ai.Name, ai.Model));
        Assert.Equal("""{"summary":[]}""", await ai.Complete("You write docs.", """{"symbol":"A"}"""));
        var request = System.Text.Json.JsonDocument.Parse(Assert.Single(server.Requests)).RootElement;
        Assert.Equal("local:7b", request.GetProperty("model").GetString());
        Assert.Equal("json", request.GetProperty("format").GetString());
        Assert.False(request.GetProperty("stream").GetBoolean());
        Assert.Equal(0, request.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal(16384, request.GetProperty("options").GetProperty("num_ctx").GetInt32());   // not Ollama's cutting default
        Assert.Equal("system", request.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Without_json_neither_server_is_held_to_a_json_object()
    {
        using (var server = new FakeServer(200, """{"message":{"content":"summary: Does it. [source]"}}"""))
        using (var ai = new OllamaProvider("local:7b", server.Uri))
        {
            Assert.Equal("summary: Does it. [source]", await ai.Complete("i", "f", json: false));
            Assert.False(System.Text.Json.JsonDocument.Parse(Assert.Single(server.Requests)).RootElement.TryGetProperty("format", out _));
        }
        using (var server = new FakeServer(200, Chat("summary: Does it. [source]")))
        using (var ai = new OpenAiCompatibleProvider("m", server.Uri, null))
        {
            Assert.Equal("summary: Does it. [source]", await ai.Complete("i", "f", json: false));
            Assert.False(System.Text.Json.JsonDocument.Parse(Assert.Single(server.Requests)).RootElement.TryGetProperty("response_format", out _));
        }
    }

    [Fact]
    public async Task A_missing_model_or_a_failing_server_is_an_http_error_that_stops_drafting()
    {
        using (var missing = new FakeServer(404, "{}"))
        using (var ai = new OllamaProvider("nope:1b", missing.Uri))
            Assert.Contains("model nope:1b not found", (await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"))).Message);
        using (var broken = new FakeServer(500, "{}"))
        using (var ai = new OllamaProvider("local:7b", broken.Uri))
            await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"));
        using (var empty = new FakeServer(200, """{"message":{"content":"<think>only thinking</think>"}}"""))
        using (var ai = new OllamaProvider("local:7b", empty.Uri))
            Assert.Null(await ai.Complete("i", "f"));
    }

    [Fact]
    public async Task A_public_address_is_refused_before_anything_is_sent()
    {
        using var ai = new OllamaProvider("local:7b", new Uri("http://8.8.8.8:11434/"));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"));
        Assert.Contains("8.8.8.8 is not a local or private address; docwizz only uses a self-hosted Ollama", e.InnerException?.Message ?? e.Message);
    }

    static string Chat(string content) => System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Fact]
    public async Task OpenAI_compatible_posts_chat_completions_with_json_mode_and_the_key_as_a_bearer_header()
    {
        using var server = new FakeServer(200, Chat("""{"summary":[]}"""));
        using var ai = new OpenAiCompatibleProvider("qwen3-coder-next", new Uri(server.Uri, "v1"), "s3cret");
        Assert.Equal(("openai-compatible", "qwen3-coder-next"), (ai.Name, ai.Model));
        Assert.Equal("""{"summary":[]}""", await ai.Complete("You write docs.", "{}"));
        var head = Assert.Single(server.Heads);
        Assert.StartsWith("POST /v1/chat/completions ", head);
        Assert.Contains("Authorization: Bearer s3cret", head);
        var request = System.Text.Json.JsonDocument.Parse(server.Requests[0]).RootElement;
        Assert.Equal("qwen3-coder-next", request.GetProperty("model").GetString());
        Assert.Equal(0, request.GetProperty("temperature").GetInt32());
        Assert.Equal("json_object", request.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("user", request.GetProperty("messages")[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_server_without_json_mode_is_asked_again_by_prompt_alone()
    {
        using var server = new FakeServer((400, """{"error":"response_format not supported"}"""), (200, Chat("<think>x</think>Plain.")));
        using var ai = new OpenAiCompatibleProvider("m", server.Uri, null);
        Assert.Equal("Plain.", await ai.Complete("i", "f"));
        Assert.Equal(2, server.Requests.Count);
        Assert.False(System.Text.Json.JsonDocument.Parse(server.Requests[1]).RootElement.TryGetProperty("response_format", out _));
        Assert.DoesNotContain("Authorization", server.Heads[0]);   // no key configured, none sent
    }

    [Theory]
    [InlineData(401, "refused the request (401): check the key in ai.api_key_env")]
    [InlineData(404, "or model m not found")]
    [InlineData(500, "500")]
    public async Task Server_errors_stop_drafting_with_what_to_fix(int status, string message)
    {
        using var server = new FakeServer(status, "{}");
        using var ai = new OpenAiCompatibleProvider("m", server.Uri, "k");
        Assert.Contains(message, (await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"))).Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"no":"choices"}""")]
    [InlineData("""{"choices":[]}""")]
    public async Task A_reply_that_is_not_an_openai_answer_is_an_http_error(string body)
    {
        using var server = new FakeServer(200, body);
        using var ai = new OpenAiCompatibleProvider("m", server.Uri, null);
        Assert.Contains("did not answer like an OpenAI-compatible server", (await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"))).Message);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_reports_an_http_error()
    {
        using var server = new FakeServer(0, "");
        using var ai = new OpenAiCompatibleProvider("m", server.Uri, null, TimeSpan.FromSeconds(2));
        // Some CI networking layers abort loopback connections before the configured timeout.
        // Both outcomes must surface as HTTP errors; do not require a specific transport diagnostic.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task An_openai_compatible_public_host_is_refused_like_any_other()
    {
        using var ai = new OpenAiCompatibleProvider("gpt-4o", new Uri("http://8.8.8.8/v1"), "k");
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => ai.Complete("i", "f"));
        Assert.Contains("8.8.8.8 is not a local or private address", e.InnerException?.Message ?? e.Message);
    }

    [Fact]
    public void A_named_key_variable_that_is_not_set_skips_ai_and_says_so()
    {
        var config = new AiConfig { Provider = "openai-compatible", Endpoint = "http://10.0.0.5:8000/v1", Model = "m", ApiKeyEnv = "DOCWIZZ_TEST_KEY_THAT_IS_NEVER_SET" };
        var err = new StringWriter();
        var old = Console.Error;
        Console.SetError(err);
        try { Assert.Null(AiProviders.Create(config, "summaries")); }
        finally { Console.SetError(old); }
        Assert.Contains("ai.api_key_env names DOCWIZZ_TEST_KEY_THAT_IS_NEVER_SET, which is not set", err.ToString());
    }

    [Theory]
    [InlineData("ai: { provider: openai }", "ai.provider: 'openai' (ollama or openai-compatible)")]
    [InlineData("ai: { provider: openai-compatible, model: m }", "needs endpoint")]
    [InlineData("ai: { provider: openai-compatible, endpoint: 'ftp://x/v1', model: m }", "is not an http(s) URL")]
    [InlineData("ai: { api_key_env: 'sk-abc-123' }", "is not an environment variable name")]
    public void Ai_configuration_is_checked_when_it_is_loaded(string yaml, string error) =>
        Assert.Contains(error, Assert.Throws<ArgumentException>(() => Config.Parse(yaml, ".")).Message);

    [Fact]
    public void An_api_key_cannot_be_written_into_the_config()
    {
        var e = Assert.ThrowsAny<Exception>(() => Config.Parse("ai: { api_key: sk-abc }", "."));
        Assert.Contains("api_key", e.Message);
    }

    [Fact]
    public void Cached_results_are_filed_by_provider_and_model()
    {
        var cache = new Dictionary<string, string>
        {
            ["a@h1"] = "legacy ollama",
            ["b@h2@openai-compatible/m1"] = "from m1",
        };
        Assert.Equal("legacy ollama", AiProse.Cached(cache, "a@h1", "ollama/qwen2.5-coder:7b"));   // older drafts were Ollama's
        Assert.Null(AiProse.Cached(cache, "a@h1", "openai-compatible/m1"));                       // another provider drafts afresh
        Assert.Equal("from m1", AiProse.Cached(cache, "b@h2", "openai-compatible/m1"));
        Assert.Null(AiProse.Cached(cache, "b@h2", "openai-compatible/m2"));
        Assert.Equal("from m1", AiProse.Cached(cache, "b@h2", null));                              // without --ai: any draft
        Assert.Equal("openai-compatible/m", AiProviders.Tag(new AiConfig { Provider = "openai-compatible", Model = "m" }));
    }

    [Fact]
    public async Task Each_draft_is_cached_as_it_arrives_so_an_interrupted_run_keeps_it()
    {
        // The first answer arrives, the second never does: the run is still going when the first draft must be on disk.
        using var server = new FakeServer((200, """{"message":{"content":"Places an order."}}"""), (0, ""));
        using var dir = new Sources();
        var cacheFile = Path.Combine(dir.Root, "ai-cache.json");
        var model = Models.Model([
            Models.N("cs:A.Orders.Place(int)", "a/Orders.cs", kind: "method", hash: "h1", tags: ["endpoint", "POST"], route: "api/orders", parameters: ["id: int"]),
            Models.N("cs:B.Refunds.Refund(int)", "b/Refunds.cs", kind: "method", hash: "h2", tags: ["endpoint", "POST"], route: "api/refunds", parameters: ["id: int"]),
        ]);
        var findings = Analyzer.Analyze(model, Models.Config());
        Assert.Equal(2, findings.Count(f => f.Missing.Contains("summary")));
        _ = AiProse.Summaries(dir.Root, model, findings, cacheFile, call: true, new AiConfig { Endpoint = server.Uri.ToString(), Model = "local:7b" });

        for (var i = 0; i < 100 && !File.Exists(cacheFile); i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Contains("Places an order.", File.ReadAllText(cacheFile));
        Assert.False(File.Exists(cacheFile + ".tmp"));
    }

    [Fact]
    public async Task Ratings_are_saved_before_completion_is_reported_and_reused_without_a_server()
    {
        using var server = new FakeServer(200, """{"message":{"content":"{\"score\":2,\"missing\":[\"purpose\"],\"note\":\"Explain why.\"}"}}""");
        using var dir = new Sources();
        var cacheFile = Path.Combine(dir.Root, "ai-assessments.json");
        var model = Models.Model([
            Models.N("cs:A.Orders", "a/Orders.cs", doc: Models.Doc("Places orders."), hash: "h1", tags: ["controller"]),
            Models.N("cs:B.Refunds", "b/Refunds.cs", doc: Models.Doc("Refunds orders."), hash: "h2", tags: ["controller"]),
        ]);
        var findings = Analyzer.Analyze(model, Models.Config());
        var config = new AiConfig { Endpoint = server.Uri.ToString(), Model = "local:7b" };
        var reported = 0;
        var ratings = await AiProse.Assessments(dir.Root, model, findings, cacheFile, call: true, config,
            (done, _, _) =>
            {
                if (done == 0) return;
                // This callback runs before Ask returns the whole batch. The old batch-only save fails here.
                var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AiProse.Assessment>>(
                    File.ReadAllText(cacheFile), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
                Assert.True(saved.Count >= done);
                Interlocked.Increment(ref reported);
            });
        Assert.Equal(2, ratings.Count);
        Assert.True(reported >= 2);
        Assert.False(File.Exists(cacheFile + ".tmp"));
        server.Dispose();

        var cached = await AiProse.Assessments(dir.Root, model, findings, cacheFile, call: false, config);
        Assert.Equal(ratings.Keys.Order(), cached.Keys.Order());
        foreach (var (id, rating) in ratings)
        {
            Assert.Equal(rating.Score, cached[id].Score);
            Assert.Equal(rating.Missing, cached[id].Missing);
            Assert.Equal(rating.Note, cached[id].Note);
        }
        var unchanged = await AiProse.Assessments(dir.Root, model, findings, cacheFile, call: true, config);
        Assert.Equal(cached.Keys.Order(), unchanged.Keys.Order());
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Ratings_are_invalidated_by_documentation_code_or_model_changes()
    {
        using var server = new FakeServer(200, """{"message":{"content":"{\"score\":3}"}}""");
        using var dir = new Sources();
        var cacheFile = Path.Combine(dir.Root, "ai-assessments.json");
        var node = Models.N("cs:Orders", "Orders.cs", doc: Models.Doc("Places orders."), hash: "h1", tags: ["controller"]);
        var config = new AiConfig { Endpoint = server.Uri.ToString(), Model = "local:7b" };
        async Task Rate(Node n, AiConfig c)
        {
            var model = Models.Model([n]);
            Assert.Single(await AiProse.Assessments(dir.Root, model, Analyzer.Analyze(model, Models.Config()), cacheFile, call: true, c));
        }
        await Rate(node, config);
        await Rate(node, config);
        Assert.Single(server.Requests);
        await Rate(node with { Doc = Models.Doc("Creates an order.") }, config);
        Assert.Equal(2, server.Requests.Count);
        await Rate(node with { Hash = "h2" }, config);
        Assert.Equal(3, server.Requests.Count);
        await Rate(node, new AiConfig { Endpoint = server.Uri.ToString(), Model = "other:7b" });
        Assert.Equal(4, server.Requests.Count);
    }
}
