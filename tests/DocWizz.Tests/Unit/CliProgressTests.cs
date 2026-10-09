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
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        // Ordinal: culture-aware comparison ignores control characters and "finds" ESC anywhere.
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
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
    public void Terminal_progress_finishes_each_stage_with_newline()
    {
        var output = new StringWriter();
        using (var bar = new CliProgress(output, 2, interactive: true))
        {
            bar.Advance("scan");
            bar.Advance("write");
        }
        Assert.Contains("docwizz [========================] 2/2 write", output.ToString());
        Assert.DoesNotContain("\r", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, output.ToString());
    }
}
