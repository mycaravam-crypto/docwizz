namespace DocWizz.Tests.Unit;

// Scope filtering and project navigation share this rule; these cases are the ones they used to implement separately.
public class ProjectOwnershipTests
{
    static readonly Func<string, string?> Owner = ProjectOwnership.Resolver([
        new("proj:App.csproj", "App.csproj", "dotnet"),
        new("proj:package.json", "package.json", "npm"),
        new("proj:src/Api/Api.csproj", "src/Api/Api.csproj", "dotnet"),
        new("proj:src/Api/Nested/Nested.csproj", "src/Api/Nested/Nested.csproj", "dotnet"),
    ]);

    [Theory]
    [InlineData("src/Api/Controllers/X.cs", "proj:src/Api/Api.csproj")]
    [InlineData("src/Api/Nested/Y.cs", "proj:src/Api/Nested/Nested.csproj")]   // the nearest manifest wins
    [InlineData("src/Api/wwwroot/app.ts", "proj:src/Api/Api.csproj")]          // a folder owns every file below it
    [InlineData("Tools/Build.cs", "proj:App.csproj")]                          // a root manifest: its language only
    [InlineData("web/main.ts", "proj:package.json")]
    [InlineData("App.csproj", "proj:App.csproj")]
    [InlineData("Dockerfile", null)]                                           // never a root manifest's sibling
    [InlineData("db/schema.sql", null)]
    public void Nearest_manifest_owns_and_a_root_manifest_owns_only_its_language(string file, string? owner) =>
        Assert.Equal(owner, Owner(file));
}
