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
    public static IAiProvider? Create(string what) => OllamaProvider.FromEnvironment(what);

    // Reasoning models think aloud before answering; only the answer is the reply.
    public static string? Answer(string? text) =>
        Regex.Replace(text ?? "", @"<think>.*?</think>", "", RegexOptions.Singleline).Trim() is { Length: > 0 } t ? t : null;
}

// The one way out to a model server: every connection, redirects and DNS answers included, must resolve to a loopback or
// private-network address, and proxies are never used (a proxy would carry the code to wherever it sends it).
static class LocalEndpoint
{
    public static HttpClient Client(Uri baseAddress, string server) => new(new SocketsHttpHandler
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
    }) { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) }; // local models on CPU are slow

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

// A self-hosted Ollama (`/api/chat`). OLLAMA_HOST picks the server, DOCWIZZ_MODEL the model; Ollama's cloud models are
// refused because they run on ollama.com.
sealed class OllamaProvider(string model, Uri endpoint) : IAiProvider
{
    public const string DefaultModel = "qwen2.5-coder:7b";

    readonly HttpClient client = LocalEndpoint.Client(endpoint, "Ollama");

    public string Name => "ollama";
    public string Model => model;
    public Uri Endpoint => endpoint;

    // DOCWIZZ_MODEL (default qwen2.5-coder:7b) at OLLAMA_HOST, or null when the model is one of Ollama's `…-cloud` /
    // `…:cloud` models.
    public static OllamaProvider? FromEnvironment(string what)
    {
        var name = Environment.GetEnvironmentVariable("DOCWIZZ_MODEL") is { Length: > 0 } m ? m : DefaultModel;
        if (IsCloud(name))
        {
            Console.Error.WriteLine($"docwizz: AI {what} skipped — {name} is an Ollama cloud model; use a local one");
            return null;
        }
        return new(name, Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST")));
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
