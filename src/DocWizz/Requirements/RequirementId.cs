using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Azure Boards retains its numeric JSON representation. GitHub identities include the repository, so neither
// another provider nor another repository can accidentally supply the text of an unrelated issue.
[JsonConverter(typeof(RequirementId.Converter))]
readonly record struct RequirementId(int Number, string? Repository = null) : IComparable<RequirementId>
{
    public bool IsGithub => Repository is not null;
    public string Display => IsGithub ? $"github#{Number}" : $"WI-{Number}";
    public string Reference => IsGithub ? $"github#{Number}" : $"AB#{Number}";
    public override string ToString() => IsGithub ? $"github:{Repository}#{Number}" : Number.ToString(CultureInfo.InvariantCulture);
    public int CompareTo(RequirementId other)
    {
        var scope = string.Compare(Repository, other.Repository, StringComparison.Ordinal);
        return scope != 0 ? scope : Number.CompareTo(other.Number);
    }
    public static implicit operator RequirementId(int number) => new(number);

    /// <summary>Resolves an explicit provider selector without contacting a server. Numeric aliases retain Azure Boards semantics.</summary>
    /// <param name="text">An ADO alias, github#number, or repository-qualified github:owner/repo#number.</param>
    /// <param name="repository">The imported history's GitHub origin, required for a short GitHub selector.</param>
    /// <returns>A positive provider-aware identity with normalized GitHub repository scope.</returns>
    /// <exception cref="ArgumentException">The selector is invalid or a short GitHub selector has no repository scope.</exception>
    public static RequirementId Parse(string text, string? repository = null)
    {
        var qualified = Regex.Match(text, @"^github:([^#]+)#([1-9][0-9]*)$");
        if (qualified.Success && int.TryParse(qualified.Groups[2].Value, out var n) && GithubRepository(qualified.Groups[1].Value) is { } scope)
            return new(n, scope);
        var github = Regex.Match(text, @"^github#([1-9][0-9]*)$");
        if (github.Success && int.TryParse(github.Groups[1].Value, out n) && GithubRepository(repository) is { } repo)
            return new(n, repo);
        var ado = Regex.Match(text, @"^(?:AB#|WI-|#)?([1-9][0-9]*)$", RegexOptions.IgnoreCase);
        if (ado.Success && int.TryParse(ado.Groups[1].Value, out n)) return new(n);
        throw new ArgumentException("--work-item must be a positive Azure Boards id (123, AB#123) or github#123 with a GitHub repository scope");
    }

    /// <summary>Normalizes a github.com origin or owner/repository name; unsupported hosts and credential-bearing URLs return null.</summary>
    /// <param name="value">A Git origin URL, SCP-style origin, or owner/repository name.</param>
    /// <returns>The lowercase owner/repository identity, or null when it cannot be established.</returns>
    public static string? GithubRepository(string? value)
    {
        if (value is null) return null;
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) value = value[15..];
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https" or "ssh") || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0
                || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
            value = uri.AbsolutePath.Trim('/');
        }
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$")
            && value.Split('/')[1] is not ("." or "..")
            ? value.ToLowerInvariant() : null;
    }

    /// <summary>Reads a GitHub issue's repository and number from its canonical HTTPS URL without treating PR URLs as issues.</summary>
    /// <param name="url">The issue URL retained in a provider link or exported issue details.</param>
    /// <returns>A repository-qualified GitHub issue identity.</returns>
    /// <exception cref="ArgumentException">The URL is not a supported github.com issue URL.</exception>
    public static RequirementId FromIssueUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && Regex.Match(uri.AbsolutePath, @"^/([^/]+/[^/]+)/issues/([1-9][0-9]*)/?$") is { Success: true } m
            && GithubRepository(m.Groups[1].Value) is { } repo && int.TryParse(m.Groups[2].Value, out var number))
            return new(number, repo);
        throw new ArgumentException("GitHub issue URL must be https://github.com/<owner>/<repo>/issues/<number> (pull requests are not issues)");
    }

    public sealed class Converter : JsonConverter<RequirementId>
    {
        /// <summary>Reads legacy numeric ADO IDs or repository-qualified GitHub IDs from a saved index.</summary>
        /// <param name="reader">The reader positioned at an identity value.</param>
        /// <param name="type">The requested identity type.</param>
        /// <param name="options">The enclosing index's JSON options.</param>
        /// <returns>A validated positive requirement identity.</returns>
        /// <exception cref="JsonException">The JSON value is not a valid requirement identity.</exception>
        public override RequirementId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && n > 0) return new(n);
            if (reader.TokenType == JsonTokenType.String)
                try { return Parse(reader.GetString()!); }
                catch (ArgumentException e) { throw new JsonException(e.Message, e); }
            throw new JsonException("requirement id must be a positive number or a repository-qualified GitHub identity");
        }
        /// <summary>Writes ADO IDs as numbers and GitHub IDs as repository-qualified strings, preserving legacy ADO JSON.</summary>
        /// <param name="writer">The output JSON writer.</param>
        /// <param name="value">The requirement identity to serialize.</param>
        /// <param name="options">The enclosing index's JSON options.</param>
        public override void Write(Utf8JsonWriter writer, RequirementId value, JsonSerializerOptions options)
        {
            if (value.IsGithub) writer.WriteStringValue(value.ToString()); else writer.WriteNumberValue(value.Number);
        }
    }
}
