using System.Text.RegularExpressions;

internal static partial class Slug
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string value) => Pattern().IsMatch(value);
}
