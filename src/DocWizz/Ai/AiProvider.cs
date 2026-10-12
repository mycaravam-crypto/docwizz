using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

// The model behind --ai. Every provider is self-hosted and connects only through LocalEndpoint, so source code never
// leaves the network whichever server answers. Drafting (AiProse) and product synthesis use this interface, never a
// server's API directly.
interface IAiProvider : IDisposable
{
    // "ollama"; part of what a cached result was produced with.
    string Name { get; }
    string Model { get; }
    Uri Endpoint { get; }

    // One chat completion that should answer with a JSON object: the reply text with any <think> block removed, or null
    // when it is empty. Throws HttpRequestException when the server can't be used at all (not running, not local, no
    // such model), so callers can stop instead of failing once per request.
    Task<string?> Complete(string instructions, string facts);
}

static class AiProviders
{
    // The provider --ai uses, or null when it may not be used; the reason goes to stderr, naming `what` was skipped.
    public static IAiProvider? Create(AiConfig config, string what) => config.Provider switch
    {
        "openai-compatible" => OpenAiCompatibleProvider.FromConfig(config, what),
        _ => OllamaProvider.FromConfig(config, what),
    };

    // "<provider>/<model>" as Create would configure it, without connecting or checking anything: what a cached result
    // is filed under, so a draft from one model isn't taken for another's.
    public static string Tag(AiConfig config) => $"{config.Provider}/{ModelName(config)}";

    // DOCWIZZ_MODEL, else ai.model, else (Ollama only) the default model.
    public static string ModelName(AiConfig config) => Environment.GetEnvironmentVariable("DOCWIZZ_MODEL") is { Length: > 0 } m ? m
        : config.Model ?? (config.Provider == "ollama" ? OllamaProvider.DefaultModel : "");

    // Reasoning models think aloud before answering; only the answer is the reply.
    public static string? Answer(string? text) =>
        Regex.Replace(text ?? "", @"<think>.*?</think>", "", RegexOptions.Singleline).Trim() is { Length: > 0 } t ? t : null;
}

// The one way out to a model server: every connection, redirects and DNS answers included, must resolve to a loopback or
// private-network address, and proxies are never used (a proxy would carry the code to wherever it sends it).
static class LocalEndpoint
{
    public static HttpClient Client(Uri baseAddress, string server, TimeSpan? timeout = null) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || !addresses.All(IsPrivate))
                throw new IOException($"{ctx.DnsEndPoint.Host} is not a local or private address; docwizz only uses a self-hosted {server}");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { BaseAddress = baseAddress, Timeout = timeout ?? TimeSpan.FromMinutes(10) }; // local models on CPU are slow

    // Loopback, RFC 1918, IPv6 unique-local.
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.IsIPv6UniqueLocal) return true;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 192 && b[1] == 168);
    }
}

// A self-hosted Ollama (`/api/chat`). OLLAMA_HOST (else ai.endpoint) picks the server, DOCWIZZ_MODEL (else ai.model) the
// model; Ollama's cloud models are refused because they run on ollama.com.
sealed class OllamaProvider(string model, Uri endpoint) : IAiProvider
{
    public const string DefaultModel = "qwen2.5-coder:7b";

    readonly HttpClient client = LocalEndpoint.Client(endpoint, "Ollama");

    public string Name => "ollama";
    public string Model => model;
    public Uri Endpoint => endpoint;

    // The configured model (default qwen2.5-coder:7b) at the configured host, or null when the model is one of Ollama's
    // `…-cloud` / `…:cloud` models.
    public static OllamaProvider? FromConfig(AiConfig config, string what)
    {
        var name = AiProviders.ModelName(config);
        if (IsCloud(name))
        {
            Console.Error.WriteLine($"docwizz: AI {what} skipped — {name} is an Ollama cloud model; use a local one");
            return null;
        }
        return new(name, Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } host ? host : config.Endpoint));
    }

    public static bool IsCloud(string model) => model.Split(':').Last().EndsWith("cloud", StringComparison.OrdinalIgnoreCase);

    // OLLAMA_HOST as Ollama itself reads it: `host`, `host:port` or a URL; default localhost:11434.
    public static Uri Uri(string? host)
    {
        var v = host is { Length: > 0 } h ? h.Trim() : "localhost";
        var b = new UriBuilder(v.Contains("://") ? v : "http://" + v);
        if (!Regex.IsMatch(v, @":\d+(/|$)")) b.Port = 11434;
        if (b.Host is "0.0.0.0" or "[::]") b.Host = "localhost"; // bind-all address, as the server is often configured
        b.Path = b.Path.TrimEnd('/') + "/";
        return b.Uri;
    }

    public async Task<string?> Complete(string instructions, string facts)
    {
        using var response = await client.PostAsync("api/chat", new StringContent(JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            format = "json",
            options = new { temperature = 0 },
            messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = facts } },
        }), System.Text.Encoding.UTF8, "application/json"));
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException($"model {model} not found (ollama pull {model}, or set DOCWIZZ_MODEL)");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return AiProviders.Answer(body.GetProperty("message").GetProperty("content").GetString());
    }

    public void Dispose() => client.Dispose();
}

// A self-hosted server with an OpenAI-compatible API (`<endpoint>/chat/completions`): vLLM, NVIDIA NIM, Nutanix
// Enterprise AI, a LiteLLM gateway. Same guard as Ollama: only loopback or private addresses, no proxy. Which hosts are
// allowed follows from the address alone (an allowlist of private ranges), so no public provider can be configured by
// accident. The API key comes from the environment variable ai.api_key_env names and is only ever sent as a header.
sealed class OpenAiCompatibleProvider : IAiProvider
{
    readonly HttpClient client;
    readonly TimeSpan timeout;
    // Some servers reject `response_format`; after the first 400 the JSON object is asked for by the prompt alone.
    bool jsonMode = true;

    public string Name => "openai-compatible";
    public string Model { get; }
    public Uri Endpoint { get; }

    public OpenAiCompatibleProvider(string model, Uri endpoint, string? apiKey, TimeSpan? timeout = null)
    {
        Model = model;
        Endpoint = new UriBuilder(endpoint) { Path = endpoint.AbsolutePath.TrimEnd('/') + "/" }.Uri;
        this.timeout = timeout ?? TimeSpan.FromMinutes(10);
        client = LocalEndpoint.Client(Endpoint, "model server", this.timeout);
        if (apiKey is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    // ai.endpoint and ai.model (DOCWIZZ_MODEL overrides the model), with the key from ai.api_key_env; null, with the reason,
    // when that variable is named but not set.
    public static OpenAiCompatibleProvider? FromConfig(AiConfig config, string what)
    {
        string? key = null;
        if (config.ApiKeyEnv is { } env && (key = Environment.GetEnvironmentVariable(env)) is not { Length: > 0 })
        {
            Console.Error.WriteLine($"docwizz: AI {what} skipped — ai.api_key_env names {env}, which is not set");
            return null;
        }
        return new(AiProviders.ModelName(config), new Uri(config.Endpoint!), key);
    }

    public async Task<string?> Complete(string instructions, string facts)
    {
        while (true)
        {
            var request = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["temperature"] = 0,
                ["stream"] = false,
                ["messages"] = new[] { new { role = "system", content = instructions }, new { role = "user", content = facts } },
            };
            if (jsonMode) request["response_format"] = new { type = "json_object" };
            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync("chat/completions",
                    new StringContent(JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json"));
            }
            // The timeout can fire while connecting too, where it arrives wrapped in an HttpRequestException.
            catch (Exception e) when (e is TaskCanceledException || e.InnerException is OperationCanceledException)
            {
                throw new HttpRequestException($"{Endpoint} did not answer within {timeout.TotalSeconds:0.#} s");
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.BadRequest && jsonMode)
                {
                    jsonMode = false;
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new HttpRequestException($"{Endpoint} refused the request ({(int)response.StatusCode}): check the key in ai.api_key_env");
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new HttpRequestException($"{Endpoint}chat/completions or model {Model} not found (check ai.endpoint, which usually ends in /v1, and ai.model)");
                response.EnsureSuccessStatusCode();
                try
                {
                    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                    return AiProviders.Answer(body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
                {
                    throw new HttpRequestException($"{Endpoint} did not answer like an OpenAI-compatible server ({e.Message})");
                }
            }
        }
    }

    public void Dispose() => client.Dispose();
}
