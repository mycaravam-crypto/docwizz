using System.Text.Json;

// Opt-in synthetic inference probe. Never sends source code or writes the documentation cache.
static class AiCheck
{
    public static async Task<int> Run(AiConfig config, bool json)
    {
        var providerName = config.Provider;
        var model = AiProviders.ModelName(config);
        string? endpoint = null;
        string? failure = null;
        bool usable = false;

        try
        {
            using var provider = AiProviders.Create(config, "check");
            if (provider is null)
            {
                failure = "AI provider rejected the configuration (cloud model or missing API key); inspect stderr and ai settings.";
            }
            else
            {
                endpoint = new UriBuilder(provider.Endpoint) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString();
                // An actual generation request checks more than TCP connectivity or model inventory.
                // No source files, configuration values or secrets are included in the prompt.
                var response = await provider.Complete(
                    "You are a diagnostic. Reply with a short nonempty acknowledgement. Do not need external context.",
                    "Connectivity test: respond with an acknowledgement.",
                    json: false).WaitAsync(TimeSpan.FromSeconds(30));
                usable = !string.IsNullOrWhiteSpace(response);
                if (!usable) failure = "The model returned an empty response.";
            }
        }
        catch (TimeoutException)
        {
            failure = "Model inference did not finish within 30 seconds.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            // Do not print exception details: upstream servers might echo authorization headers or tokens.
            failure = "Cannot complete a local inference request. Check provider, endpoint, model, credentials and server logs.";
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { usable, provider = providerName, model, endpoint, error = failure }));
        else
        {
            Console.WriteLine($"AI check: {(usable ? "USABLE" : "UNAVAILABLE")}");
            Console.WriteLine($"Provider: {providerName}");
            Console.WriteLine($"Model: {model}");
            if (endpoint is not null) Console.WriteLine($"Endpoint: {endpoint}");
            if (failure is not null) Console.WriteLine($"Reason: {failure}");
        }
        return usable ? 0 : 1;
    }
}
