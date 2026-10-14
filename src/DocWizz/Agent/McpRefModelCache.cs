// MCP-session cache only: a resolved commit is immutable, while a ref name (including HEAD) may move.
// Resolve on every request and cache only successful scans; CLI commands retain their existing behavior.
sealed class McpRefModelCache
{
    readonly Dictionary<(string Sha, string Scope), CodeModel> models = new();

    public CodeModel? Get(string gitRef, string scope, Func<string, string?> resolve, Func<string, CodeModel?> scan)
    {
        var sha = resolve(gitRef);
        if (sha is null || !IsFullSha(sha)) return null;
        var key = (sha, scope);
        if (models.TryGetValue(key, out var model)) return model;
        model = scan(sha);
        if (model is not null) models[key] = model;
        return model;
    }

    static bool IsFullSha(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
