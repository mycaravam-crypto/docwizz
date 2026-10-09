namespace DocWizz.Tests.Unit;

public class CliProgressTests
{
    [Fact]
    public void Redirected_progress_has_stable_lines_without_terminal_control_codes()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 3, interactive: false))
        {
            bar.Advance("scan");
            bar.Advance("analyze");
            bar.Advance("write");
        }
        var text = output.ToString();
        Assert.Contains("[========================] 3/3 write", text);
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("\u001b", text);
    }

    [Fact]
    public void Disabled_progress_keeps_output_empty()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 2, interactive: false, enabled: false))
            bar.Advance("scan");
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Terminal_progress_updates_one_line_and_finishes_with_newline()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 2, interactive: true))
        {
            bar.Advance("scan");
            bar.Advance("write");
        }
        Assert.Contains("\r\u001b[2Kdocwizz [========================] 2/2 write", output.ToString());
        Assert.EndsWith(Environment.NewLine, output.ToString());
    }
}
