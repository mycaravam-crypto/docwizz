using System.Text.RegularExpressions;

namespace DocWizz.Tests.Unit;

// CLI.md and the README against the command table (Cli.Commands): a command or option added, removed or moved to another
// command without updating the docs fails here, so the reference can't drift from what the parser accepts.
public class CliDocsTests
{
    static readonly string Root = FindRoot();
    static readonly string CliMd = File.ReadAllText(Path.Combine(Root, "CLI.md"));
    static readonly string Readme = File.ReadAllText(Path.Combine(Root, "README.md"));

    // Setup's stages as setup prints them (Journey/SetupJourneyTests checks the order on a real run).
    public static readonly string[] SetupStages = ["scan", "config", "analyze", "architecture", "generate", "check"];

    // Commands a new user never needs: they must be marked as such wherever commands are listed.
    static readonly string[] Advanced = [.. Cli.Commands.Where(c => c.Value.Use is "manual setup" or "advanced, debugging").Select(c => c.Key)];

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CLI.md"))) return dir.FullName;
        throw new InvalidOperationException("CLI.md not found above the test binary");
    }

    // A `## heading` section, up to the next heading of the same level.
    static string Section(string markdown, string heading)
    {
        var m = Regex.Match(markdown, $@"(?ms)^{Regex.Escape(heading)}\s*$(.*?)(?=^{new string('#', heading.TakeWhile(c => c == '#').Count())} |\z)");
        Assert.True(m.Success, $"no section {heading}");
        return m.Groups[1].Value;
    }

    // Table rows as cells; an escaped `\|` stays inside its cell.
    static List<string[]> Rows(string section) => [.. section.Split('\n')
        .Where(l => l.StartsWith('|') && !l.StartsWith("|---"))
        .Select(l => l.Replace(@"\|", "\u0001").Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("\u0001", "|")).ToArray())
        .Skip(1)];   // the header

    [Fact]
    public void Every_command_is_in_the_command_reference_and_nothing_else()
    {
        var documented = Rows(Section(CliMd, "## Commands")).Select(r => r[0].Trim('`')).Where(c => c != "help").ToList();
        Assert.Equal(Cli.Commands.Keys.Order(), documented.Order());
        foreach (var (name, c) in Cli.Commands)
        {
            var row = Rows(Section(CliMd, "## Commands")).Single(r => r[0] == $"`{name}`");
            Assert.StartsWith($"`docwizz {c.Syntax.Split(' ')[0]}", row[1]);
            Assert.All(c.Accepts, o => Assert.True(row[1].Contains($"--{o}"), $"CLI.md syntax of {name} doesn't show --{o}"));
        }
    }

    [Fact]
    public void Every_command_is_at_a_glance_and_in_the_readme()
    {
        var glance = Rows(Section(CliMd, "## At a glance")).Select(r => r[0].Trim('`')).ToHashSet();
        var readme = Section(Readme, "## Commands");
        foreach (var name in Cli.Commands.Keys)
        {
            Assert.Contains(name, glance);
            Assert.True(readme.Contains($"`docwizz {name} "), $"README Commands doesn't list {name}");
        }
    }

    // The Options table's "Applies to" column is the compatibility matrix in prose: it must say exactly what parses.
    [Fact]
    public void Every_option_is_documented_with_the_commands_that_accept_it()
    {
        var applies = new Dictionary<string, string>();
        foreach (var row in Rows(Section(CliMd, "## Options")))
            foreach (var m in Regex.Matches(row[0], @"(?<![\w-])--?([a-z]+)").Cast<System.Text.RegularExpressions.Match>())
                applies[m.Groups[1].Value == "h" ? "help" : m.Groups[1].Value] = row[2];

        foreach (var option in Cli.Flags.Concat(Cli.Valued.Keys))
        {
            Assert.True(applies.TryGetValue(option, out var documented), $"--{option} is not in CLI.md Options");
            var expected = Cli.Global.Contains(option) ? ["all"]
                : Cli.Commands.Where(c => c.Value.Accepts.Contains(option)).Select(c => c.Key).Order().ToArray();
            Assert.True(expected.SequenceEqual(documented!.Split(',', StringSplitOptions.TrimEntries).Order()),
                $"CLI.md says --{option} applies to `{documented}`; the parser accepts it on {string.Join(", ", expected)}");
        }
        Assert.All(applies.Keys, o => Assert.True(Cli.Flags.Contains(o) || Cli.Valued.ContainsKey(o), $"CLI.md documents --{o}, which doesn't exist"));
    }

    [Fact]
    public void Advanced_and_debugging_commands_are_marked_off_the_default_path()
    {
        Assert.Equal(["init", "scan"], Advanced.Order());
        foreach (var row in Rows(Section(CliMd, "## At a glance")).Where(r => Cli.Commands.ContainsKey(r[0].Trim('`'))))
        {
            var advanced = Advanced.Contains(row[0].Trim('`'));
            Assert.True(advanced == (row[3] == "no"), $"At a glance: {row[0]} is {(advanced ? "" : "not ")}advanced but its default path is `{row[3]}`");
        }
        var commands = Section(CliMd, "## Commands");
        Assert.All(Advanced, c => Assert.Matches($@"\| `{c}` \|.*\| (manual setup|advanced) \|", commands));
    }

    [Fact]
    public void The_first_setup_workflow_is_documented()
    {
        var setup = Section(CliMd, "### 1. First setup");
        Assert.Contains("docwizz setup .", setup);
        Assert.Equal(SetupStages, Rows(setup).Where(r => int.TryParse(r[0], out _)).Select(r => r[1]));   // the stage table, in run order
        Assert.All(["SUCCESS", "SUCCESS_WITH_FINDINGS", "FAILED"], s => Assert.Contains($"| `{s}` |", setup));
        Assert.Contains("--force", setup);
        Assert.Contains("docwizz setup .", Section(Readme, "## Quick start"));
        Assert.Contains("start here", CliMd);
    }

    [Fact]
    public void The_ci_workflow_is_documented()
    {
        var ci = Section(CliMd, "### 4. Quality gates and CI");
        Assert.Contains("docwizz check . --since", ci);
        Assert.Contains("uses: mycaravam-crypto/docwizz@", ci);   // the GitHub Action in this repository
        Assert.True(File.Exists(Path.Combine(Root, "action.yml")));
        Assert.Contains("--since", Section(Readme, "### Gate pull requests"));
    }

    [Fact]
    public void Exit_codes_are_documented_for_every_command()
    {
        var documented = Rows(Section(CliMd, "## Exit codes")).SelectMany(r => Regex.Matches(r[0], "`([a-z]+)`").Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.All(Cli.Commands.Keys, c => Assert.Contains(c, documented));
        Assert.Contains("exits **2**", Section(CliMd, "## Exit codes"));
    }
}
