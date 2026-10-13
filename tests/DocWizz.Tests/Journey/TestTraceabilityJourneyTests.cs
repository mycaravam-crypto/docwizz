using System.Diagnostics;

namespace DocWizz.Tests.Journey;

// #126: test code that only constructs a type (`new Order(..)`) is a test of that type, and check --since run on the
// repository root (tests in scope) reports it; tests outside the scanned path link nothing.
public class TestTraceabilityJourneyTests
{
    static void Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(20)), "git did not finish: " + string.Join(" ", arguments));
        Assert.True(process.ExitCode == 0, $"git {string.Join(" ", arguments)} exited {process.ExitCode}: {stderr.Result}");
    }

    static void Commit(string root, string message)
    {
        Git(root, "add", "-A");
        Git(root, "-c", "user.name=DocWizz", "-c", "user.email=docwizz@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", message);
    }

    [Fact]
    public void A_test_that_constructs_a_type_links_it_and_only_a_scope_with_the_tests_sees_the_link()
    {
        using var repo = new Sources(
            ("docwizz.yaml", "tests: [\"tests/*\"]\n"),
            ("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("src/Shop/Base.cs", """
                /// <summary>Placeholder so the base commit has code.</summary>
                public class Base { }
                """));
        Git(repo.Root, "init", "-q");
        Commit(repo.Root, "base");

        File.WriteAllText(Path.Combine(repo.Root, "src/Shop/Order.cs"), """
            /// <summary>An order line.</summary>
            public record Order(int Quantity);
            /// <summary>Not exercised by any test.</summary>
            public record Invoice(int Number);
            """);
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "tests/OrderTests.cs"), """
            public class OrderTests
            {
                public void Keeps_the_quantity() { var order = new Order(2); }
            }
            """);
        Commit(repo.Root, "order");

        var root = Docwizz.Run(cwd: repo.Root, env: null, args: ["check", ".", "--since", "HEAD~1"]);
        Assert.True(root.Exit == 0, root.ToString());
        Assert.Matches(@"Test traceability \(1 of \d+ changed symbols linked\)", root.Out);
        Assert.Contains("+ Order  ← OrderTests.Keeps_the_quantity", root.Out);
        Assert.DoesNotContain("Invoice  ←", root.Out);

        // Scanning src/ alone leaves tests/ out of scope: nothing is linked, and the report says so rather than guessing.
        var src = Docwizz.Run(cwd: repo.Root, env: null, args: ["check", "src", "--since", "HEAD~1"]);
        Assert.True(src.Exit == 0, src.ToString());
        Assert.Matches(@"Test traceability \(0 of \d+ changed symbols linked\)", src.Out);
    }
}
