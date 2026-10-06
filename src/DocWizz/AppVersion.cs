using System.Reflection;
using System.Runtime.InteropServices;

// What `docwizz --version` prints and what `setup` writes into docwizz.yaml. The number is `<Version>` in DocWizz.csproj
// (SemVer); the SDK appends the git commit it was built from (`0.1.0+e4320bd…`), which is how a bug report or a CI log
// names the exact build. A build outside git has no commit and says nothing about one.
static class AppVersion
{
    static readonly string Informational = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string Number => Parse(Informational).Number;

    public static string Line => Describe(Informational, Environment.Version.ToString(), RuntimeInformation.RuntimeIdentifier);

    // `0.1.0+e4320bd5…` → (0.1.0, e4320bd); build metadata that isn't a commit hash is not reported as one.
    public static (string Number, string? Commit) Parse(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational, null);
        var meta = informational[(plus + 1)..];
        var commit = meta.Length >= 7 && meta.All(Uri.IsHexDigit) ? meta[..7] : null;
        return (informational[..plus], commit);
    }

    // `docwizz 0.1.0 (commit e4320bd, .NET 10.0.0, linux-x64)`
    public static string Describe(string informational, string runtime, string rid)
    {
        var (number, commit) = Parse(informational);
        return $"docwizz {number} ({(commit is null ? "" : $"commit {commit}, ")}.NET {runtime}, {rid})";
    }
}
