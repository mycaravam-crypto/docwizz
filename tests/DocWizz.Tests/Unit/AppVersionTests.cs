namespace DocWizz.Tests.Unit;

// The version line: the csproj's <Version>, the commit the SDK appends, the runtime and platform.
public class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0+e4320bd5c0ffee1234567890abcdef12345678", "0.1.0", "e4320bd")]
    [InlineData("1.2.0-rc.1+e4320bd5c0ffee", "1.2.0-rc.1", "e4320bd")]
    [InlineData("0.1.0", "0.1.0", null)]
    [InlineData("0.1.0+local", "0.1.0", null)]   // build metadata that isn't a commit
    public void Parses_number_and_commit(string informational, string number, string? commit) =>
        Assert.Equal((number, commit), AppVersion.Parse(informational));

    [Fact]
    public void Describes_the_build_in_one_line()
    {
        Assert.Equal("docwizz 0.1.0 (commit e4320bd, .NET 10.0.0, linux-x64)", AppVersion.Describe("0.1.0+e4320bd5c0ffee", "10.0.0", "linux-x64"));
        Assert.Equal("docwizz 0.1.0 (.NET 10.0.0, win-x64)", AppVersion.Describe("0.1.0", "10.0.0", "win-x64"));
    }

    [Fact]
    public void The_running_build_reports_the_project_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppVersion.Number);
        Assert.StartsWith($"docwizz {AppVersion.Number} (", AppVersion.Line);
    }
}
