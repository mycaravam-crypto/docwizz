namespace DocWizz.Tests.Journey;

// Regression: on Windows, CSharpScanner once emitted '\' in Node.File while test-source
// classification used '/' paths. Test helpers then appeared as undocumented product code.
public class WindowsTestSourcePathsJourneyTests
{
    [Fact]
    public void Analyze_excludes_test_helpers_from_documentation_findings()
    {
        // Deliberately omit a test .csproj: the tests/ path glob must classify this source
        // without relying on project metadata to hide the Windows path mismatch.
        using var repo = new Sources(
            ("src/Shop/Shop.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """),
            ("src/Shop/Calculator.cs", """
                namespace Shop;
                /// <summary>Performs arithmetic for the shop.</summary>
                public static class Calculator
                {
                    /// <summary>Adds two numbers.</summary>
                    public static int Add(int left, int right) => left + right;
                }
                """),
            ("tests/Connector.Integration.Tests/FakeServiceNow.cs", """
                namespace Connector.Integration.Tests;
                public class FakeServiceNow
                {
                    public int UndocumentedHelper(int value) => value + 1;
                }
                """)
        );

        var result = Docwizz.Run("analyze", repo.Root, "--format", "json");

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain("FakeServiceNow", result.Out);
        Assert.DoesNotContain("UndocumentedHelper", result.Out);
        Docwizz.NoStackTrace(result);
    }
}
