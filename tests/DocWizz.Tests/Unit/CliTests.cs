namespace DocWizz.Tests.Unit;

// The command line contract, in-process: what parses to what, and which command/option combinations are accepted.
// The matrix below is the executable specification for option compatibility; CLI.md documents the same table.
public class CliTests
{
    static Cli.Request Parse(string args) => Cli.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Parses_command_directory_flags_and_values()
    {
        var r = Parse("generate . out --html --profile api");
        Assert.Null(r.Error);
        Assert.Equal("generate", r.Command);
        Assert.Equal([".", "out"], r.Positional.Skip(1));
        Assert.Equal("", r.Options["html"]);
        Assert.Equal("api", r.Options["profile"]);
        Assert.Equal("", Parse("check . -h").Options["help"]);
    }

    [Theory]
    // allowed
    [InlineData("setup .", null)]
    [InlineData("setup . --force --html --ai --profile api", null)]
    [InlineData("generate . --html", null)]
    [InlineData("generate . --ai", null)]
    [InlineData("generate . --ai --html", null)]
    [InlineData("check . --since origin/main", null)]
    [InlineData("check . --since origin/main --format json", null)]
    [InlineData("analyze . --format json --profile vue", null)]
    [InlineData("architecture . --format json", null)]
    [InlineData("diff HEAD~1 HEAD --format json", null)]
    [InlineData("remediate . --validate", null)]
    [InlineData("remediate . --package Newtonsoft.Json --to 13.0.3 --format json", null)]
    [InlineData("remediate . --since main", null)]
    [InlineData("scan . --timings", null)]
    [InlineData("init . --help", null)]
    [InlineData("", null)]
    [InlineData("help generate", null)]
    // options a command doesn't use
    [InlineData("analyze . --html", "--html doesn't apply to analyze; it applies to setup, generate")]
    [InlineData("scan . --ai", "--ai doesn't apply to scan; it applies to setup, generate")]
    [InlineData("generate . --format json", "--format doesn't apply to generate; it applies to check, analyze, architecture, diff, remediate")]
    [InlineData("generate . --force", "--force doesn't apply to generate; it applies to setup")]
    [InlineData("check . --validate", "--validate doesn't apply to check; it applies to remediate")]
    [InlineData("analyze . --since main", "--since doesn't apply to analyze; it applies to check, remediate")]
    [InlineData("setup . --format json", "--format doesn't apply to setup; it applies to check, analyze, architecture, diff, remediate")]
    [InlineData("init . --profile api", "--profile doesn't apply to init; it applies to setup, generate, check, analyze, architecture, diff")]
    // conflicts and bad values
    [InlineData("remediate . --package Newtonsoft.Json", "--package and --to go together: --package <name> --to <version>")]
    [InlineData("remediate . --to 13.0.3", "--package and --to go together: --package <name> --to <version>")]
    [InlineData("--format", "--format needs a value: console or json")]
    [InlineData("check . --since", "--since needs a value: a git ref, e.g. origin/main")]
    [InlineData("check . --format --since main", "--format needs a value: console or json")]
    [InlineData("--format xml", "unknown format xml: use console or json")]
    [InlineData("analyze . --format xml", "unknown format xml: use console or json")]
    // unknown names, with a suggestion for a likely typo
    [InlineData("--unknown", "unknown option --unknown")]
    [InlineData("setup . --unknown", "unknown option --unknown")]
    [InlineData("check . --fromat json", "unknown option --fromat; did you mean --format?")]
    [InlineData("check . --format=json", "unknown option --format=json; did you mean --format?")]
    [InlineData("genrate .", "unknown command genrate; did you mean generate?")]
    [InlineData("frobnicate .", "unknown command frobnicate")]
    public void Command_and_option_compatibility(string args, string? error) => Assert.Equal(error, Parse(args).Error);

    [Fact]
    public void Every_option_is_accepted_by_some_command_and_every_command_has_global_options()
    {
        var accepted = Cli.Commands.Values.SelectMany(c => c.Accepts).Concat(Cli.Global).ToHashSet();
        Assert.All(Cli.Flags.Concat(Cli.Valued.Keys), o => Assert.Contains(o, accepted));
        foreach (var (name, _) in Cli.Commands)
            Assert.All(Cli.Global, o => Assert.Null(Parse($"{name} . --{o}").Error));
    }

    [Fact]
    public void Help_lists_every_option_a_command_accepts()
    {
        foreach (var (name, c) in Cli.Commands)
            Assert.All(c.Accepts, o => Assert.True(c.Options.Contains($"--{o}"), $"`docwizz help {name}` doesn't mention --{o}"));
    }

    [Fact]
    public void The_first_error_wins_and_the_command_is_still_known()
    {
        var r = Parse("analyze . --html --bogus");
        Assert.Equal("analyze", r.Command);
        Assert.Equal("unknown option --bogus", r.Error);   // an unknown name is reported before a misplaced one
    }
}
