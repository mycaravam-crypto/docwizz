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

    // A one-shot HTTP server on loopback: answers every request with `status` and `body`, records the request bodies.
    sealed class FakeServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public List<string> Requests { get; } = [];
        public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

        public FakeServer(int status, string body)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var c = await listener.AcceptTcpClientAsync();
                        var stream = c.GetStream();
                        var reader = new StreamReader(stream, Encoding.UTF8);
                        var length = 0;
                        for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                        var buffer = new char[length];
                        var read = 0;
                        while (read < length) read += await reader.ReadAsync(buffer, read, length - read);
                        lock (Requests) Requests.Add(new string(buffer));
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
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
        Assert.Equal("system", request.GetProperty("messages")[0].GetProperty("role").GetString());
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
}
