using System.Text.RegularExpressions;

namespace DocWizz.Tests.Journey;

// The CLI as a process: exit codes, what goes to stdout vs stderr, short usage on mistakes, never a stack trace.
// Which combinations parse is specified in Unit/CliTests; these check what a user actually sees.
public class CliContractTests
{
    [Fact]
    public void No_command_says_how_to_start_on_stderr()
    {
        var r = Docwizz.Run();
        Assert.Equal(1, r.Exit);
        Assert.Equal("", r.Out);
        Assert.Contains("docwizz setup .", r.Err);
        Assert.Contains("docwizz help", r.Err);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("help")]
    [InlineData("-h")]
    public void Help_is_the_full_reference_on_stdout(string arg)
    {
        var r = Docwizz.Run(arg);
        Assert.Equal(0, r.Exit);
        Assert.Equal("", r.Err);
        Assert.StartsWith("usage: docwizz <command>", r.Out);
        Assert.All(Cli.Commands.Keys, c => Assert.Contains($"docwizz {c} ", r.Out));
        Assert.All(Cli.Flags.Concat(Cli.Valued.Keys), o => Assert.Contains($"--{o}", r.Out));
        // Commands a new user doesn't need come last, under their own heading.
        var advanced = r.Out[r.Out.IndexOf("manual setup and debugging:", StringComparison.Ordinal)..];
        Assert.Contains("docwizz init ", advanced);
        Assert.Contains("docwizz scan ", advanced);
        Assert.True(r.Out.IndexOf("docwizz setup ", StringComparison.Ordinal) < r.Out.IndexOf("docwizz generate ", StringComparison.Ordinal));
    }

    // The full reference is written by hand, so check it against the command table (Cli.Commands): each command's lines
    // (`docwizz <command> ...` and the option lines under it) show exactly what that command accepts, and the shared
    // options name exactly the commands that accept them.
    [Fact]
    public void Help_reference_shows_each_command_with_the_options_it_accepts()
    {
        var r = Docwizz.Run("help");
        Assert.Equal(0, r.Exit);
        string[] shared = ["profile", "format"];
        var shown = new Dictionary<string, HashSet<string>>();
        var listedFor = new Dictionary<string, string[]>();
        var section = "";
        string? command = null;
        foreach (var line in r.Out.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.Length > 0 && line[0] != ' ') section = line;
            var commandLine = Regex.Match(line, @"^  docwizz (\S+)");
            if (commandLine.Success) command = commandLine.Groups[1].Value;
            else if (!line.StartsWith("    ")) command = null;   // an option line belongs to the command above it
            if (section == "options:" && Regex.Match(line, @"^  --([a-z]+)") is { Success: true } o && shared.Contains(o.Groups[1].Value))
                listedFor[o.Groups[1].Value] = [.. Regex.Match(line, @"(?:[a-z-]+/)+[a-z-]+").Value.Split('/')];
            if (command is null || !Cli.Commands.ContainsKey(command)) continue;
            foreach (var m in Regex.Matches(line, @"--([a-z]+(?:-[a-z]+)*)").Cast<System.Text.RegularExpressions.Match>())
                (shown.TryGetValue(command, out var s) ? s : shown[command] = []).Add(m.Groups[1].Value);
        }

        foreach (var (name, c) in Cli.Commands)
        {
            var options = shown.GetValueOrDefault(name) ?? [];
            Assert.All(c.Accepts.Except(shared), o => Assert.True(options.Contains(o), $"`docwizz help` doesn't show --{o} for {name}"));
            Assert.All(options, o => Assert.True(c.Accepts.Contains(o), $"`docwizz help` shows --{o} for {name}, which doesn't accept it"));
        }
        foreach (var option in shared)
        {
            Assert.True(listedFor.TryGetValue(option, out var listed), $"`docwizz help` options don't say which commands take --{option}");
            Assert.Equal(Cli.Commands.Where(c => c.Value.Accepts.Contains(option)).Select(c => c.Key).Order(), listed!.Order());
        }
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("version")]
    [InlineData("check . --version")]
    public void Version_is_one_line_on_stdout(string args)
    {
        var r = Docwizz.Run(args.Split(' '));
        Assert.Equal(0, r.Exit);
        Assert.Equal("", r.Err);
        Assert.Matches(@"^docwizz \d+\.\d+\.\d+\S* \((commit [0-9a-f]{7}, )?\.NET \S+, \S+\)\r?\n$", r.Out);
    }

    [Theory]
    [InlineData("setup", "docwizz setup [dir]")]
    [InlineData("generate", "docwizz generate <dir> [out]")]
    [InlineData("check", "docwizz check <dir> [--since <ref>]")]
    public void Command_help_shows_syntax_example_and_options(string command, string syntax)
    {
        foreach (var args in new[] { new[] { command, "--help" }, ["help", command] })
        {
            var r = Docwizz.Run(args);
            Assert.Equal(0, r.Exit);
            Assert.Equal("", r.Err);
            Assert.StartsWith(syntax, r.Out);
            Assert.Contains("example:", r.Out);
            Assert.All(Cli.Commands[command].Accepts, o => Assert.Contains($"--{o}", r.Out));
        }
    }

    [Theory]
    [InlineData("--unknown", "unknown option --unknown", "Run 'docwizz help' for all commands and options.")]
    [InlineData("setup . --unknown", "unknown option --unknown", "Run 'docwizz help setup' for its options.")]
    [InlineData("--format", "--format needs a value: console or json", "Run 'docwizz help' for all commands and options.")]
    [InlineData("--format xml", "unknown format xml: use console or json", "Run 'docwizz help' for all commands and options.")]
    [InlineData("analyze . --html", "--html doesn't apply to analyze; it applies to setup, generate", "Run 'docwizz help analyze' for its options.")]
    [InlineData("generate . --force", "--force doesn't apply to generate; it applies to setup", "Run 'docwizz help generate' for its options.")]
    [InlineData("remediate . --package Newtonsoft.Json", "--package and --to go together", "Run 'docwizz help remediate' for its options.")]
    [InlineData("chek .", "unknown command chek; did you mean check?", "Run 'docwizz help' for all commands and options.")]
    [InlineData("help nope", "unknown command nope", "Run 'docwizz help' for all commands and options.")]
    public void Mistakes_get_one_actionable_line_and_a_pointer_to_help(string args, string error, string hint)
    {
        var r = Docwizz.Run(args.Split(' '));
        Assert.Equal(1, r.Exit);
        Assert.Equal("", r.Out);
        var lines = r.Err.TrimEnd().Split('\n');
        Assert.Equal(2, lines.Length);   // concise: the problem and where to look, not the full reference
        Assert.StartsWith(error, lines[0]);
        Assert.Equal(hint, lines[1].TrimEnd());
        Docwizz.NoStackTrace(r);
    }

    [Fact]
    public void A_missing_directory_is_a_message_not_a_crash()
    {
        var r = Docwizz.Run("analyze", Path.Combine(Path.GetTempPath(), "docwizz-no-such-dir"));
        Assert.Equal(1, r.Exit);
        Assert.StartsWith("not a directory:", r.Err);
        Docwizz.NoStackTrace(r);
    }

    [Fact]
    public void A_broken_config_names_the_file_without_a_stack_trace()
    {
        using var repo = Repos.ConsoleApp();
        File.WriteAllText(Path.Combine(repo.Root, "docwizz.yaml"), "check: [oops\n");
        var r = Docwizz.Run("analyze", repo.Root);
        Assert.Equal(1, r.Exit);
        Assert.Contains("docwizz.yaml", r.Err);
        Assert.DoesNotContain("docwizz help", r.Err);   // it's the file that needs fixing, not the options
        Docwizz.NoStackTrace(r);
    }

    [Fact]
    public void An_internal_error_is_one_line_and_exit_2_not_a_stack_trace()
    {
        using var repo = Repos.ConsoleApp();
        var r = Docwizz.Run("scan", repo.Root, Path.Combine(repo.Root, "missing", "dir", "model.json"));
        Assert.Equal(2, r.Exit);
        Assert.StartsWith("docwizz: error: ", r.Err);
        Assert.Contains("not a finding about the repository", r.Err);
        Docwizz.NoStackTrace(r);
    }

    [Fact]
    public void Json_output_is_only_json_on_stdout()
    {
        using var repo = Repos.ConsoleApp();
        var r = Docwizz.Run("analyze", repo.Root, "--format", "json");
        Assert.Equal(0, r.Exit);
        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);   // throws if anything else is mixed in
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }
    [Fact]
    public void Ado_generation_options_require_explicit_complete_selection()
    {
        Assert.Contains("--ado-project and --ado-area", Cli.Parse(["generate", ".", "--ado-project", "Shop"]).Error);
        Assert.Contains("--history-since requires", Cli.Parse(["generate", ".", "--history-since", "v1.0.0"]).Error);
        Assert.Null(Cli.Parse(["generate", ".", "--html", "--ado-project", "Shop", "--ado-area", "Shop\\Orders", "--history-since", "v1.0.0"]).Error);
        Assert.Null(Cli.Parse(["generate", ".", "--html"]).Error);
    }

}
